# Bot sim rollback / lane cost test

Measures the cost of the bot module's 3-way-split sim (sim-pump + Think + send) and its real-player
rollback, so we can decide **how many rewind lanes** to run (3 / 4 / 5) and whether an **auto-tune** of
the middle lanes is worth building. Two costs are in tension:

- **Per-lane base cost** — every snapshot lane is a full world ticked every sim-tick, so N lanes ≈ N× the
  base sim. More lanes = more steady cost even when nothing is late.
- **Roll-forward cost** — a late real-player packet rebuilds from the nearest lane older than it. Fewer /
  worse-placed lanes = more ticks re-simulated per rebuild.

## The metric

Each arena with a live sim logs once a second (temporary `REWIND` diagnostic):

```
REWIND rebuilds/s=<n> refused/s=<n> maxLate=<t>t (<ms>ms) lanes=[<delays>] pump=<n>x<ticks>t/<avg>us avg/<max>us max behind=<t>t skipped=<t>t bricks=<n> think=<t>t send=<t>t bots=<n>
```

- **`pump=<n>x<ticks>t/<avg>us avg/<max>us max`** — the headline: pumps in the last second, the average
  sim ticks each ran, and how long the pump body (drain + think + sim + events + send) took, averaged and
  worst-case. `n x avg` is the arena-thread time the sim used that second.
- **`behind` / `skipped`** — whether the sim is keeping up with real time. `behind` is how many ticks the
  sim owes the wall clock right now (normally 0); `skipped` is the total it has written off because it
  could not keep up. If `skipped` is climbing the sim is running slow, and the server also logs
  `The bot sim cannot keep up with real time`.
- **`refused/s`** — commands the sim REFUSED (its command buffer was full). **Must be 0** — otherwise bots
  are not steering and player input is being lost. The server also logs `The bot sim refused N command(s)`
  as a warning; raise `Bots:MaxCommandsPerTick`.
- **`rebuilds/s`** — how many pumps rewound the lanes (rebuilds are batched: at most one per pump).
- **`lanes=[…]`** — the lane delays in force right now (they move when the auto-tune re-seats them).
- **`maxLate`** — the furthest behind the sim's own current tick a real-player packet landed. 0 means every
  packet was on time (the sim had not reached its tick yet), so nothing was rewound.
- **`bricks`** — bricks standing in the sim right now. Should follow what the server's brick module has placed.
- **`bots`** — sim workload (entities).

With `Bots:LaneStatsInterval > 0` you also get `LANESTATS … cdf:` — the distribution of that lateness, for
lane placement. The heavy per-bot `CBT/DMG/NAV/MAP` dump auto-suppresses above 8 bots so it never skews a
run.

## What was wrong before 2026-10-09 (fixed — and why older numbers are void)

1. **The sim was silently refusing almost all its input.** The world was built with the library's default
   pending-command buffer, 256 per lane. Every command is queued on every lane that has not reached its
   tick yet, so the deepest lane holds each one for the whole horizon; bots are steered with up to 2
   commands per bot per tick, so at a 70-tick horizon it was full with 2-3 bots. A full buffer refuses
   everything — bot steering and real-player packets alike — and nothing reported it. Measured with
   40 bots + 100 clients: 1500-2200 refused a second against 0-9 rebuilds.
   *Fix:* the buffer is sized from `Bots:MaxCommandsPerTick` x the horizon; the library now reports every
   refused command to the host, and the module logs a warning when any are.
2. **Sim time was not tied to wall time.** Each pump advanced a fixed number of ticks, but the arena timer
   re-arms only after the pump returns, so pumps came every `SimPumpInterval + pump time`. A 12 ms pump on
   a 20 ms interval ran the sim at 0.6x real time; the heavy run below at 0.2x.
   *Fix:* each pump runs as many ticks as real time has moved on (up to 4x the pump size per pump, to
   repay a stall over a few pumps). If it falls more than half a second behind, the backlog is written off
   and a warning is logged.

**Every pump time recorded before these fixes is void** — it was measured on a sim applying a few percent
of its input, in slow motion. Re-measured below.

## Results after the fixes (2026-10-09)

Lanes start at `[0,25,50,70]`, 2 intermediate lanes, `LaneRetuneInterval = 1`, clients half at `--lag 50`
and half at `--lag 120`, localhost.

| build | load | sim speed | refused | lanes after re-seat | pump (per second) |
|---|---|---|---|---|---|
| Debug | 10 bots + 30 clients | 1.0x | 0 | `[0,16,48,70]`, held next window | 27 x 15.8 ms → 30 x 12.5 ms after the re-seat |
| Debug | 40 bots + 100 clients | ~0.85x | 0 | (no re-seat — see below) | 13 x 59-74 ms; warns it cannot keep up |
| Release | 40 bots + 100 clients | 1.0x | 0 | `[0,16,48,70]`, held next window | 30 x 12.9 ms → 30 x 12.8 ms (max ~24-28 ms) |

- In Release the heavy load runs in real time on roughly 40% of one core. In Debug it is about 15% short.
- The Release run showed no measurable pump saving from the re-seat (12.9 → 12.8 ms): at this load the
  pump is mostly forward simulation and sends, and only about half the pumps rewind at all.
- The Debug heavy run never re-seated: once the sim trails the wall clock by more than the players' lag,
  their packets are no longer late (the sim has not reached their tick yet), so nothing rewinds and the
  tuner sees nothing to judge by. That is correct behaviour, but it means lane tuning only happens on a
  sim that is keeping up.
- Compare the same heavy Debug load before the fixes: buffer at 256 → 1500-2200 refused/s; buffer raised
  but clock not fixed → sim at ~0.2x real time, pump 31-77 ms.

## Lane auto-tune

