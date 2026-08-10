# Where the server runs out

Measured after Phase 2 (per-arena player collections), which is what makes these numbers worth
recording: the position scan is no longer a meaningful cost, so what is left is what actually limits
the server.

Same conditions as `BASELINE.md`: 32 cores, Linux (WSL2), .NET 10, **Debug** build, client and
server sharing the machine. Positions only, no weapons or deaths. Clients staggered 5 ms apart on
connect.

| clients | arenas | pos pkt/s | scanned/pkt | in-view/pkt | SendToOne/s | cores | mainloop | send thr | queue depth | send overruns |
|---|---|---|---|---|---|---|---|---|---|---|
| 997 | 16 | 9,952 | 62.1 | 61.0 | 606,859 | 0.72 | 27% | **38%** | 1 | 0 |
| 1,959 | 20 | 18,813 | 94.0 | 92.1 | 1,733,317 | 1.67 | **75%** | **75%** | 70 | 17 |

Thread percentages are of one core.

## The ceiling is about 2,600 players, and two things cause it

At a thousand players the network send thread is the busiest thing in the server, at 38% of a core
against the mainloop's 27%. By two thousand they have converged, both at 75%, and the server is
using 1.67 cores of the 32 available.

Extrapolating from those two points, **both threads reach one core at roughly 2,600 players**, and
the server stops there no matter how many cores the machine has. Two independent signs of the
approach are already visible at two thousand:

- The mainloop work queue sits at a depth of 70, against 1 at a thousand players. Work is arriving
  faster than the single consumer retires it.
- The send thread recorded 17 overruns, meaning iterations that used their whole 10 ms interval. At a
  thousand players there were none.

The important part is that the two threads are within a percent of each other. Relieving one alone
leaves the other at 100% and moves the ceiling very little:

- Sharding the send thread (Phase 4) without sharding the mainloop leaves the mainloop at 100%.
- Sharding the mainloop across arenas (Phase 5) without sharding the send thread leaves the send
  thread at 100%.

(The first of those is now measured rather than predicted — see below.)

Both are needed to move past roughly 2,600 players, and neither is worth doing alone.

## What sharding the send thread did

Measured after `Net:SendThreads` was added, same conditions, 2,000 clients over 20 arenas:

| Net:SendThreads | mainloop | busiest send thread | send total | send iter/s | overruns | SendToOne/s | cores |
|---|---|---|---|---|---|---|---|
| 1 | 65% | **74%** | 74% | 94 | 43 | 1,570,087 | 1.56 |
| 4 | 69% | **23%** | 91% | 381 | **0** | 1,711,373 | 1.76 |

The send side is no longer anywhere near a limit: the busiest of the four threads uses under a
quarter of a core, and the iterations that used their whole 10 ms interval went from 43 in the window
to none. Because it is no longer running late, it also gets more done — 1.71M single-player sends per
second against 1.57M.

Total send processor time rose from 74% to 91%. That is the cost of four threads each taking the
player read lock and keeping their own bookkeeping, plus the extra throughput. It is worth paying
here because the machine has 32 cores and the constraint was one thread, not total work — but it is
also why the default is 1. Below roughly a thousand players a single send thread is not close to
busy, and splitting it would only add the overhead.

**The mainloop is now the only thing in the way.** At 2,000 players it is at 69% against the busiest
send thread's 23%, so the next ceiling is the mainloop reaching one core at roughly 2,900 players.
That is what Phase 5 (arena-sharded workers) addresses, and the measurement gate in the plan is
satisfied: the single mainloop thread is genuinely the remaining bottleneck.

## What indexing per-player data by array did

Per-player data was fetched from a `ConcurrentDictionary` on every access, including from the
fan-out's inner loop — around two million hash probes a second at this load. The keys are dense
sequential ints, so an array replaces it. Same 2,000 clients, `Net:SendThreads` at 4, and at a
near-identical load (19,565 against 19,575 pkt/s, 87.5 in-view both):

| | dictionary | array |
|---|---|---|
| mainloop | 69% | **65%** |
| mean mainloop iteration | 0.798 ms | **0.628 ms** |
| mainloop iterations/s | 877 | 982 |
| managed heap | 95 MB | **74 MB** |

The mean mainloop iteration is 21% shorter, which is the honest measure of the change since it is
what the work per iteration actually became. The 22% smaller managed heap is the dictionary's buckets
and nodes going away — about 64 entries per player across 2,000 players.

## Where the mainloop's time actually goes, and what that means for Phase 5

At 2,000 players the mainloop handles 19,600 position packets a second. For each one it walks the
arena's players (98 candidates), and for the 88 that pass the distance check it builds and hands a
packet to the network layer — 1.7 million `SendToOne` calls a second.

That fan-out loop is the mainloop. And reading it, **it writes almost nothing shared**: the packet it
builds is a stack local, and its only two effects on anything shared are `INetwork.SendToOne` and an
interlocked lag counter, both already documented as callable from any thread. Everything else it
touches on other players — position, ship, freq, resolution, spectating — it only reads, under a read
lock that several threads already hold at once.

So the fan-out can move to an arena thread with a snapshot of the sending player's position, and it
does not need the full arena-threading model to do it. That is a much smaller change than moving the
packet *handlers*, which cannot be done piecemeal: the handlers mutate player game state, so moving
some of them and not others just splits one writer into two.

## What is not the limit

**Not the position scan.** 94 scanned per packet against 92 sent to, so 98% of the scan produces a
send. Before Phase 2 the same shape of run scanned 577 to send to 46. This is no longer worth
optimising.

**Not the global player write lock.** Held 2.2 ms per ten second window at a thousand players and
3.9 ms at two thousand — under a twentieth of a percent of wall time, and it grows with player count
rather than with load, since it is taken about twenty times a second regardless. The scans under the
write lock that the plan proposed removing are not costing anything measurable, and would not be
worth the risk of restructuring the player state machine to avoid.

**Not memory or allocation.** 196 MB working set and 1 KB/s allocated at two thousand players, with
no garbage collection in any generation. Four times the player count that sizes the object pools,
and they are still keeping up.

**Not the receive thread.** 6% of a core at two thousand players. It does, however, drop connection
inits when several hundred clients connect at once: 600 simultaneous connects lost about 4%, while
2,000 staggered 5 ms apart lost 2%. That is a burst-arrival limit rather than a steady-state one,
and it is why the load client has `--connect-delay`.

## Caveats

- Debug build, and the load generator shares the machine, so the absolute ceiling in a release build
  on dedicated hardware will be higher. The ratio between the two threads is the durable finding, not
  the player number.
- Both rows are a single ten second window taken once the load had settled.
- Positions only. Combat roughly doubles the send side, so a zone where people are actually fighting
  will reach the ceiling sooner.
