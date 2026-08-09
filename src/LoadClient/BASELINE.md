# Baseline measurements

Numbers taken before any scaling work, so that later changes can be judged against something.
Produced with the load client in this folder and read from the server's `Metrics` module.

Every figure here was re-measured after the code review that followed the first attempt at this
document. That matters, because the earlier numbers were wrong in two ways that are easy to
reintroduce:

- The metrics module sampled per thread processor time out of `/proc` *after* taking the window's
  allocation baseline, so its own string allocations were counted as the server's. That inflated the
  reported allocation rate from roughly zero to around 120 KB/s.
- The send thread iteration and overrun counters were printed but never written, so they read zero
  regardless of what the send thread was actually doing.

Machine: 32 cores, Linux (WSL2), .NET 10, **Debug** build. Debug matters — treat processor figures as
relative, not as capacity planning. Client and server share the machine.

Reproduce with:

```
dotnet run --project src/LoadClient -- --protocol game --clients <n> --arenas <list> --spread 2000 --duration 60
```

The server needs `EncryptionNull` loaded **before** `EncryptionVIE` in `Modules.config`. The first
encryption module to accept a connection init wins, so with VIE first every client is assigned VIE
encryption and the load client, which cannot do that, connects and then silently achieves nothing.

## Game protocol — real network load

Clients speak the game protocol over UDP, so the server sends to them as it would to a real player.
Thread percentages are of one core.

| clients | arenas | pos pkt/s | scanned/pkt | in-view/pkt | SendToOne/s | lock reads/s | cores | mainloop | send thr | alloc | GC |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 (idle) | – | 0 | 0 | 0 | 0 | 220 | 0.00 | <1% | <1% | 0 KB/s | 0 |
| 40 | 1 | 400 | 40.0 | 39.0 | 15,611 | 17,491 | 0.03 | <1% | 1% | 0 KB/s | 0 |
| 100 | 1 | 1000 | 100.0 | 99.0 | 99,038 | 103,203 | 0.09 | 4% | 4% | 0 KB/s | 0 |
| 100 | 4 | 1000 | 100.0 | **24.0** | 24,027 | 28,347 | 0.05 | 2% | 2% | 0 KB/s | 0 |
| 577 | 12 | 5691 | **577.0** | **45.7** | 260,365 | 287,715 | 0.36 | 17% | 16% | 1 KB/s | 0 |
| 598 † | 12 | 5664 | **597.0** | **45.8** | 261,907 | 344,385 | **0.71** | 37% | 26% | 1 KB/s | 0 |

† with combat: `--weapons 20 --deaths 6`

Mainloop iteration cost, and what the server pushed back out as measured from the clients:

| clients | arenas | mainloop iter/s | mean iteration | updates sent/s | bandwidth out | working set | heap |
|---|---|---|---|---|---|---|---|
| 0 (idle) | – | 14 | 0.02 ms | 0 | 0 | 131 MB | 33 MB |
| 40 | 1 | 68 | 0.12 ms | 6,829 | 132 KB/s | 131 MB | 39 MB |
| 100 | 1 | 71 | 0.63 ms | 19,251 | 385 KB/s | 137 MB | 40 MB |
| 100 | 4 | 91 | 0.17 ms | 16,423 | 316 KB/s | 134 MB | 42 MB |
| 577 | 12 | 327 | 0.49 ms | 92,328 | 1,945 KB/s | 166 MB | 42 MB |
| 598 † | 12 | 262 | 1.43 ms | 189,767 | 4,199 KB/s | 166 MB | 66 MB |

600 clients were requested for the last two rows; 577 and 598 connected. The shortfall was not
investigated — the client does not retransmit a lost login — so the counts are what was measured
rather than what was asked for.

## What the numbers say

**The scan is zone wide, and most of it is waste.** `scanned/pkt` tracks the whole zone population
regardless of how players are distributed, while `in-view/pkt` is what actually receives anything.
With everyone in one arena the two agree. Spread over arenas they diverge, and the gap is pure waste:

- 100 players in 1 arena: scans 100, sends to 99. Almost nothing wasted.
- 100 players over 4 arenas: scans 100, sends to 24. **76% wasted.**
- 597 players over 12 arenas: scans 597, sends to 46. **92% wasted**, at 3.4 million scans/sec.

That gap is what a per-arena player collection removes, and it widens with the number of arenas, so
it gets worse exactly as a zone grows.

**Both costs are quadratic in players per arena.** 40 to 100 clients in one arena is 2.5x the players
and 6.3x the sends, against 6.25x for a perfect square.

**The work lands on very few threads.** At 597 players with combat the mainloop is at 37% of a core
and the send thread at 26%, everything else negligible, on a 32 core machine using 0.71 cores in
total. Adding cores does nothing; parallelism is bounded by the thread count, not the core count.

