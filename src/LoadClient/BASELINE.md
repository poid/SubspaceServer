# Baseline measurements

Numbers taken before any scaling work, so that later changes can be judged against something.
Produced with the load client in this folder and read from the server's `Metrics` module.

Machine: 32 cores, Linux (WSL2), .NET 10, Debug build. Debug matters — treat the CPU figures as
relative, not as capacity planning.

Reproduce with:

```
dotnet run --project src/LoadClient -- --protocol game --clients <n> --arenas <list> --spread 2000 --duration 50
```

The server needs `EncryptionNull` loaded **before** `EncryptionVIE` in `Modules.config`. The first
encryption module to accept a connection init wins, so with VIE first every client is assigned VIE
encryption and the load client, which cannot do that, connects and then silently achieves nothing.

## Game protocol (real network load)

| clients | arenas | pos pkt/s | scanned/pkt | in-view/pkt | SendToOne/s | lock reads/s | cores | mainloop | send thread | alloc | GC |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 (idle) | – | 0 | 0 | 0 | 0 | 220 | 0.01 | <1% | – | 131 KB/s | 0 |
| 40 | 1 | 400 | 40.0 | 39.0 | 15,610 | 17,389 | 0.03 | 1% | 2% | 104 KB/s | 0 |
| 100 | 1 | 994 | 100.0 | 99.0 | 98,441 | 102,570 | 0.09 | 4% | 4% | 145 KB/s | 0 |
| 100 | 4 | 1000 | 100.0 | 24.0 | 24,022 | 28,138 | 0.05 | 2% | 2% | 103 KB/s | 0 |
| 537 | 12 | 5338 | **537.0** | **42.8** | 228,808 | 255,705 | 0.34 | 15% | 15% | 104 KB/s | 0 |
| 567 † | 12 | 5353 | **567.0** | **42.7** | 230,911 | 305,379 | **0.61** | 32% | 21% | 121 KB/s | 0 |

† with combat: `--weapons 20 --deaths 6`

Thread percentages are of one core. Idle CPU was measured separately; a dash means the thread was
below the 1% reporting threshold.

## What combat costs

The last two rows are the same scenario with and without fighting, so they isolate what firing adds.
20% of position packets carrying a weapon, plus six deaths per client per minute:

| | positions only | with combat | change |
|---|---|---|---|
| Updates sent to clients | 81,265/s | 166,642/s | **2.05x** |
| Bandwidth out | 1,775 KB/s | 3,684 KB/s | **2.08x** |
| Processor | 0.34 cores | 0.61 cores | **1.79x** |
| Mainloop thread | 15% | 32% | 2.1x |
| Send thread | 15% | 21% | 1.4x |

Bandwidth roughly doubled from a fifth of packets carrying fire, because a weapon shot cannot use
either compact batched form — every recipient gets the largest position packet instead — and it uses
the weapon's range rather than the plain view range, so it reaches further. Kill notifications came
back at 2,505/s, confirming the deaths were processed rather than rejected.

A measurement without combat therefore flatters the server by about half on the send side. The
positions-only rows are the better basis for comparing one change against another, because combat
depends on who runs into whom, but they are not a picture of a real game.

## Bots, for comparison

Bots are fake players, so the fan out skips them: they generate scan load but the server never sends
to them. They are cheap to run in bulk and useful for loading the game logic, not the network.

| bots | brain | pos pkt/s | scanned/pkt | cores | mainloop | alloc |
|---|---|---|---|---|---|---|
| 50 | orbit | 502 | 51.0 | 0.10 | 9% | – |
| 200 | orbit | 1875 | 201.0 | 0.37 | 34% | – |
| 250 | orbit | 2275 | 251.0 | 0.44 | 40% | – |
| 150 | combat | 1440 | 150.3 | 0.42 | 40% | 249 KB/s |

Combat bots cost noticeably more each: 150 of them use more processor than 200 orbiting ones. Most
of that is the bot AI itself, which runs on the server, rather than anything the server does for a
real player.

