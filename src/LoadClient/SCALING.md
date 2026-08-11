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

---

# Pushing past two thousand

With the fan-out on the arena threads, the mainloop is no longer the limit, so the load was raised
until something else became one. Arenas were added along with players, to keep per-arena counts near
the cap rather than letting them grow without bound.

| clients | arenas | pkt/s | in-view/pkt | cores | mainloop | busiest thread |
|---|---|---|---|---|---|---|
| 3,000 | 20 | 20,019 | 109.1 | 3.24 | 15% | ArenaLoop, 42% |
| 4,500 | 40 | 27,596 | 63.9 | 3.08 | 19% | ArenaLoop, 39% |
| 6,000 | 60 | 30,824 | 47.5 | 3.17 | 24% | ArenaLoop, 39% |

Nothing is saturated. At six thousand clients the busiest thread in the server is an arena worker at
39% of a core, with the send threads at 25–33%, the mainloop at 24% and the receive thread at 20%.
No overruns, no dropped work, no errors.

## The lag check was the next wall, and it was quadratic

The first run of this series found it immediately: at 4,500 and again at 6,000 clients, `LagAction`
sat at **100% of a core** — a whole thread, and the busiest thing in the server, ahead of the arena
workers at 45%.

Its check thread wakes once per player per interval, and each wake up walked every player taking a
lock on each one to find the least recently checked. The work therefore grew with the *square* of the
population, the same shape as the position fan-out before it had a per-arena player collection. It
also took the zone-wide player lock once per player rather than once per interval, so every other
thread needing that lock waited behind it.

Collecting the due players in one pass and then taking them one at a time makes it linear. It removed
more than a core at six thousand players:

| clients | before | after |
|---|---|---|
| 4,500 | 4.34 cores | **3.08** |
| 6,000 | 4.60 cores | **3.17** |

The cost stopped growing with the population at all — total processor time is now flat from 3,000 to
6,000 clients, because the extra work is offset by the smaller in-view counts that come with spreading
players over more arenas.

## What the harness can no longer tell us

**The load client is now the limit, not the server.** At six thousand clients it produces 30,824
position packets a second, about 5 per client per second against the ~9.5 each is trying to send, and
only about 4,900 of the 6,000 ever connected. It shares the machine with the server and has to
*receive* 1.5 million packets a second to do it.

So these numbers are a floor on what the server can do, not a ceiling. Finding the real one needs the
load generated from other machines.

## The bandwidth limiter was quietly capping every measurement

Chasing why the load client seemed to be losing three quarters of its packets turned up something
that changes how every number above should be read.

`BandwidthDefault` gives each connection `Net:LimitInitial` bytes per second — **5,000** — and raises
it only in `AdjustForAck`, when an acknowledgement arrives. There is no growth with time. Load clients
generate almost no reliable traffic, so they almost never ack, so they sat at close to the initial
limit for the whole run. The server was therefore discarding most position packets at the limiter
rather than sending them, which is exactly what it is designed to do.

Nothing was lost and nothing was broken. But it means the *send* side of every measurement above was
doing a fraction of the work real clients would ask of it. Raising the limit to 64 KB/s, at the same
2,000 clients over 20 arenas:

| | default limiter | realistic bandwidth |
|---|---|---|
| position updates each client received | 166/s | **771/s** |
| total sent to clients | 7,129 KB/s | **30,598 KB/s** |
| send threads (grown automatically) | 2 | **5** |
| total cores | 2.45 | 3.84 |
| send overruns | 0 | 47 |

Four and a half times the actual data, and the send threads grew from two to five on their own to
carry it. The arena workers barely moved, at 33% against 35% — the fan-out scan happens before the
limiter, so that half of the work was always being measured honestly. It is the send side that was
understated.

**This matters beyond the harness.** The plan's arena cap analysis assumes a player in a full arena
receives around 25 KB/s. A connection cannot reach that from a 5,000 B/s starting limit on
unreliable traffic alone, because only acks raise it. Any zone running large arenas will want
`Net:LimitInitial` and `Net:LimitMinimum` raised regardless of anything in this document — it is
independent of the threading work and predates all of it.

## What the player id width actually costs, measured

The plan predicted that once player ids pass 1023 the server has to fall back to the 21 byte weapons
packet for every position update, roughly doubling the bandwidth of the dominant traffic class. That
is now measured rather than derived.

Bytes on the wire per position update, taken from what the clients actually received, with the
bandwidth limiter raised so it is not the thing being measured:

