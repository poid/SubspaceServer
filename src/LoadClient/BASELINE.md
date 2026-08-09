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

Thread percentages are of one core. Idle CPU was measured separately; a dash means the thread was
below the 1% reporting threshold.

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

**Memory and allocation are flat.** 104 KB/s allocated at 537 players, the same as idle, with no
garbage collection in any generation across every run. Working set went from 131 MB idle to 169 MB.
Notably 537 players is past `Constants.TargetPlayerCount` (512), which sizes the object pools, and
there is still no allocation growth — so the pools are not a bottleneck at these counts and do not
need retuning yet. Recheck this if a run ever shows sustained allocation or a rising gen0 count.

## Caveats

- Debug build. Absolute CPU is pessimistic; the shape of the curves is what matters.
- Client and server share a machine, so they compete for CPU.
- 600 clients were requested for the last row and 537 connected. The shortfall was not
  investigated, so treat the client count as measured rather than requested.
- The chat protocol mode drives the player life cycle but sends no position packets, so it cannot
  produce any of the above. It is limited to about five logins per second by the server's chat
  module, which is single threaded at 10Hz.