The physics engine allocates a fixed number of ship slots, so an arena tops out at **256 bots**.
That happens to be the same ceiling as the protocol's byte sized player id in the ordinary position
packet.

## Against the developer's earlier stress test

For context, the server's developer previously ran 500 zerobots at 50 per arena, plus a Continuum
client, on a Ryzen 5 5600X with the Visual Studio debugger attached, and reported nearly exhausting
32 GB of RAM. Separately, all in one arena, he saw around 200 before things became processor bound.

The 567 client combat row above is the closest comparison: a similar population at a similar spread,
now with fighting rather than just movement. Differences worth keeping in mind before treating the
two as equivalent:

- His bots were playing properly, including repels, on a small map where they were constantly
  killing each other. Ours fire bullets and bombs on a timer and die on a timer.
- His RAM figure is about his clients, not the server. 500 separate bot programs plus a debugger is
  what filled the memory; the server here used 167 MB for 567 players. The load client is a single
  process holding 567 sockets, which is far cheaper than 500 processes, so that number is not
  comparable and is not evidence of an improvement.
- His processor limit in a single arena appears to have been the bots, not the server.
- This machine has 32 cores against his 6. That did not matter much, since the server only reached
  0.61 cores, but it did mean the load generator never competed with the server for processor.

So: the same order of population, with a comparable but not identical workload, on better hardware.

## What the numbers say

**The scan is zone wide, and most of it is waste.** `scanned/pkt` tracks the whole zone population
regardless of how the players are distributed, while `in-view/pkt` is what actually receives
anything. With everyone in one arena the two agree. Spread over arenas they diverge, and the gap is
pure waste:

- 100 players in 1 arena: scans 100, sends to 99. Almost nothing wasted.
- 100 players over 4 arenas: scans 100, sends to 24. **76% wasted.**
- 537 players over 12 arenas: scans 537, sends to 43. **92% wasted**, at 2.87 million scans/sec.

That gap is what a per-arena player collection removes, and it widens with the number of arenas, so
it gets worse exactly as the zone grows.

**Both costs are quadratic in players per arena.** Going from 40 to 100 clients in one arena is 2.5x
the players and 6.3x the sends, against 6.25x for a perfect square.

**The work lands on very few threads.** At 537 players the mainloop and the network send thread are
at 15% of a core each and everything else is negligible, on a 32 core machine using 0.34 cores in
total. Adding cores does nothing here; the parallelism is bounded by the thread count, not the core
count.

**Memory and allocation are flat.** No garbage collection in any generation across any run, and
working set went from 131 MB idle to 169 MB. 537 players is past `Constants.TargetPlayerCount`
(512), which sizes the object pools, and there is still no allocation growth and no collection, so
the pools are not a bottleneck at these counts and do not need retuning. Recheck if a run ever shows
sustained allocation or a rising gen0 count.

> The allocation rates in the table above, around 100 to 150 KB/s, are wrong. They are left as
> recorded because that is what those runs reported. A review afterwards found the metrics module
> was sampling per thread processor time out of /proc *after* taking the window's allocation
> baseline, so its own string allocations were being counted as the server's. Measured again once
> that was fixed, allocation under the same load is **about 1 KB/s**. The conclusion is unchanged
> and in fact stronger: the server barely allocates at all in steady state.

## Caveats

- Debug build. Absolute CPU is pessimistic; the shape of the curves is what matters.
- Client and server share a machine, so they compete for CPU.
- 600 clients were requested for the last two rows and 537 then 567 connected. The shortfall was not
  investigated, so treat the client count as measured rather than requested.
- Combat runs are less repeatable than positions-only ones, since what happens depends on who ends
  up near whom. Compare changes on the positions-only rows and use combat to check behaviour.
- The send thread iteration and overrun counters were not wired up during these runs, so the zeros
  they reported were not a measurement of an idle send thread. Fixed after review.
- The chat protocol mode drives the player life cycle but sends no position packets, so it cannot
  produce any of the above. It is limited to about five logins per second by the server's chat
  module, which is single threaded at 10Hz.