**Allocation is essentially zero.** 0 to 1 KB/s at every load, and no garbage collection in any
generation in any run. 577 players is past `Constants.TargetPlayerCount` (512), which sizes the
object pools, and there is still nothing — so the pools are not a bottleneck at these counts and do
not need retuning. Recheck if a run ever shows sustained allocation or a rising gen0 count.

**The send thread has headroom.** It runs at 95-100 iterations a second at every load, which is its
10 ms interval, and recorded a single overrun across all runs. It is not the constraint yet.

## What combat costs

The last two game rows are the same scenario with and without fighting, so they isolate what firing
adds. A fifth of position packets carrying a weapon, plus six deaths per client per minute:

| | positions only | with combat | change |
|---|---|---|---|
| Updates sent to clients | 92,328/s | 189,767/s | **2.06x** |
| Bandwidth out | 1,945 KB/s | 4,199 KB/s | **2.16x** |
| Processor | 0.36 cores | 0.71 cores | **1.97x** |
| Mainloop thread | 17% | 37% | 2.2x |
| Send thread | 16% | 26% | 1.6x |
| Mean mainloop iteration | 0.49 ms | 1.43 ms | 2.9x |

Bandwidth doubled from a fifth of packets carrying fire, because a weapon shot cannot use either
compact batched form — every recipient gets the largest position packet instead — and it uses the
weapon's range rather than the plain view range, so it reaches further. Kill notifications came back
at 2,925/s, confirming the deaths were processed rather than rejected.

A measurement without combat therefore flatters the server by about half on the send side. The
positions-only rows are still the better basis for comparing one change against another, because
combat depends on who runs into whom, but they are not a picture of a real game.

## Bots, for comparison

Bots are fake players, so the fan out skips them: they produce scan load but the server never sends
to them, which is why `in-view` and `SendToOne` are zero throughout. They also run their AI and
physics simulation **on the mainloop**, so they load it far more heavily per player than a real
client does, and unlike the server they allocate.

| bots | brain | pos pkt/s | scanned/pkt | cores | mainloop | mean iteration | alloc | working set |
|---|---|---|---|---|---|---|---|---|
| 50 | orbit | 495 | 51.0 | 0.10 | 9% | 5.3 ms | 49 KB/s | 141 MB |
| 200 | orbit | 1880 | 201.0 | 0.38 | 35% | 23.3 ms | 198 KB/s | 150 MB |
| 250 | orbit | 2257 | 251.0 | 0.47 | 42% | 31.7 ms | 234 KB/s | 147 MB |
| 150 | combat | 1440 | 151.0 | 0.43 | 41% | 27.4 ms | 152 KB/s | 152 MB |

Combat bots cost noticeably more each: 150 of them use about as much processor as 250 orbiting ones.
Most of that is the bot AI, which is server side, rather than anything the server does for a player.

Two things to keep in mind when using bots as a measurement:

- The mean mainloop iteration is 20-60x what an equivalent number of real clients produces, because
  the physics and AI run there. Bots are a good way to load the position scan and the game logic, and
  a poor way to judge mainloop headroom.
- Bot allocation, 50 to 235 KB/s, is the simulation rather than the server. Do not read it as server
  allocation; the game protocol rows are the ones that speak to that.

The physics engine allocates a fixed number of ship slots, so an arena tops out at **256 bots** —
the same ceiling as the protocol's byte sized player id in the ordinary position packet.

## Against the developer's earlier stress test

For context, the server's developer previously ran 500 zerobots at 50 per arena, plus a Continuum
client, on a Ryzen 5 5600X with the Visual Studio debugger attached, and reported nearly exhausting
32 GB of RAM. Separately, all in one arena, he saw around 200 before things became processor bound.

The 598 client combat row is the closest comparison: a similar population at a similar spread, with
fighting rather than only movement. Differences worth stating before treating the two as equivalent:

- His bots played properly, including repels, on a small map where they were constantly killing each
  other. Ours fire bullets and bombs on a schedule and die on a schedule.
- His RAM figure is about his clients, not the server. 500 separate bot programs plus a debugger is
  what filled the memory; the server here used 166 MB for 598 players. The load client is a single
  process holding 598 sockets, which is far cheaper than 500 processes, so that number is not
  comparable and is not evidence of an improvement.
- His processor limit in a single arena appears to have been the bots, not the server.
- This machine has 32 cores against his 6. That mattered little, since the server only reached 0.71
  cores, but it did mean the load generator never competed with the server for processor.

So: the same order of population, with a comparable but not identical workload, on better hardware.

## Caveats

- Debug build. Absolute processor use is pessimistic; the shape of the curves is what matters.
- Client and server share a machine, so they compete for processor.
- Combat runs are less repeatable than positions-only ones, since what happens depends on who ends up
  near whom. Compare changes on the positions-only rows and use combat to check behaviour.
- Each figure is a single 10 second window taken once the load had settled, not an average of several
  runs. Treat differences under about 10% as noise.
- The chat protocol mode drives the player life cycle but sends no position packets, so it cannot
  produce any of the above. It is limited to about five logins per second by the server's chat
  module, which is single threaded at 10Hz.