Unless `Bots:RewindLanes` pins them, the controller re-seats its intermediate lanes itself (physics
library, `ReplayController.EnableLaneAutoTune` — see the library's `INTEGRATION.md` §9.2.1). The server
logs where they start and every move:

```
Rollback lanes start at [0,25,50,70] ticks; re-seated every 10 min from observed lag.
Rollback lanes re-seated to [0,16,48,70] ticks (were [0,25,50,70]), from 1346 rewinds observed.
```

How it places them, in short: one lane just above each distinct group of lateness (observed max + margin
+ 1), lanes kept at least 1.5x apart so they cannot bunch on one crowd, and a lane with no group of its own
placed further out as cover — so a spike a little later than usual lands on a lane instead of rolling the
whole horizon. With two lanes, two crowds share one lane and the other is cover; with three, each crowd
gets its own.

Earlier runs, before the spacing/cover rules were tightened and before the clock fix (Debug, 10 bots +
30 clients, buffer raised by hand), are kept for what they show about laggy players:

| traffic | lanes | pump avg |
|---|---|---|
| two crowds | `[0,11,19,70]` | 8.5 ms |
| + 3 clients @ `--lag 400`, no lane above them → every one of their batches rolls 70 | `[0,11,19,70]` | **31-35 ms** (max 49-62) |
| same laggers caught by a lane at 47 (separate run) | `[0,18,47,70]` | **16.2 ms** (max ~38) |

Those runs also placed the same two-crowd traffic three different ways (`[11,19]`, `[16,20]`, `[18,47]`).
The new rules remove the bunched placements; the two runs since both gave `[0,16,48,70]`. The LoadClient
is still a poor stand-in for real players here — each process sends all its clients' packets in one burst
every 100 ms — so placement stability is worth confirming with real clients.

To watch it yourself set `LaneRetuneInterval = 1` (and `LaneStatsInterval = 1` for the distribution), run
the clients, and wait a minute.

## Knobs

**Server, per test arena** (`arenas/<name>/arena.conf`, `[Bots]`):
- `RewindLanes` — set explicitly per run to compare fixed lane sets, e.g. `0,70` / `0,35,70` /
  `0,15,30,50,70`; pinned lanes are never re-seated. Leave unset to auto-derive (deepest =
  `Lag:PingToSpec` + `RewindHorizonMargin`) and let the auto-tune place the rest.
- `IntermediateLanes` (0-3, default 2), `LaneRetuneInterval` (min, default 10, 0 = off),
  `LaneRetuneFlex` / `LaneRetuneMargin` (ticks, default 3) — the auto-tune.
- `MaxCommandsPerTick` (default 128) — sizes the sim's command buffer. About 2 per steering bot; raise it
  for big bot counts (200 bots needs ~450) or whenever `refused/s` is not 0.
- `SimPumpInterval`, `ThinkInterval`, `SendInterval` (ms) — the three cadences.
- `LaneStatsInterval` (min), `LaneStatsBucket` (ticks) — the staleness histogram.
- If injecting `--lag`, raise `Lag:PingToSpec` (and `PingToStartIgnoringWeapons`) in the test arena so the
  load clients aren't specced/ignored for lag mid-run.

**Load client** (`SS.LoadClient`, game mode — needs `EncryptionNull` loaded and unknown logins allowed):
- `--clients <M>` real players feeding late packets, `--arenas 0` (a `(Public 0)` arena the Bots module
  attaches to), `--lag <ms>` injected one-way latency (drives deep rollbacks), `--weapons <%>`,
  `--spread <px>`, `--duration <s>`, `--bricks <per minute>` (each client lays bricks on its own tile;
  the server log shows `Brick dropped` per brick and `Gave the bot sim N brick(s)` when the sim receives them).

## Procedure

1. In the test arena's `[Bots]` set the `RewindLanes` for this run (and raise `[Lag]` limits if lagging).
2. Start the server (Zone working dir).
3. Spawn the sim workload — a repeatable scatter of combat bots:
   ```
   SS.LoadClient --clients 1 --arenas 0 --command "loadtest 40 1 combat" --duration 180
   ```
   (or `?spawnbot combat` a few times by hand for a small run).
4. In a second LoadClient, add real players generating late packets:
   ```
   SS.LoadClient --protocol game --clients 50 --arenas 0 --lag 300 --weapons 15 --duration 150
   ```
5. Watch the server's `REWIND` line for that arena during the hold — record `pump avg/max`, `rebuilds/s`,
   `maxStale`.
6. Change `RewindLanes` (step 1), restart, repeat.

## Matrix

> The matrix below is the original plan for comparing FIXED lane sets. It has not been re-run since the
> fixes above; any results recorded for it earlier are void.

| Run | RewindLanes | Load clients | Reads |
|---|---|---|---|
| **A. Base cost** | `0,70` / `0,35,70` / `0,15,30,50,70` | none (bots only) | `pump avg` vs lane count → per-lane cost |
| **B. Roll-forward** | same three | `--clients 50 --lag 300` | `pump avg` delta vs A, `rebuilds/s`, `maxStale` → rollback cost & placement value |
| **C. Deep / horizon** | `0,15,30,50,70` | `--lag 500` then `--lag 700` | `maxStale` vs `horizon`, `LANESTATS beyond%` → horizon adequacy |

**Decision:** if per-lane base cost (A) dwarfs the roll-forward savings of extra lanes (B), run **fewer**
lanes (3–4) and the auto-tune middle-lane idea isn't worth it. If roll-forward dominates and good
placement cuts it sharply, 5 lanes and/or auto-tune pays. (C) sizes `RewindHorizonMargin`.

> All of `REWIND` / `LANESTATS` / the `--lag` flag are temporary test scaffolding — strip
> the server-side diagnostics once the lane decision is made.