| player ids | packet the server picks | Continuum-style client | plain VIE client |
|---|---|---|---|
| ≤ 255 | 10 B batched small (0x39) | **11.3 B** | 17.5 B (16 B, 0x28) |
| 256 – 1023 | 11 B batched large (0x3A) | **12.6 B** | 21.1 B (21 B, 0x05) |
| > 1023 | 21 B weapons (0x05) | **16.8 B** (mixed) | 22.0 B |

The Continuum figures sit just above the packet sizes themselves, the difference being the grouping
byte amortised over each merged batch. The 16.8 B row is a mix, because that run had ids either side
of 1023.

**So at ten thousand concurrent players every id is over 1023 and every position update costs 21 bytes
instead of 11.** That is the 1.9× the plan estimated, confirmed. Arena-local ids would put every
player back under 256 and halve the outbound bandwidth of the thing the server spends most of its
bandwidth on.

That matters more now than when the plan was written, because the bandwidth limiter section above
shows bandwidth is the binding per-connection constraint, not processor time.

## Arena-local player ids, measured

Implemented behind `Net:UseArenaLocalPlayerIds`, off by default. `Player.Id` stays the zone-wide
identity used by the billing server, persistence and logging; `Player.ArenaSlot` is what clients are
told and is a slot within the arena, so it stays small however many are online.

2,000 clients over 20 arenas, so 100 per arena and every arena-local id below 256:

| | zone-wide ids | arena-local ids |
|---|---|---|
| bytes per position update | 16.7 | **11.5** |
| total sent to clients | 24,220 KB/s | **17,670 KB/s** |
| position updates delivered | 1,488,692/s | **1,575,026/s** |
| send thread overruns | 6 | **0** |

**A third less bandwidth, while delivering more updates.** The compact packet is smaller, so more of
it fits through, which is why the update count went up rather than staying level.

The 1.45× here is smaller than the 1.9× the earlier table predicts, and that is expected: with 2,000
players about half the zone-wide ids are still under 1023 and get the 11 byte batched form anyway. At
ten thousand every id is above it, and the comparison becomes the full 21 against 11.3.

### What it cost to get right

The slot is taken and given up inside `SetPlayerArena`, together with membership of the arena's player
list, so the two cannot disagree. Two places needed more than a rename:

**`Player.Attached`** holds a client-visible id and was compared against a zone-wide one. Both sides
now use the slot.

**Chat stamped one packet with one id for a whole set of recipients**, and mod chat goes to staff
across the zone. A recipient in another arena would have resolved that id to one of *their* arena's
players and attributed the message to them. Recipients outside the sender's arena are now sent -1.

The debug auditor earned its keep twice here. It caught that a player let into a full arena ends up
with no slot and therefore invisible — now refused at the authorization step instead, under the write
lock so two players cannot take the last place at once. Fake players still bypass that, so they can
exceed the cap; that is reported where it happens and the auditor no longer requires every player to
hold a slot, only that a slot held points back at its holder.

Verified under 600 clients with weapons and deaths at a deliberately small cap of 64, so slots were
being recycled constantly: no assertion failures.

### Still unverified

**No real Continuum client has been tested against this.** The specific risk is that a client may keep
its player table across an arena change rather than rebuilding it from the entering list. If so,
problems appear only when switching arenas, not at login — so a smoke test that logs in and flies
around will pass while the feature is broken. That is why it is off by default.

## Two things the load client was getting wrong

Both were found chasing figures that did not add up, and both were making the server look better than
it is.

**It claimed no client features**, so the server never used the compact batched forms on it and sent
16 or 21 bytes where a real Continuum client would get 10 or 11. Every byte figure measured before
this overstated what the server pushes. The server takes a VIE client's feature flags from the login
packet verbatim, so claiming batched positions is enough without having to speak Continuum's
encryption. `--no-batch-positions` keeps the old behaviour, which is what a plain VIE client does.

**It counted a batched packet as one update.** The send thread merges consecutive batched position
packets for the same recipient into one packet holding many position items, so counting packets
undercounted updates by about five times — and the bytes per update that came out of it were wrong by
the same factor in the other direction. After the fix the client's count agrees with the server's own
`SendToOne` counter to within noise, which is the check that says both are now right.

## What to watch next

- **The receive thread**, at 20% of a core at six thousand and climbing faster than anything else.
  It is a single thread, and sharding it is the plan's Phase 7.
- **The global player write lock**, held 6.5 ms, 14.7 ms and 19.9 ms per ten second window at 3,000,
  4,500 and 6,000. Still under a quarter of a percent of wall time, but it is the one figure that
  grows with player count rather than staying flat.
- **Connection bursts.** Only 4,909 of 6,000 clients connected, staggered 3 ms apart. The receive
  thread drops connection inits under burst arrival, which is a known limit and unrelated to steady
  state, but it will matter for a zone that fills quickly.
