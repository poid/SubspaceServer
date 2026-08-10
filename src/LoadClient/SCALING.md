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

## Letting the server pick the number of send threads

A setting only helps someone who predicted the load. A zone sitting at a hundred players that fills to
several thousand needs more send threads at the moment nobody is likely to be watching. So each send
thread measures what fraction of its interval it spends working, and shard 0 starts another when they
are all busy.

Getting this right took two corrections, both of which the measurements caught and neither of which
was obvious beforehand.

**A threshold near the top is its own resting point.** At 70%, the server went from one thread to two
and stopped, sitting at 70–74% and still recording 30 overruns. Splitting a thread's work in two
leaves each half at about half the utilization, so a high threshold guarantees the threads settle just
above it and stay there, late. At 50% a split lands below the threshold and stops. A thread is also
counted as busy if it used its whole interval on any iteration in the window, since a thread that is
late on some and idle on others averages out below any threshold while still sending late.

**Assigning at connect only spreads future connections.** With that fixed threshold, the second thread
still sat at 5% of a core while the first stayed at 71% — it had started just after the last client
connected, so there were no new connections left to give it. A thread started because the server
filled up gets nothing when the players causing the load are already connected, which is the whole
case this exists for. So each thread now hands its excess to emptier ones.

Moving a connection between threads does not reorder its data: everything sent for a connection is
done holding that connection's `OutLock` and its outgoing queues are first in first out, so whichever
thread drains them next carries on where the last left off. What must not happen is two threads both
deciding a connection has lagged out, so a thread only ever removes from its own shard and does so
before adding to the other.

The result, 2,000 clients, starting from `SendThreads = 1` with nobody intervening:

| | one thread, fixed | four threads, fixed | adaptive |
|---|---|---|---|
| threads | 1 | 4 | **3, found on its own** |
| busiest | 74% | 23% | 27% |
| total send processor time | 74% | 91% | **78%** |
| overruns | 43 | 0 | **0** |

It grew 1 → 2 → 3 and settled at 25/26/27% — evenly spread, which is the rebalancing working. It also
landed on a cheaper configuration than the four threads picked by hand: three threads do the same work
for 78% of a core rather than 91%, because each extra thread costs its own scan and its own share of
the player lock.

Threads are not given back when load falls. An idle send thread costs one wakeup per interval, and the
alternatives are worse: retiring one means agreeing with the assignment path about exactly when a
shard stopped being usable, for a saving that does not justify it.

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

## Moving the fan-out off the mainloop

Done, and the mainloop stops being the limit. Same 2,000 clients over 20 arenas, everything on its
defaults, nobody intervening:

| | before | after |
|---|---|---|
| mainloop | 65% | **12%** |
| mean mainloop iteration | 0.630 ms | **0.065 ms** |
| mainloop iterations/s | 982 | 1,979 |
| total processor time | 1.73 cores | 2.54 cores |
| overruns | 0 | 0 |

The mainloop iteration is roughly ten times shorter, which is the honest measure: it is what the work
per iteration actually became. The fan-out now runs on four arena workers at about 30% of a core each.

**More worker threads is worse, not better, and this was the one thing measurement had to settle.**
The first default was the processor count less four, which on this machine meant sixteen workers. That
cost **4.84 cores** and pushed the mainloop back up to 30%:

| ArenaLoop workers | total cores | mainloop | mean mainloop iteration |
|---|---|---|---|
| 2 | 1.94 | 10% | 0.059 ms |
| 4 (default now) | 2.54 | 12% | 0.065 ms |
| 16 | 4.84 | 30% | 0.222 ms |

The work the workers do reads player state under a lock shared with the mainloop and the send threads.
Past a handful of threads, each extra one adds contention on that lock rather than capacity — enough
of it to make the mainloop itself measurably slower. Sixteen threads each doing a sixteenth of the
work still each pay the per-pass cost. The default is now a small fraction of the machine.

The offload does cost total processor time: 2.54 cores against 1.73. That is the queue hop per packet
plus four threads' share of the player lock. It is worth paying because the constraint was never total
work, it was one thread's share of one core — and that constraint is now gone.

### What it means for the ceiling

Nothing in the server is close to saturated at 2,000 players any more. The busiest thing is an arena
worker at about 30% of a core, with the mainloop at 12% and the send threads at 25%. Extrapolating
from one point is worth little, but the shape has changed: the limits from here are the arena workers
and the send threads, both of which grow with load, rather than a single thread that cannot.

### The advisor contract changed

`IPlayerPositionAdvisor.EditIndividualPositionPacket` is now called from an arena thread rather than
the mainloop, and for two arenas at once. It is documented on the interface. The one implementation in
the tree, `Matchmaking/MatchFocus`, only reads player data and edits the packet it is given, so it was
already safe.

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
