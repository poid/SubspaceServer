# Where the server runs out

> **Read [Ten thousand players](#ten-thousand-players) first.** The server has since been run with
> 9,906 players in a hundred arenas, logging nothing. Every ceiling in the earlier sections of this
> document — starting with the "about 2,600 players" immediately below — was the load generator
> running out, not the server. Those sections are kept because the reasoning about what scales with
> what is still sound and is what guided the work, but their numbers are wrong and no extrapolation
> from them should be trusted.

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

# Ten thousand players

**The figures in the rest of this section, and every ceiling quoted earlier in this document, were
measured with a load generator that was itself the limit.** They are kept below only because the
reasoning about *shapes* — what scales with population, what scales with arena size — still holds. The
absolute numbers do not. What follows is the series repeated once the generator was fixed.

100 players per arena throughout, so what changes between runs is population alone.

| clients | connected | logged in | in arenas | each sending | server sent | errors |
|---|---|---|---|---|---|---|
| 2,000 | 2,000 | 2,000 | **2,000** | 10.0/s | 1.63M updates/s, 27.7 MB/s | 0 |
| 4,000 | 4,000 | 4,000 | **4,000** | 10.0/s | 1.83M updates/s, 40.8 MB/s | 0 |
| 7,000 | 6,978 | 6,977 | **6,977** | 10.0/s | 3.36M updates/s, 75.8 MB/s | 0 |
| 10,000 | 9,928 | — | **9,906** | 10.0/s | 2.16M updates/s, 51.1 MB/s | 0 |

**Ten thousand concurrent players in a hundred arenas, on a debug build, sharing one machine with the
load generator, with nothing logged.** At that point the server was running eight send threads at
42–44% of a core each, having grown into them by itself from one, with the mainloop at 19%.

The server's limit is still not known. Its own receive socket's drop counter did not move across a
whole run at six thousand and its receive queue was empty at every sample, so it was never the one
losing packets.

## Why the delivered rate falls with population, and why it is not the server

Per-client delivery falls from 814/s at two thousand to 218/s at ten thousand, which looks like the
server running out. It is not: the kernel discarded 1.6 million datagrams during that run, on the
*client's* sockets, and those are updates the server had already sent.

Raising the client's receive buffer eightfold barely moved it:

| client receive buffer | in arenas | delivered per client | kernel drops | memory peak |
|---|---|---|---|---|
| 512 KB | 9,907 | 198/s | 1,700,981 | 3,185 MB |
| 4 MB | 9,906 | 218/s | 1,569,502 | 3,280 MB |

So it is not a buffer size problem. Ten thousand receive loops in one process cannot all be serviced
promptly, and a socket that goes unattended fills whatever it has been given. Memory was never a
constraint — Linux charges a socket buffer by what is queued in it, not by its limit.

**Splitting the generator across processes makes it worse, not better.** Five processes of two thousand
against one process of ten thousand: 5,835 connections against 9,778, all five stalling at the same
count at the same instant. Same scheduler, same cores, five runtimes competing. For the same reason,
more WSL instances or virtual machines on one box will not help either — a second physical machine is
the only thing that adds capacity, and a gigabit link would itself become the limit somewhere near
these numbers, since the server was already pushing 75.8 MB/s at seven thousand.

---

# Pushing past two thousand (superseded — generator-limited)

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

## What the harness could not tell us, and why (superseded)

Everything below was true of the generator at the time and is no longer. Four defects in it, each
found only because a figure did not add up, were between this and the numbers above: it never retried
a connection init, never retransmitted anything it sent "reliably", divided its counters by the wrong
window, and left its sockets on a receive buffer sized for a socket someone was attending to. Each one
made the server look slower than it is.



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

### Checked against a real Continuum 0.40 client

The risk this was gated on — a client keeping its player table across an arena change rather than
rebuilding it from the entering list — did not materialise. The test was better than planned, because
the slot allocator walks forward: re-entering the same arena gave ids 0, then 4, then 5, while the
bots kept 1 to 3. So the client was repeatedly told it was a different player, which is the hardest
version of the case.

Working: arena entry, repeated arena switching with the id changing underneath, the player list,
position updates, spectating, private messages, turret attach, kills, and flag pickup and drop. No
malicious packets, no errors, no assertion failures across the whole session.

**But it found a bug the automated tests could not, and the way it failed is the lesson.** Dying
produced no kill message at all — not on screen, not in chat. `S2C_Kill` was still carrying zone-wide
ids, so the client could not match either id to anyone it knew and silently rendered nothing. There
was no error on either side: the server sent a well-formed packet, and a client that cannot resolve an
id has nothing to report. Only a person looking at a screen would ever have caught it.

The first sweep had looked for the id being cast off a variable named `player`. These sites cast off
`killer`, `killed`, `other` and `carrier`. A sweep for *any* variable's `Id` being narrowed to a wire
width found six: the kill packet, the banner packet sent for another player, and four flag pickup and
drop packets.

### Still unverified

Anything that needs a **second real client** to observe. Banners, crowns, greens and score updates all
carry ids, and fake players cannot confirm them because they never receive packets. Given that a wrong
id shows up as nothing happening rather than as an error, their silence during testing proves nothing.
That is why this stays off by default.

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

## The global player lock was taken once per recipient, and it was redundant

Attributing the lock by caller found the send path taking the global player read lock 4,325,895 times
a second at four thousand players. At two thousand the breakdown was `SendOrBufferPacket` 1,789,546 a
second and `SendRaw` 179,967, against everything else taking it once per packet. The arithmetic named
the source exactly — 20,001 packets a second times 89.4 recipients in view is 1,788,089 — and the
source turned out to be re-entry. The position fan-out takes the lock once around its whole recipient
loop and then calls `SendToOne` per recipient, and the send thread holds it across its entire pass;
both then reached code that took it again, for a check the outer hold had already made safe.

Skipping the acquisition when the calling thread already holds it, measured at four thousand players
over forty arenas with connections arriving throughout, at the same offered load each time:

| | processed pkt/s | workers busy | cores | read locks/s | cores per 1k pkt |
|---|---|---|---|---|---|
| 2 workers, before | 27,823 | 99% | 6.67 | 3,005,009 | 0.2397 |
| 2 workers, after | 33,079 / 34,407 | 100% | 6.70 | 154,748 | 0.2025 |
| 4 workers, before | 42,628 | 90-91% | 10.13 | 4,325,895 | 0.2376 |
| 4 workers, after | 40,011 | 79-80% | 8.67 | 161,663 | 0.2167 |

Read the four worker row carefully, because the lower number is the better result. 42,628 was a
saturated server draining a backlog inside the sampled window, above the 39,677 a second the clients
were actually sending. Afterwards the server tracks the offered load exactly, at 79% of four workers
instead of 90%, for 8.67 cores instead of 10.13. Where the workers genuinely were the limit, at two,
throughput rose about a fifth for the same processor time.

**It did not change how the workers scale, which is what it was built to test.** Per worker core, the
drop from two workers to four was 14,050 to 10,875 before and 16,540 to 12,582 after: 22.6% against
23.9%. Everything became about a sixth more efficient and the shape of the curve did not move. Lock
acquisition count is a real cost and is not the reason adding arena workers loses throughput.

One measurement note. A debug-only assertion change was included at first and looked like it might be
inflating the result, so it was reverted and re-run alone: 34,407 packets a second without it against
33,079 with it, which is the wrong way round and inside the run to run spread. It was worth nothing
and was dropped. All of the improvement is the skip.

## The arena workers scale backwards, and the processor is not the reason

> **The numbers in this section are contaminated; the conclusion is not.** Every two worker run in
> the table below carried measurably less load than the four worker run it is compared with, for a
> reason nothing in the output mentioned — see [Load that went missing](#load-that-went-missing). The
> effect was afterwards re-established on runs verified clean, and reproduced twice: see
> [The same work, more processor time](#the-same-work-more-processor-time). This section is kept for
> the reasoning and for the causes it ruled out, which stand.

The per worker efficiency loss survived removing the lock traffic, so it was measured properly. The
unit is scans per arena-core-second — players examined by the fan-out, divided by the processor time
the arena workers used — which normalises for how many recipients each packet has and for how hard
the workers are being driven.

| run | players | arenas | workers | busy | scans per core-second |
|---|---|---|---|---|---|
| half-w2 | 2,000 | 20 | 2 | 56% | 1.787M |
| lk-w2 | 4,000 | 40 | 2 | 99% | 1.671M |
| sat-w2 | 4,000 | 20 | 2 | 102% | 1.622M |
| lk-w4 | 4,000 | 40 | 4 | 78% | 1.278M |
| sat-w4 | 4,000 | 20 | 4 | 77% | 1.296M |

Efficiency depends on the number of workers and on nothing else in that table. Two workers give about
1.65M whether they are 56% busy or 102% busy and whether the players sit in twenty arenas or forty;
four give about 1.29M under the same variations. At identical work per packet, doubling the workers
raised throughput from 16,541 packets a second to 20,078 — 1.21 times for twice the threads.

Three candidate causes were tested and all three were wrong.

**Not the per recipient atomics.** Removing both the `SendToOne` counter and the bandwidth priority
statistic — the two shared fields each recipient touches — moved the two-to-four loss from 23.5% to
23.4% and absolute efficiency by under 2%.

**Not the send path at all.** Spreading the players across the whole map cut recipients per packet
from 89 to 20 while leaving the scan untouched. If anything done once per recipient were responsible,
the loss would have fallen with it. It did not move: 23.5% at 89 recipients, 23.4% at 20.

**Not thin loading of each worker.** The idea that a worker handling fewer items pays more per item in
wakeups predicts that equal items per worker should cost the same. It does not: at 10,000 items a
second per worker either way, two workers managed 1.787M scans per core-second and four managed
1.278M, 28% apart.

What is left is thread count itself, and the progression starts at the first thread added, with the
same total work throughout:

| workers | scans per core-second | against one worker |
|---|---|---|
| 1 | 5.263M | |
| 2 | 4.353M | -17.3% |
| 4 | 3.333M | -36.7% |

**This machine is an i9-13900K**, which does not have equal cores: eight performance cores with two
threads each, and sixteen efficiency cores. That looked like the answer, and it was written up here as
such. It is wrong, and the correction is worth more than the original claim.

Measured natively on Windows, where processor affinity is real and logical processor numbers map to
the hardware — inside WSL2 they do not, and every subset of vCPUs there benchmarks within 3% of every
other, which is what hid this. A fixed loop pinned to one processor at a time:

| processors | time | |
|---|---|---|
| 0, 1, 2, 8, 14, 15 | 2,850-3,021 ms | performance cores |
| 16, 17, 20, 24, 30, 31 | 7,083-7,635 ms | efficiency cores, 2.5x slower |

So the machine is heterogeneous, comfortably enough to explain a 20-30% penalty. But pinning the
server to eight distinct performance cores and the load client to the efficiency cores — which also
removes the load generator from competing with the server, a confound in every earlier number here —
the penalty survives. At the same delivered throughput, four thousand players over twenty arenas:

| configuration | cores per 1,000 packets |
|---|---|
| 1 worker, 8 performance cores | 0.249 |
| 2 workers, 8 performance cores | 0.276, 0.285 |
| 2 workers, 16 performance cores | 0.282, 0.313 |

Eleven to twenty six percent more processor time for exactly the same work, with every core identical
and nothing else running on them. Whatever this is, it is not the cores being unequal.

Treat that as preliminary rather than settled. The Windows runs did not reach a steady state inside
two minutes: throughput ramps, peaks, then falls to a floor near twenty thousand packets a second in
every configuration, so the window the harness picks lands in a different phase depending on the run.
Read at their peaks instead of at the floor, two workers beat one. The comparison above is taken at
the floor, where all configurations deliver the same amount and only the cost differs, which is the
one place they are comparable — but it rests on a single window for the one worker case.

**What this means for everything measured here.** `ArenaLoop:Workers` was tuned on this box and found
four best with eight clearly worse. That number should still be re-measured on the machine the server
will actually run on, but the reason is no longer that this processor is unusual. It is that the
number is a property of the machine and the load, and nobody has yet explained why more workers cost
more.

## Load that went missing

Two runs of the same configuration — four thousand clients, forty arenas, two arena workers, nothing
different between them — settled at 18,554 packets a second and 37,677. Both perfectly steady for the
whole run. That looks like a threading defect, because half throughput is what one of two workers
doing nothing looks like.

It was not. Per worker counters showed both workers retiring identical numbers of items with their
queues nearly empty, so nothing was stalled and nothing was backing up. Only half as much work was
ever queued. The reason was in the log the whole time, in a category nobody was reading:

```
I <LagAction> {7} [Load3767] specced for: ping
I <Game>      {7} [Load3767] Changed ship/freq to ship Spec, freq 8025.
```

LagAction had specced 2,144 of the 4,000 clients for high ping, and `Game.HandlePositionPacket` says
plainly what that costs:

```csharp
// spectators don't get their position sent to anyone
if (player.Ship != ShipType.Spec)
```

A specced player is still connected and still sending ten position packets a second. Every one of
them is received, checksummed, lag-collected and dispatched — and then produces no broadcast at all.
The metric that this document calls throughput counts broadcasts.

That is visible in the numbers once you know to look. `mainloop work` sat at 40,000 a second in every
run, degraded or not, because every packet really did arrive. Only `arena work` moved:

| run | workers | specced | mainloop work | arena work / pkt/s |
|---|---|---|---|---|
| f1 | 1 | 0 | 40,012 | 39,993 |
| d1 | 2 | 0 | 40,054 | 40,005 |
| d3 | 2 | 171 | 40,132 | 38,285 |
| f2r | 2 | 231 | 40,027 | 37,677 |
| d2 | 2 | 2,009 | 40,161 | 19,924 |
| f2 | 2 | 2,144 | 40,053 | 18,554 |

`(4000 − specced) × 10` predicts the measured rate on every one of those rows to within a tenth of a
percent. There is nothing left to explain.

### What it does to the rest of this document

The specced count was never equal between the two sides of a comparison, and it ran consistently
against the two worker runs:

| pair | 2 workers | 4 workers |
|---|---|---|
| first worker sweep | 538 specced | 337 specced |
| after the lock skip | 1,259 specced | 233 specced |
| the A/B repeat | 1,058 specced | 127 specced |

So each of those pairs compared a lighter load against a heavier one and read the difference as
scaling. The throughput comparison the worker count was tuned on does not survive that.

The efficiency table in the section above is hurt less, because scans per core-second divides work by
the processor time spent on it and both fall together — but it is not clean either. The two worker
runs were saturated at 99–102% and the four worker runs were not, at 77–78%, and that split lines up
exactly with the spec contamination. Comparing a thread that is pinned against one that is waiting is
a different measurement from comparing two thread counts.

### And a second leak, still unexplained

The saturated pair had **zero** clients specced and still broadcast far less than was offered: 40,374
mainloop work items a second against 16,541 broadcasts at two workers, and 39,808 against 20,078 at
four. Packets arrived, were dispatched, and produced nothing, with no spec involved and only about
thirty malicious-packet lines in the entire log.

The remaining ways out of `HandlePositionPacket` are a bad checksum, a post-death `(-1, -1)`, and a
timestamp that is not newer than the last one recorded. The third is the plausible one: the load
generator shares the machine, these are the runs where it works hardest, and a client whose send loop
bursts can put two packets in the same ten millisecond tick, the second of which the server correctly
discards. That is a guess, and it is the next thing to measure rather than something to build on.

### What the harness does now

It reports the specced count and the ratio of broadcast to offered on every run, and warns when the
second falls below ninety percent — whatever the cause, spec or otherwise. It already reported
assertion failures and error counts, and this belongs beside them for the same reason: a run that
quietly measures three quarters of the load it asked for produces a number that looks entirely
plausible and means nothing.

## The same work, more processor time

The load that went missing invalidated the runs the worker scaling claim rested on, so it had to be
established again on runs the harness could vouch for. The configuration to do it with was already in
the document: the players spread across the whole map, which cuts recipients per packet to about
twenty and leaves the candidate scan as the bulk of what a worker does. Three runs, one variable.

| workers | offered → broadcast | specced | scanned/pkt | in-view | read locks/s | arena worker cores |
|---|---|---|---|---|---|---|
| 1 | 40,000 → 39,997 | 158 | 100.0 | 19.9 | 161,321 | **76%** |
| 2 | 39,998 → 40,046 | 176 | 100.0 | 19.9 | 161,541 | **92%** |
| 4 | 40,001 → 39,996 | 172 | 100.0 | 19.9 | 161,399 | **120%** |

Every run broadcast everything it was offered, so all three carried the same load. The work per
packet is identical to a tenth of a recipient, the total is identical to a tenth of a percent, and the
player lock rate — the thing this used to be blamed on — is identical to a fifth of a percent. The
only thing that differs is how many threads it was spread over, and four of them need **1.59 times**
the processor time that one does to do it.

Repeated later on a second set of three runs: 75%, 91%, 119%. Within a point at every worker count.
Whatever this is, it is not noise and it is not the measurement.

One caveat on the first triple, found afterwards and worth stating: its settled windows varied by up
to 36%, so a single window out of it was not safe to compare. The repeat runs varied by 2-3% and the
pinned pair below by 0-3%, and they agree with it — but that is luck rather than method, which is why
the harness now reports the spread on every run.

### What it is not

Adding to the list, because eliminating a suspect is worth as much as finding one:

**Not the cost of blocking on an empty queue.** A worker takes one item at a time from a
`BlockingCollection`, and the blocking form is expensive when the queue is empty — it builds a linked
cancellation source and registers on the one every worker shares. That scales exactly the wrong way:
four workers each get a quarter of the same work, so each finds its queue empty far more often than
one worker does. It fits the shape of the problem well enough to be worth trying.

It was wrong. Blocking once and then draining everything already queued, up to a batch of 128, moved
one worker from 76% to 75%, two from 92% to 91%, and four from 120% to 119%. Nothing, at any worker
count, so the change was reverted rather than kept for the tidiness of it.

**Not the pooled object crossing the handoff.** The mainloop also gets more expensive across the same
three runs — 18%, 22%, 23% — while its work per packet does not change at all, which points at the
handoff between them rather than at either side. Two pooled objects cross it per position packet: the
`PositionBroadcast` and the work item carrying it, both rented on the mainloop and returned on a
worker. With one worker a recycled object shuttles between two cores; with four it comes back from
four in turn. That is a cache line travelling further for the same work, and it grows with the number
of consumers while the instruction count stays flat.

Allocating both instead of pooling them, so that each is written once, read once and dies, left one
worker at 76% and four at 120% — the pooled figures to the point. It cost 11.8 MB/s of allocation
against 4.9, and gen0 collections roughly doubled, so the pool is earning its place; it is simply not
what this is.

**Not the load generator competing for cores.** The generator shares this machine and is the larger
consumer, so four runnable threads asking a contended scheduler for time is not the same experiment
as one. Per-thread counters made that look promising: from one worker to four, voluntary parking
roughly doubled per thread, but *involuntary* switches — the scheduler taking the core away mid-work,
each one resuming on a cold cache — went from 4-76 per ten seconds to eight and twelve thousand.

Pinning the server to CPUs 16-31 and the generator to 0-15, so neither can preempt the other, made it
slightly worse rather than better:

| | 1 worker | 4 workers | ratio |
|---|---|---|---|
| sharing the machine | 75% | 119% | 1.59 |
| opposite halves | **74%** | **124%** | **1.68** |

So the preemption is a symptom of the workers running dry, not the cause of the cost. (`taskset`
inside WSL2 pins to virtual processors the hypervisor still places as it likes, so this is weaker
evidence than pinning on real hardware would be. It is enough to rule the generator out, because
removing the competition did not remove the penalty.)

### Isolating the workers makes it worse — but the experiment was broken

> **Retracted.** The processors given to the workers were not four cores. CPUs 28 and 29 are threads
> of one core and 30 and 31 are threads of another, so "one dedicated processor each" put two workers
> on each physical core with their execution units shared. That accounts for the numbers below
> without any theory about distance, and the conclusion drawn from them — that the cost grows with
> how far apart the threads are put — does not follow. Kept because the reasoning is where the error
> is, and because the same mistake is easy to repeat: `lscpu` reports this machine as sixteen cores
> of two threads, which is not its real shape either.

The obvious remaining explanation was that a worker at thirty percent duty keeps losing its core to
something else and resumes on a cold cache, where one at seventy-five percent holds on to it. Giving
each worker a processor nothing else in the server touches should then have closed most of the gap —
mainloop, send threads and receive thread confined to CPUs 16-27, the workers alone on 28, 29, 30 and
31, and the load generator on 0-15.

It went the other way, and consistently:

| | 1 worker | 4 workers | ratio |
|---|---|---|---|
| sharing the machine with the generator | 75% | 119% | 1.59 |
| server and generator on opposite halves | 74% | 124% | 1.68 |
| one dedicated processor per worker | 77% | **133%** | **1.73** |

One worker costs the same however it is isolated, which is expected — almost nothing about its
situation changes. Four workers cost more the further apart they are put. That kills the cold cache
explanation, which predicts the opposite, and it kills scheduling with it.

What grows when threads are pushed onto separate cores is the distance data has to travel between
them. Pinning the mainloop to one group of processors and the workers to another guarantees that
every work item and everything it points at crosses between them, where a free scheduler can put a
producer and its consumer on cores that share a cache.

That also means the pool result above proves less than it looked like proving. Allocating instead of
pooling removed the *recycling* of an object between cores; it did not remove the *crossing*, since a
freshly allocated broadcast is still written on the mainloop and read on a worker. Only one mechanism
was eliminated, not the suspect.

## Timing the loop directly, after a profiler lied about it

Nine hypotheses had been eliminated without confirming anything, so the next step was to stop guessing
and measure where the worker's own time went. Two attempts, and the first was worse than useless.

**A sampling profiler cannot answer this.** `dotnet-trace` reported, with no ambiguity, that 95% of
the arena worker's time was `DateTime.UtcNow`. It is not: a clock read costs 14 ns on this machine,
so the two million of them in the sampled window account for 28 ms, not the 18 seconds attributed.
The managed sampler suspends threads at garbage collection safe points, so it reports the call that
happens to carry a poll rather than the code burning the cycles, and a tight loop with a poll every
iteration collects all of it. The give-away was in the stacks — `Thread.PollGCWorker` sitting under
`get_UtcNow` — but the check that caught it was arithmetic, not the tool.

**Timing the loop by hand did answer it,** and found a defect worth more than the investigation. The
wait handed to the queue take is how long until the next timer is due, and it was truncated to whole
milliseconds. A timer due in a fraction of one became a wait of zero: the take returns immediately,
the timer is still not due, and the worker goes round again as fast as the processor allows until it
is. With a periodic timer per arena there is nearly always something due inside the next millisecond.

Per ten seconds, at four thousand players over forty arenas:

| | one worker | four workers |
|---|---|---|
| items of real work | 400,000 | 400,000 |
| loop iterations, before | **2,800,000** | **4,800,000** |
| loop iterations, after | 400,077 | 400,349 |
| arena worker processor time | 76% → **72%** | 121% → **102%** |

Four workers gained the most because the spin fills whatever idle time a worker has, and four workers
at a quarter of the load each have three times as much of it. Rounding the wait up instead of
truncating it is the whole fix.

This had been inflating every arena worker measurement since the timers were added, and inflating it
more the more workers were running — so every figure earlier in this document overstates the penalty.
It does not remove it. The eliminations survive, because both sides of each comparison carried the
same spin, but the batch drain result is now explained rather than merely negative: with one iteration
per item there is never anything queued behind the item being taken, so batching the take had nothing
to batch.

### What is left is real, and it is the work itself

With the spin gone, the time spent in the fan-out is measured directly, and it is not the same on
both sides:

| | one worker | four workers |
|---|---|---|
| work per item, before the fix | 15.6 µs | 22.5 µs |
| work per item, after the fix | **15.7 µs** | **21.1 µs** |
| total fan-out time | 63% of a core | 85% of a core |

Same 400,000 items, same 100.0 candidates scanned, same 19.9 recipients, same player lock rate — and
a third more processor time per packet to do it. That is the answer to the question this section
started with: it is not queueing, not scheduling, not overhead around the work. The same instructions
run slower, which is what memory-bound work spread across more cores looks like, and it fits the one
positive signal already found — that separating the workers onto their own processors makes it worse
rather than better.

### It is the sends, and the scan gets faster

Splitting `BroadcastPosition` the same way, with the four `SendToOne` calls timed individually and
everything else counted as the scan:

| | 1 worker | 4 workers | |
|---|---|---|---|
| per send | 0.455 µs | 0.599 µs | **+31%** |
| per candidate scanned | 0.147 µs | 0.125 µs | **−13%** |

Both movements are consistent across all four workers and both windows, and they go in opposite
directions, which is what makes them worth trusting: a measurement artefact such as blocking on the
player lock would inflate both.

That is the sharing, laid out. The candidate scan reads player state belonging to the worker's own
arenas, so it is private to that worker and its working set shrinks from four thousand players to one
thousand — and it gets cheaper, by an eighth. Every send writes into a connection owned by whichever
*send thread* holds it, which has nothing to do with arenas, so four workers write into the same set
of connections from four cores instead of one — and that costs a third more per send.

**Not the shared per-recipient counters**, re-tested because the original test predated the busy wait
fix and the harness checks. Two of them are written once per recipient, about 800,000 times a second
here: the `SendToOne` metric and the bandwidth priority statistic, which every position packet adds to
the same array slot. Removing both:

| arena worker processor time | counters in | counters out |
|---|---|---|
| one worker | 78% | 74% |
| four workers | 123% | 121% |

Four points at one worker and two at four. They are a real fixed cost on a hot path — worth removing
on their own merits, and the metrics interface's own rule already says a loop at that rate should
tally locally — but they are not what scales. The ratio is unchanged at 1.6, so the earlier
elimination was right, for better reasons than it had at the time.

### What is left

Two pooled objects are taken per *recipient* rather than per packet, inside the connection lock in
`SendOrBufferPacket`: a `SubspaceBuffer` and the linked list node that carries it. Both come from a
process-wide `Pool<T>` over a `ConcurrentQueue`, both at around 800,000 a second, and both are rented
on an arena worker and returned on a send thread once the packet has gone out.

That is the same cross-core recycling that was tested on the broadcast object and found to be nothing
— at twenty times the rate. The broadcast object is taken once per packet, forty thousand times a
second; these are taken once per recipient. With one worker there is a single thread dequeuing; with
four there are four, on the same queue head, each handed memory last written by a different core.

It is the only remaining thing in the send path that scales with the number of workers rather than
with the amount of work, and it is testable by giving each worker its own pool: `PooledObject` already
records which pool it came from, so returns from the send threads find their way back without any
bookkeeping.

**Tested, and it is not that either — the sharing was helping.** One pool per renting thread, which
cycles correctly (allocation stayed at 4.9 MB/s, against the 11.8 MB/s of the run that bypassed a
pool entirely):

| | shared pool | one pool per thread |
|---|---|---|
| one worker | 72% | **69%** |
| four workers | **104%** | **113%** |
| ratio | 1.44 | 1.64 |
| whole server | 3.09 cores | 3.47 cores |
| working set | 336 MB | 373 MB |

Three points better at one worker and nine worse at four, so the scaling it was meant to fix got
worse. The working set says why: splitting the pool four ways puts four times as many distinct
buffers in circulation, and each worker gets back memory a send thread wrote last. One shared pool
keeps a smaller, hotter set cycling, and that is worth more than the queue contention it costs.

Which is the same lesson as the broadcast object, arrived at from the opposite direction: pooling
here is not overhead to be removed or split up, it is what keeps the working set small enough to stay
in cache. The remaining candidates in the send path are the connection lock and the outgoing queue
themselves — real work rather than bookkeeping, and not obviously removable.

## Tallying the per-recipient counters locally

Not a fix for any of the above, and worth recording for the shape of the result rather than its size.
Two things were counted on every send to every recipient: the `SendToOne` metric, and a shared array
slot for bytes at that bandwidth priority which every position packet adds to at the same index. Both
are single cache lines written by every thread that sends, at several million a second in the default
configuration — the rate `IServerMetrics` itself says must be accumulated locally rather than reported
per operation.

Each thread now keeps its own counts and adds them to the shared ones every thousand sends:

| | before | after |
|---|---|---|
| one worker | 72% | 73% |
| four workers | **104%** | **100%** |
| ratio | 1.44 | **1.37** |
| whole server | 3.09 cores | **2.94 cores** |

Nothing at one worker — slightly worse, in fact, because three thread-local accesses cost about what
the two uncontended interlocked operations they replaced did. Four points at four workers, where those
lines are genuinely bouncing between cores. That is the shape to expect from removing contention
rather than removing work, and it is worth knowing that the *earlier* figure for this, four points at
one worker, came from a build carrying the fan-out instrumentation and did not survive being measured
on a clean one.

The reported figures stay right: 796,111 sends a second against 796,083 before.

## Removing work stops helping once there are four workers

*Explained below, under "Why four workers cost more than two": the extra was never in the work. It
was the wait between items, which spun and yielded, and none of the removals here could touch it.*

The node pool was the last shared object on the send path that had not been tested — a
`DefaultObjectPool` whose fast path is a single field settled by an interlocked compare-exchange,
taken from once per recipient. Replacing it with plain allocation, which removes it from the path
entirely at the cost of 42 MB/s of very short lived objects against 4.9:

| | pooled | allocated |
|---|---|---|
| one worker | 73% | **67%** |
| four workers | 100% | 101% |

Six points better at one worker, nothing at four. Worth knowing on its own that the pool is losing to
the allocator even uncontended — a generation zero allocation is close to a pointer bump, while the
pool costs at least an interlocked compare-exchange and an array scan when it misses — but the ratio
got worse, so it is not what scales.

Put beside the two experiments before it, that is the useful result:

| what was removed | at one worker | at four workers |
|---|---|---|
| the shared per-recipient counters | −4 points | −0 points |
| the shared buffer pool, split per thread | −3 points | **+9 points** |
| the node pool, replaced by allocation | **−6 points** | +1 point |

**Every change that takes work out of the path helps at one worker and does nothing at four.** Four
workers sit between 100% and 104% whatever is removed. Three independent attempts is enough to stop
treating that as coincidence: at four workers the arena workers are not limited by their own
instruction stream. They are waiting, and removing instructions from a thread that is waiting changes
nothing.

The obvious explanation was that each worker is idle three quarters of the time, wakes with nothing in
cache, and spends its time waiting on memory. Giving each worker a processor nothing else touched
should then have helped, and the earlier attempt at that had been discounted because it ran with the
timer busy wait still present.

Redone on the fixed build it still went the wrong way — 73% to 68% at one worker, 100% to 110% at
four — and the reason turned out to have nothing to do with caches. **The four processors were two
cores.** CPUs 28 and 29 are threads of one core, 30 and 31 of another, so four workers on 28 through
31 is two workers per core sharing execution units, which is the opposite of the isolation intended.
One worker on CPU 28 did have a whole core, with its sibling idle, which is why that side improved.

Both isolation results, this one and the earlier 133%, say only that two threads on one core are
slower than one. The experiment has to be run one thread per *physical* core to mean anything, and
`lscpu` will not help: it reports this machine as sixteen cores of two threads, which is not the shape
of a processor with eight performance cores and sixteen efficiency cores either. The sibling lists
under `/sys/devices/system/cpu/*/topology` are what to read.

Run properly — workers on CPUs 24, 26, 28 and 30, which are four separate cores with their sibling
threads left idle, the rest of the server on 16-23 and the generator on 0-15 — it gives the same
answer for a good reason instead of a bad one:

| | unpinned | one physical core each |
|---|---|---|
| one worker | 73% | **67%** |
| four workers | 100% | **104%** |

A single worker gains six points from owning a core. Four gain nothing, which is what kills the
explanation: idle three quarters of the time is exactly the case a private core should rescue, and it
does not.

So the table above gains two more rows and the same signature. Five interventions — remove the shared
counters, split the buffer pool, drop the node pool, dedicate cores, dedicate them correctly — and
every one helps a single worker while none helps four. Each worker costs about 26% of a core for ten
thousand packets a second, and that figure has not moved for anything tried against it.

### What none of them touched

Every run in this document has `Net:HandlePacketsOnArenaThread` off. The mainloop is therefore what
writes `playerData.Position` for all forty thousand packets a second, and the workers read that same
player state while scanning — a hundred candidates a packet. Writer and readers are on different cores
by construction, and nothing done to the reader can change that. It is the one sharing relationship
that survives every experiment above, because every experiment above was aimed at the reader.

The setting exists to move the write onto the thread that does the reading. Its own documentation says
to try it on a debug build first, since every packet handler in the server was written when the
mainloop was the only thread that ran them, and the threading assertions are there to find whichever
of them is not ready for that — which is what the load tests here are, so the assertions were already
live.

### Handling packets on the arena thread is worth thirteen percent, at one worker

*Re-measured in Release with the yield fix in, at two workers: a three percent loss. See "The cast
that was a call" below. This figure was taken in Debug with the spin present.*

Turned on, with no assertion failures and no errors at either worker count:

| | mainloop handles packets | arena thread handles packets |
|---|---|---|
| one worker, the workers | 73% | 75% |
| one worker, **whole server** | 2.94 cores | **2.57 cores** |
| four workers, the workers | 100% | 112% |
| four workers, whole server | 2.94 cores | 2.97 cores |

At one worker the mainloop effectively disappears — 74 iterations a second against 2,686, no longer
using enough processor to appear among the threads at all — and absorbing all of its work costs the
arena worker two points. The whole server does the same job for thirteen percent less.

At four workers, absorbing the same work costs twelve points and the total does not move. That is the
sixth intervention with the same signature, and the first that changed what is shared rather than how
much work is done. It behaves like all the others.

**What this is good for is the configuration, not the mystery.** The cheapest way to serve this load
is one arena worker with packets handled on it: forty thousand packets a second for 2.57 cores,
against 2.94 to 2.97 for every four worker arrangement tried. More workers have not been worth their
cost at any load this machine can produce.

### Sending weapons and deaths through it found two real defects

The positions-only run was clean, which said less than it looked like saying: one of the seventeen
arena-scoped packet types, and none of the paths where the risk actually is. Twenty percent weapon
fire and six deaths a client a minute found two concurrency defects, neither of which can happen while
the mainloop handles packets, because the mainloop is also what changes the state they race against.

**A packet can outlive the routing decision that placed it.** Which thread handles a packet is decided
when it arrives, from the sender's arena at that moment. The sender can leave before the arena's
worker gets to it, and the worker then runs a handler for a player whose state it no longer owns,
beside whichever thread now does — the one thing a single thread per arena exists to prevent. The
assertion caught it and took the server down with it:

```
MainloopWork_CallPacketHandlers must run on the mainloop or on the thread owning
arena '(none)', but ran on 'ArenaLoop-0'.
```

Fixed by dropping the packet. Everything routed this way is that arena's business, so one for an arena
the sender has left has nothing to act on, and handing it to the mainloop instead would let it
overtake packets that went there directly while the player was leaving.

**The sender's ship was read twice around an array bound.** The position handler tests `player.Ship`
against spectator and then reads it again, four times, to index an array with one entry per ship —
which spectator is not. The mainloop can spec a player between the test and the index; LagAction does
it for ping and so does a death. Six `IndexOutOfRangeException` in one run, on six different players.
Fixed by reading it once into a local.

With both fixed, a sustained combat run — a thousand clients over ten arenas, 7,463 deaths, nothing
specced — held 9,991 packets a second of the 10,001 offered, steady to within a percent across every
window, with no assertion failures and no errors.

### What combat cannot measure here

At four thousand clients the same combat load collapses, and on both code paths. With packets on the
arena thread LagAction specced 3,949 of the 4,000 and throughput fell to ten packets a second; with
them on the mainloop it collapsed too, from something else, with only 230 specced. Two runs that both
failed cannot be compared with each other, so there is no throughput comparison for combat at that
scale — the thirteen percent above is a positions-only figure and should not be quoted for anything
else.

## One packet per Select was the ceiling, and it was not the buffer

Chasing where the missing quarter of the load went: the listening socket's own drop counter, from
`/proc/net/udp`, moved **1,136,376** in one six thousand client run, which is the 6,900 a second that
never reached the mainloop. Worth noting against an earlier section of this document, which concluded
the server was never the one losing packets because its receive *queue* read empty at every sample —
the drops are bursty, and only the cumulative counter catches them.

**Not the buffer.** Raising the receive buffer to the system maximum, four times the default, left
throughput identical at ~41,000 packets a second and simply queued more before dropping it. More
clients connected so more was offered, and the drops rose to match. That is a throughput limit, not a
buffering one, and no amount of configuration fixes it.

**One packet per system call was the limit.** The receive thread called `Socket.Select`, took a single
packet, and went back to `Select` — which rebuilds and walks its socket list every call, so the whole
zone's incoming traffic cost two system calls a packet. That also explains the shape of it: the thread
sat at 15% of a core while its socket overflowed, because it was short of packets per call rather than
short of processor.

Draining each readable socket instead, bounded at 64 so a game socket carrying the zone cannot starve
the ping socket beside it:

| | one packet per Select | drain per wake |
|---|---|---|
| connected | 4,844 of 6,000 | **6,000 of 6,000** |
| broadcast | 41,509 pkt/s | **55,069 pkt/s** |
| socket drops | 1,136,376 | **115,137** |
| specced for lag | 54 | **0** |
| arena worker | 69% | **111%** |

A third more throughput, every client connecting rather than one in five losing its attempt, ninety
percent fewer drops — and the arena worker is now the limit, at 111%. That is the first time anything
here has driven a worker to the wall, which is the condition the parked worker question was waiting
for.

## Letting the send thread's lock go fixes it, and ten thousand players work

The send thread held the player read lock for its whole pass, across a socket write per connection, and
with several of them on overlapping schedules there was never a moment when none held it. A
`ReaderWriterLockSlim` stops admitting readers once a writer waits, but that only helps if the readers
already inside let go, and these did not until the pass ended.

Letting it go every 64 connections and taking it again is the entire fix. Ten thousand clients over a
hundred arenas, staggered, five minute hold:

| | collapsing | after |
|---|---|---|
| connected | 9,328 of 10,000 in 118s | **10,000 of 10,000 in 48s** |
| broadcast | 11,052 pkt/s | **48,856 pkt/s** |
| write waited | 11,605 ms | **0.0 ms** |
| mainloop | 14% of a core, queue 19,061 | 53%, queue 4,602 |
| arena workers | 12% each | **110% and 109%** |
| specced for lag | 370 | **0** |

Four times the throughput, every client connecting in a third of the time, and the arena workers are
the limit again rather than sitting idle behind a blocked mainloop.

### Bounding the queue, which changes the comparison below

An overloaded worker with no queue limit does not shed, it accumulates. Offered four times what it
could carry, one grew a two gigabyte managed heap and swung sixty percent between windows — those
swings being collections rather than anything about the server's rate. Turning the limit on:

| ten thousand players | no limit | limit of 2,000 |
|---|---|---|
| one worker | 27,357 pkt/s, 2,053 MB, 60% spread | **58,870 pkt/s, 187 MB, 9%** |
| two workers | 48,856 pkt/s, 2,749 MB, 5% spread | **62,614 pkt/s, 218 MB, 8%** |

Bounded memory, steady throughput, and **nothing dropped** — the mainloop's own limit was never
reached. Nothing needed dropping, because the fan-out a full arena queue refuses runs on the mainloop
instead: slower for that packet, free for the ones behind it. Now the default.

**This changes what the worker comparison means.** With the queue bounded, adding a second worker is
worth six percent rather than the seventy-eight the unbounded runs suggested — because in the one
worker case the mainloop picks up the overflow and becomes a second fan-out thread in all but name:

| | threads doing fan-out | throughput | per fan-out core |
|---|---|---|---|
| one worker + mainloop | 102% + 93% = 1.95 | 58,870 pkt/s | **30,190 pkt/s** |
| two workers + mainloop | 103% + 103% + 40% = 2.46 | 62,614 pkt/s | **25,453 pkt/s** |

Per core of fan-out, the arrangement without the second worker is sixteen percent better. The mainloop
doing the work inline beats handing it to a worker, which is the handoff cost showing up from a
direction none of the earlier experiments approached it from.

### Everything measurable here has now been ruled out

The penalty matters for a machine larger than this one: per worker-core, two workers retire 29,783
packets a second and four retire 22,243, so four are a quarter less efficient at the thing you would
buy more cores to do. Worth knowing what it is before scaling out.

The runtime's own counters were the last untried instrument, and they close the last software
suspect. Same load, same work per packet, one worker against four:

| | one worker | four workers | |
|---|---|---|---|
| monitor lock contentions | 28/s | 77/s | against ~800,000 lock acquisitions a second |
| garbage collection heap size | — | *smaller* at four | so not collection |
| heap fragmentation | — | *lower* at four | |
| thread pool queue | 0 | 0 | |

Contention rises by less than three times, from four thousandths of a percent of acquisitions to one
hundredth. At ten microseconds a blocking event that is under a tenth of a percent of a core, against a
cost of twenty-five. **The connection lock and the queue behind it are not it**, and neither is the
collector, which does less work at four workers rather than more. Nothing else in the runtime's
instrumentation moves.

So the list of things this cannot be now runs: the per recipient counters, the buffer pool, the node
pool, dedicated processors, dedicated *physical* processors, the shared player lock, blocking on an
empty queue, the mainloop writing what the workers read, the connection lock, and the collector.

What is left is below the level anything here can see: how far the data travels between cores. That
needs the processor's own counters, and this machine has none —
`/sys/bus/event_source/devices` offers `software` and `tracepoint` events and no `cpu`, because WSL2
does not expose a performance monitoring unit. It is not that `perf` is missing; there is nothing for
it to read.

**On real hardware this is one run.** `perf stat -e cache-misses,LLC-load-misses,LLC-store-misses`
around the same comparison — four thousand players over forty arenas, spread out, one worker against
four — would say in a single pass whether four workers take more cache misses for the same work. If
they do, it is data moving between cores and the fix is in how arenas, connections and send threads
are grouped. If they do not, everything above is wrong and it is something none of this has touched.

### What a worker is actually worth, measured at saturation

With the queue bounded and each configuration driven until it could take no more, ten thousand players
over a hundred arenas:

| workers | throughput | whole server | the workers do | the mainloop backfills |
|---|---|---|---|---|
| 1 | 58,870 pkt/s | 7.11 cores | 36,388/s | **22,484/s** |
| 2 | 62,614 pkt/s | 8.19 cores | 61,353/s | 1,262/s |
| 4 | 63,393 pkt/s | 9.26 cores | 63,393/s | 0 |

**Four workers cost two and a bit more cores than one for seven percent more throughput.** Every
earlier statement in this document about workers buying capacity sub-linearly — 1.6 times at two, 2.9
at four — was extrapolated from cost at a fixed load because they could not be saturated at the time.
Measured, four is 1.08 times one.

The reason worker count matters so little is in the last column. A full worker queue sends the fan-out
back to the mainloop, so the mainloop backfills whatever the workers cannot take, and the work gets
done either way. The setting mostly decides which thread does it. That is also why one worker is not
the answer despite being the cheapest: the mainloop is at 93% doing that backfilling, and it has a
zone to run besides. At two it is at 40% and the workers carry essentially all of it.

The default is now two rather than a share of the processor count.

### The comparison as it stood on unbounded queues

Both worker counts can now be driven to the wall at the same load, which was impossible while the
receive path, the connect burst, and then the write lock each ran out first. Same ten thousand clients,
same hundred arenas, same 46 recipients a packet, and only the worker count differing:

| workers | saturated at | against one |
|---|---|---|
| 1 | ~27,400 pkt/s (110%) | 1.00 |
| 2 | 48,856 pkt/s (110% + 109%) | **1.78** |

So the tax is real and it does cost capacity: two workers carry about 1.78 times one, not two. Roughly
a ninth of the second worker is lost to whatever the thirty-seven percent penalty is, which is the
first time that has been measured rather than extrapolated.

**The one worker figure is approximate.** Its windows ranged from 18,890 to 35,443 — a sixty percent
spread — because saturating one worker means offering it nearly four times what it can take, and there
is nothing to refuse the excess. `ArenaLoop:MaxQueueDepth` defaults to no limit, so the overflow queues
instead: two gigabytes of managed heap in that run, and the swings are collections on it rather than
anything about throughput. The two worker run is clean at five percent.

That missing backpressure is worth fixing on its own account. A server that is offered more than it can
carry should refuse the excess, which is what the setting exists for, rather than growing a queue until
the collector notices.

## Before the fix: around nine thousand players the write lock starved and the server stopped recovering

The saturation comparison still could not be made, and this time the reason is worth more than the
comparison would have been. Two workers need about nine thousand players to reach the wall, and at
that population the server enters a state it does not come out of.

Ten thousand clients over a hundred arenas, connections staggered three milliseconds apart, a five
minute hold. 9,328 connected, and then:

| | |
|---|---|
| offered | 44,260 pkt/s |
| broadcast | **11,052 pkt/s**, a quarter of it |
| mainloop | 75 iterations a second, 13 ms each — but **14% of a core** |
| mainloop queue | **19,061 deep** |
| player lock | write held 118 ms, **write waited 11,605 ms** |
| whole server | 1.91 cores of 32 |

The mainloop is busy in elapsed time and idle in processor time, which means it is blocked rather than
working, and the lock figures say on what. Readers hold the player lock: the send threads take it
across a whole pass, and at nine thousand connections a pass is long. The mainloop is a writer — every
connect and player state change needs it — and it waited eleven seconds out of ten. Its queue grows
past nineteen thousand, packets pile up behind it, and the arena workers sit at 12% each with almost
nothing reaching them. The server does not shut down afterwards either; both runs had to be killed.

**Not caused by draining the receive socket**, which was the obvious suspect since that change removed
the packet loss that had been throttling the input. The identical load on the build immediately before
it collapses the same way:

| | before the drain change | after |
|---|---|---|
| connected | 9,323 of 10,000 | 9,328 of 10,000 |
| broadcast | 11,427 pkt/s | 11,052 pkt/s |
| arena workers | 12% + 12% | 12% + 12% |
| mainloop | 14% of a core | 14% of a core |
| write waited | 7,698 ms | 11,605 ms |

Same collapse, same mechanism, one commit apart. The drain change lets the server reach this point
rather than causing it.

This is the ceiling that matters now, and it is not the arena workers — they are barely awake through
it. It is the one figure this document has been watching grow for a while: the global player write
lock, 6.5 ms of wait at three thousand players, 19.9 ms at six thousand, and eleven seconds at nine.

## Before that: the workers could not be saturated, because something else ran out first

The ceilings below are extrapolated from cost at a fixed load, so the obvious thing to do was measure
them: offer one worker and then two about ten percent more than each was predicted to carry, and see
what each actually retires. Spread out, so that a packet costs the worker a hundred candidate scans
while costing the generator only twenty recipients to receive — which is what makes loading the server
possible without loading the generator.

Neither got close:

| | offered | reached the mainloop | broadcast | arena workers | headroom |
|---|---|---|---|---|---|
| one worker, 6,000 clients | 48,069 | 37,056 | 36,988 | 62% of 100% | 38% |
| two workers, 10,000 clients | 62,777 | 47,061 | 46,442 | 145% of 200% | 55% |

About a quarter of what the clients sent never reached the mainloop's work queue, which drops nothing
itself — so it was lost before that, in the receive path or on the way there. The connections tell the
same story: 4,848 of 6,000 and 6,853 of 10,000 clients ever connected, which is the connection init
dropping under burst arrival that this document already records.

**So worker capacity is not measurable on this machine.** Not because the workers are too fast to
load, but because nothing here can deliver enough packets to them: they still had 38% and 55% of
themselves spare at the point where a quarter of the offered load was already going missing. The
figures in the table below stay extrapolations.

That is worth more than the measurement it failed to make. At every load this setup can produce, the
arena workers are not the constraint — the receive path is, and it is a single thread.

## What a worker costs, and what it buys

Filling in the two worker case on the current build completes the picture, and it is less bleak than
"more is not better" suggested:

| workers | same work costs | against one worker | ceiling, extrapolated |
|---|---|---|---|
| 1 | 73% of a core | 1.00 | ~55,000 pkt/s |
| 2 | 89% between them | 1.22 | ~90,000 pkt/s, 1.6 times |
| 4 | 100% between them | 1.37 | ~160,000 pkt/s, 2.9 times |

The marginal cost falls rather than rises — one to two costs sixteen points, two to four costs eleven
— so workers are sub-linear rather than wasteful. Four of them buy nearly three times the ceiling for
just over a third more processor time, which is worth paying when the ceiling is what is wanted.

The ceilings are extrapolations from a worker being able to reach a whole core, and they are
optimistic: a saturated worker costs about a fifth more per packet than one at half load, measured
below. Treat them as an ordering rather than as numbers.

What actually decides the setting is that an arena belongs to the worker it was created on for the
rest of its life. Raising it once a zone is full does not move the arenas that are already busy, so it
has to be sized for the peak — which is why the default takes its cue from the processor count, as a
stand-in for how large a zone the machine was meant to host.

## What one worker can carry, and why the ratios are not capacity

Everything above holds the load constant and varies the worker count, which says what a worker costs
but not what it can carry. Those two are the same thing only if cost per packet does not depend on how
busy the worker is, and it does. One worker, identical work per packet — a hundred players an arena,
about ninety recipients — at two very different loads:

| | half load | saturated |
|---|---|---|
| throughput | 15,009 pkt/s | 27,494 pkt/s |
| arena worker | 50% | **108%** |
| **cost per packet** | **33.3 µs** | **39.3 µs** |
| broadcast of what was offered | 100% | **91%** |
| working set | 290 MB | 452 MB |

**A saturated worker costs about a fifth more per packet than one at half load.** Nothing was specced,
so the missing nine percent is the server itself: with no queue depth limit the backlog simply grows,
which is what the working set is doing, and throughput settles at what the worker can actually retire.

So the worker count comparisons above, all made between 25% and 73% busy, measure the cheap end of
that range. They are still a fair comparison with each other — both sides of every pair sat in the
same range — but the ratio between them is not a capacity ratio, and four workers will not carry four
times 27,300 packets a second.

What can be said: **one arena worker sustains around 27,300 position packets a second** at ninety
recipients each, which is roughly 2,700 players at ten updates a second in one arena group.

What cannot be said is what four carry, and not for want of trying. Four workers would saturate
somewhere near 87,000 packets a second, which at ninety recipients is nearly eight million updates a
second flowing back to the load generator — twice what it has ever sustained. Raising the recipients
per packet to reach saturation at a lower packet rate makes the generator's receive side worse rather
than better. That measurement needs a second machine.

## On hardware with a real PMU: a cliff at four send threads, and it is probably the box

Every cache explanation in this document up to here was a hypothesis that could not be tested, because
WSL2 exposes no PMU — `/sys/bus/event_source/devices` has no `cpu` entry, so there is nothing for
`perf` to read. The zone's VPS is 8 vCPUs of AMD EPYC under KVM with the counters exposed, and the
comparison recorded at the end of the previous section has now been run there.

The A/B is the send threads, not the arena workers. `ArenaLoop:Workers` was 2 in both runs. 2,000
clients over 20 arenas, `--spread 16384`, 150 seconds, with `perf stat -e cache-misses,cache-references`
attached to the server process for a 30 second window inside the settled part of the run.

| | 2 send threads | 3 send threads | 4 send threads |
|---|---|---|---|
| broadcast | 20,037 pkt/s | 20,007 pkt/s | 19,995 pkt/s |
| outgoing updates | 385,751/s | 383,449/s | 383,790/s |
| server processor | **2.93 cores** | **2.94 cores** | **3.48 cores** |
| busiest send thread | 85% | 61% | 58% |
| cache references | 468.2 M/s | 488.7 M/s | 560.8 M/s |
| cache misses | 66.6 M/s | 68.2 M/s | 76.8 M/s |
| miss ratio | 14.23% | 13.95% | 13.70% |

**The third thread is free and the fourth is not.** Two and three threads cost the same processor time
to the hundredth of a core, while the busiest thread falls from 85% of its interval to 61%. The step
to four costs 19%.

The counters track the processor time exactly at all three points. Per outgoing update: 1,214 → 1,275
→ 1,461 references, 173 → 178 → 200 misses. So the relationship established below holds — the work
being done and the memory being touched move together — but it is a cliff between three and four
threads, not a cost per thread.

Per core-second the counters are flat across all three: 159.8, 166.3, 161.1 M references and 22.7,
23.2, 22.1 M misses. **The threads are not stalling more per unit of time; the four-thread
configuration touches more memory for the same output.** Same instructions running slower would show
as misses per core-second going up, and that does not happen at any of the three points.

The miss *ratio* falls monotonically, 14.23 → 13.95 → 13.70%. Colder caches would raise references and
misses together and hold or worsen the ratio. References rising faster than misses is the signature of
lines fetched but found in another core's cache rather than in memory.

Where the processor time went, by thread:

| | 2 threads | 3 threads | 4 threads |
|---|---|---|---|
| send threads | 85 + 82 = **167%** | 61 + 60 + 55 = **176%** | 58+53+53+47 = **211%** |
| arena workers | 51 + 50 = 101% | 48 + 47 = 95% | 56 + 55 = 111% |
| receive + mainloop | 24% | 23% | 26% |

The arena workers get *cheaper* at three threads and dearer at four, which no story about the send
threads alone explains — the producers are being affected by something that only appears at four.

### The four-thread cliff is most likely the machine, not the code

The two-point fit recorded here first — 22.1% of a core constant per thread plus 0.0614% per player —
predicted 3.15 cores at three threads. The measurement is 2.94. **There is no per-thread constant.**
Refitting on the two-and-three-thread points gives 9.0% plus 0.0745% per player, which predicts 46.3%
per thread at four and measures 52.8%: the four-thread run is 6.5 points per thread above its own
trend line.

What is special about four is the box. `loadtest.sh` runs the load generator on the same machine, and
2,000 clients receiving 379,000 updates a second is not cheap. At three send threads the server's hot
threads are 3 send + 2 arena + receive + mainloop = 7 on 8 vCPUs; at four they are 8, before the
generator, the garbage collector, the reliable thread and the sized send thread are counted.

The variance says the same thing. Three runs of the four-thread configuration cost 3.48, 3.64 and 4.33
cores; two runs of the two-thread configuration cost 2.93 and 3.09. A contended machine gives noisy
numbers; a fixed toll would not.

So the honest reading is that this pair measures **where this VPS runs out of processors while also
generating its own load**, and the coherence signature in the counters is what oversubscription looks
like from inside the process — threads descheduled, migrated, and refilling caches. It is not proof of
an intrinsic cost to running more send threads, and the three-thread point is evidence against one.

### What this means for the setting

On this box **three send threads is strictly better than two**: the same processor time, and the
busiest thread drops from 85% of its interval to 61%. The send threads were the binding constraint at
two — at 85% they are close to overrunning, and an overrun means outgoing data going out late — so
the third thread buys roughly 40% more population headroom for nothing. `Net:MaxSendThreads = 3`.

Refitting capacity on the honest points: a send thread saturates near 1,220 players owned, and the
server costs about `0.09 × threads + 0.00134 × players` cores. Three threads reach about 3,700 players
at 5.2 cores. Beyond that this box cannot both serve and generate, so the population sweep needs the
generator moved off it.

### What this does not settle

`ArenaLoop:Workers` was held at 2. The comparison named in `0ccc1ff` — one worker against four, same
work, counters attached — has still not been run, and the three-thread result above is a reason not to
assume the answer. The arena worker penalty was measured on a machine with 32 logical processors at
73%, 89% and 100% of a core for one, two and four workers, which is nowhere near filling it, so
whatever explains the four-send-thread cliff on an 8 vCPU box does not obviously explain that. **They
are two open questions, not one**, and this section should not be read as having closed either.

Nothing here separates oversubscription from an intrinsic cost, because no run recorded what the whole
machine was doing — only what the server process was doing. Server processor time and the server's own
cache counters both look the same whether the server is slow because of coherence traffic or slow
because it is being descheduled.

`LLC-load-misses` and `LLC-store-misses` come back `<not supported>` under this hypervisor, so only the
aggregate generic events are available, and what they map to on a virtualised EPYC is not certain
enough to reason about absolute values. The ratios between two runs on the same box are the usable part.

### Two measurements, one of which was worthless

The first attempt ran `perf` by hand in a second terminal after the harness had already printed its
results — by which time the harness has shut the server down. `pgrep` still found a process, so the
window attached to a stray from an earlier run and recorded 957 M references against the 14,046 M the
same configuration produces under load. The arithmetic caught it: 10 M references per core-second is
one every 250 cycles, which is not what a busy server looks like.

The fix was to stop doing it by hand. A wrapper starts the load test, waits for the fourth `position:`
line in the server log, attaches `perf` to the running process for 30 seconds and then waits for the
harness. The four-thread run had in fact been timed correctly the first time and reproduces to within
7% on references and 4% on misses across the two attempts, which is the reason to trust the pair.

## The arena workers, with headroom and counters at last: two is the floor, and four is real

The comparison owed since `0ccc1ff`. 1,600 clients over 16 arenas — chosen so the count divides evenly
by one, two and four, since arenas are handed to workers round robin at creation (`ArenaLoop.cs:221`)
— `--spread 16384`, send threads pinned at two so the adaptive supervisor could not vary them, and
`perf` plus `/proc` over the same 30 second window inside the settled part of each run.

| | 1 worker | 2 workers | 4 workers |
|---|---|---|---|
| broadcast | 16,016 pkt/s | 15,999 pkt/s | 16,005 pkt/s |
| outgoing updates | 290,617/s | 289,563/s | 290,646/s |
| **server processor** | 2.78 cores | **2.39 cores** | 3.13 cores |
| send threads | 91 + 91 = **182%** | 74 + 74 = **148%** | 91 + 89 = **180%** |
| arena workers | 75% | 36 + 35 = **71%** | 27 × 4 = **108%** |
| receive + mainloop | 21% | 20% | 25% |
| cache references | 463.0 M/s | 388.3 M/s | 469.7 M/s |
| cache misses | 56.2 M/s | 50.4 M/s | 61.7 M/s |
| send thread overruns | 1,063 | 902 | 1,183 |
| working set | 231 MB | 238 MB | 294 MB |
| **machine idle** | 1.32 cores | 2.49 cores | **1.65 cores** |

**Two workers is a floor, not a compromise.** One costs 16% more processor time than two and four
costs 31% more, for work that is identical to within a tenth of a percent. The default of two, which
was picked for a different reason — that at one worker the mainloop was at 93% backfilling — turns out
to be the measured optimum on this machine.

**The four worker penalty is real and it is not the machine.** 1.65 cores were idle, so nothing was
waiting for a processor. This is the first time the penalty has been measured with headroom and
hardware counters at the same time, and it survives both. Worker processor time goes 71% → 108% for
the same work, which is the same +50% shape seen on the development machine, and the send threads go
up 32 points alongside it.

### One worker is dear for a different reason than four

The costs are not on the same thing. At four workers, **both** the workers and the send threads rise.
At one worker the workers are *cheap* (75% against 71%, and it is one thread rather than two) and the
whole of the excess is in the send threads, which do none of its work — 182% against 148%.

The obvious explanation, that a single bursty producer makes the send threads pass over their shards
more often, is **wrong**. Send thread iterations are 167 a second at one worker and 166 at two. Same
passes, same sends, 34 points more processor time. It is the cost of a pass that changes, not their
number. Note also that 167 across two threads is 83 each, against a nominal 100 — these threads are
already overrunning their 10 ms interval at this load, in every configuration.

### What the counters say about the four worker case

Per outgoing update, against two workers as the baseline:

| | 1 worker | 2 workers | 4 workers |
|---|---|---|---|
| references per update | 1,593 | 1,341 | 1,616 |
| misses per update | 193.5 | 174.0 | 212.2 |
| references per core-second | 166.5 M | 162.5 M | 150.1 M |
| misses per core-second | 20.2 M | 21.1 M | 19.7 M |

Four workers take 20% more references and 22% more misses per update, but **31% more processor time**.
That gap is the new information. Where the send thread comparison showed processor time and memory
traffic rising together, here processor time rises faster than the traffic that is supposed to explain
it, and references per core-second *fall*. The processor is spending time not issuing memory
references — stalled longer on each miss, or waiting on something.

Longer stalls for the same misses points at where the line comes from rather than how many there are.
The first guess was the fabric: this is an EPYC Naples part, and on that generation the L3 is not
shared across a package but across a group of four cores, so a line owned by a core in another group
crosses the interconnect. Eight vCPUs would be two such groups, which four workers might span and two
might not.

**The topology says no.** `shared_cpu_list` for the L3 is `0-7`, one cache for every processor, and
`thread_siblings_list` is a single processor each, so there is no SMT either. Nothing here to span.

That answer comes with a caveat which cannot be resolved from inside the guest: the machine reports
8 MB of L3 for eight cores, where two real Naples groups would be 16 MB, so what the guest is shown is
synthetic. The host may still be placing these vCPUs across groups, and neither reading the topology
nor pinning inside the guest can see or change it. The hypothesis is untestable here rather than
disproved — but it is also unactionable here, which matters more.

What remains, and fits the numbers without needing any of that: the **working set grows with workers**,
231 MB at one, 238 at two, 294 at four, against 8 MB of last level cache. A miss that used to be
answered by another core's cache becomes a miss answered by memory, which is the same count of misses
taking longer — exactly the shape measured. Whether that is buffers in flight or the collector giving
each thread its own allocation budget is not established; the arena queues are nowhere near their 2,000
item limit at 27% busy, so it is not backlog.

## The firewall is a fifth of the send thread, and it has been corrupting every comparison

**Read this before the two sections below it, both of which it invalidates in part.**

A sampled profile of one send thread, hardware counters, 999 Hz, 30 seconds inside a settled run of
1,600 clients over 16 arenas:

```
66.82%  [kernel.kallsyms]
31.54%  memfd:doublemapper          <- JIT compiled code
 0.80%  libcoreclr.so

12.31%  nft_do_chain                <- nftables walking its rule chain
 3.05%  expr_call_ops_eval
 1.96%  nft_meta_get_eval
 1.32%  nft_immediate_eval
 1.05%  __nf_conntrack_find_get
 0.85%  nf_hook_slow
 0.81%  udp_sendmsg                 <- the actual send
```

**Two thirds of the send thread is kernel, and at least a fifth of the whole thread is the firewall**,
evaluating its chain and doing a connection tracking lookup for every datagram. `udp_sendmsg` itself
is 0.81%. `__pv_queued_spin_lock_slowpath` at 2.14% is contention on conntrack's own locks between the
server's threads.

This is the answer to the arithmetic that could not be made to work: 13.6 µs per connection per pass
for two 22 byte packets, against syscalls that ought to cost 2% of a core. The syscalls do cost about
that. The firewall wrapped around them costs ten times more.

### It also explains why the comparisons would not settle

Same configuration, different runs: `Workers = 2` measured 2.39 cores and then 2.82, and `Workers = 1`
measured 2.78 twice and then 2.63. **The one worker against two result reversed on repetition.** An 18%
spread between runs of one configuration is larger than most of the differences recorded below, so:

- **"One worker makes the send threads dear" is withdrawn.** It was one pair, and the pair did not hold.
- **"Six busy threads is the bottom of a U" is withdrawn.** Every step in it — 1 to 2 workers at 16%,
  2 to 3 send threads at 18% — is at or below the noise it was measured against.
- **Four workers costing 31% is the only step above the spread**, and it is one run. It needs repeating
  before it is believed either.

Connection tracking is stateful and its table carries entries between runs, which is a plausible source
of exactly this kind of run to run variation. Nothing in the harness was wrong; the machine underneath
it was not holding still, and no amount of averaging windows *within* a run would show that.

**Nothing on this machine should be compared again until the firewall is out of the packet path**, and
then every configuration needs repeating rather than measuring once.

## Threads are U shaped, and six is the bottom — WITHDRAWN, see above

Adding the send thread count to the worker sweep, all at 1,600 clients over 16 arenas with the same
instrumentation, and counting every thread that does continuous work — the arena workers, the send
threads, the receive thread and the mainloop:

| run | workers | send threads | busy threads | server processor | send | workers | references | misses | overruns |
|---|---|---|---|---|---|---|---|---|---|
| w1 | 1 | 2 | **5** | 2.78 cores | 182% | 75% | 463.0 M/s | 56.2 M/s | 1,063 |
| **w2** | **2** | **2** | **6** | **2.39 cores** | **148%** | **71%** | **388.3 M/s** | **50.4 M/s** | 902 |
| st3w2 | 2 | 3 | **7** | 2.82 cores | 175% | 82% | 444.2 M/s | 59.4 M/s | **471** |
| w4 | 4 | 2 | **8** | 3.13 cores | 180% | 108% | 469.7 M/s | 61.7 M/s | 1,183 |

Same work in every row — 16,004 to 16,016 packets a second, 289,000 to 291,000 updates delivered.

**Six busy threads is the bottom of a U**, and it does not matter much which side the extra thread is
added to: a seventh costs 18%, an eighth 31%. Server processor time and cache references move together
across every row, which they did in the send thread comparison too.

**"Three send threads are free" is withdrawn.** That was measured at 2,000 clients on a box later shown
to be about 94% busy, where the machine rather than the setting was being measured. With headroom the
third send thread costs 18% — the same as any other seventh thread. It does halve the overruns, 902 to
471, so it buys latency; it does not buy capacity, and it is not free.

**Adding a thread on either side makes both sides dearer.** The third send thread raised worker time
from 71% to 82% as well as its own from 148% to 175%. Four workers raised send thread time from 148%
to 180% as well as their own from 71% to 108%. Producers and consumers share the connection state and
the buffers, and the cost lands on both.

**One worker is the exception and is still unexplained.** It has the fewest busy threads of any row and
is the second dearest, with the whole excess in the send threads, which do none of its work. Not
because they run more often: 167 iterations a second against 166 at two workers. Batching is the
suspect — `SendOutgoing` merges position packets that are already queued when it arrives
(`Network.cs:4188`), so the producer's arrival pattern decides how much merging it does, and bytes per
delivered update do vary with it: 21.7 at one worker, 23.2 at two, 22.4 at four. Suspect, not
established.

The arithmetic that makes it worth chasing: a send thread walks 800 connections 83.5 times a second,
so 66,800 connection visits a second, and at 91% of a core that is **13.6 µs per connection** carrying
about 2.2 queued packets of 22 bytes. Syscalls are not it — at roughly 20 updates packed into a
datagram that is 7,250 sends a second per thread, under 2% of a core — and neither is the bandwidth
limiter, whose `Iter` and `Check` are a few operations over a six integer array. Something per
connection per pass costs far more than what it appears to do, and no amount of comparing runs will
name it.

## The worker's profile, and the first thing it said was that this is a Debug build

With managed frames resolving at last (`DOTNET_EnableWriteXorExecute=0`, so that JIT code is where
the perf map says it is), `profile-thread.sh` against `ArenaLoop-0` at 1,600 clients over 16 arenas,
`--spread 16384`, two workers, 30 s at 999 Hz:

| where | share of the worker |
|---|---|
| JIT code | 88.9% |
| kernel | 7.7%, all of it timer interrupts |
| libcoreclr | 2.3% |

No system calls. A worker buffers packets and the send threads do the sending, so the firewall that
costs a send thread a fifth of itself never touches a worker. That was argued above from first
principles; this is the measurement.

The second thing in it was the tag on every method of the server's own: `[MinOptJitted]`. The runtime
marks each method in the perf map with how it was compiled, and that one means compiled with
optimisation off and never to be tiered up, which is what every method of an assembly built in Debug
gets. The BCL methods around them were `[OptimizedTier1]`. `loadtest.sh` ran `dotnet run` with no
configuration, which is Debug, and every figure in this document was measured that way. The caveat
was written down near the top; what the profile added was its size:

| method | share of the worker | in a Release build |
|---|---|---|
| `LittleEndianConverter.Convert(int16)` | 7.3% | nothing: on x64 it returns its argument, and is inlined away |
| `TryGetExtraData` and its cast helper | 14.4% | inlined at the call site |
| `Hypot` | 7.3% | real work, in registers rather than stack slots |
| packet getters, `Debug.Assert` string handlers | 2.8% | gone |

`CONFIGURATION=Release` on `loadtest.sh` now runs the Release build, built first by the caller; the
run says which build it was, and `profile-thread.sh` sums the `[MinOptJitted]` share of a profile and
warns. In Release the same worker went from 52.5 µs a packet to 30, and the server from 2.61 cores
to 2.27. The send threads did not move, because their cost is in the kernel.

Per-packet figures from here on are a method's share × the worker's utilisation ÷ the packets that
worker handled. Both the thread's total and its share of the packets change with the worker count, so
the raw percentages mislead.

## Why four workers cost more than two: the wait spun and yielded

Two workers against four, same load, one worker profiled each time, Debug and then Release:

| µs per packet on the worker | pw-2 (Debug) | pw-4 | rw-2b (Release) | rw-4 |
|---|---|---|---|---|
| whole worker | 52.5 | 70.0 | 30.0 | 37.5 |
| kernel and runtime | 5.2 | 13.2 | 9.6 | 13.6 |
| send side: `SendOrBufferPacket`, `OutLock`, pool, atomics | 15.4 | 19.9 | 10.6 | 13.0 |
| scan side: `BroadcastPosition`, `Hypot`, `TryGetExtraData` | 21.9 | 23.6 | 8.5 | 9.5 |

The send side grew and the scan side did not, which is what the hand timing above had said. But the
largest mover was the kernel, and the timer interrupt's share of it was constant, so whatever it was
scaled per packet and not per processor-second. Its symbols were the scheduler and system call entry
and exit, with `do_sched_yield` among them. Nothing else: no networking, no memory management.

The obvious reading, that an idler worker blocks and is woken for more of its items, was wrong, and
the kernel's own counts for the thread said so: 284 blocks a second, but 2,765 times a second
switched out while still runnable. `perf stat` on the thread over the same window:

| per second, one worker of two, Release | |
|---|---|
| `sched_yield` | 17,942 |
| `futex` | 539 |
| blocked and woken | 284 |

A worker waited on a `BlockingCollection`, whose wait is a `SemaphoreSlim`. When that finds nothing
queued it spins for up to 140 iterations before it will block, and from the tenth iteration every
other one is `Thread.Yield`: a `sched_yield` system call and a full trip through the scheduler,
whether or not anything else can run, up to 65 of them for one empty queue. 284 × 65 is 18,460.
Nearly every item arrives inside the first few iterations, which are pause instructions and cost
nothing to speak of, but the few hundred gaps a second that outlast the budget cost 65 system calls
each, about 140 µs of processor per idle transition, and that was a fifth of the worker's time. The
budget is fixed while the gaps between one worker's items grow with the number of workers, so a
worker at four is more often in the expensive part of its wait than a worker at two. The cost lands
in the kernel and in the runtime's spin, never in any method of the server's, which is why six
changes to the work each helped one worker and none helped four: the extra was never in the work.

It is not a capacity cost. At saturation there are no gaps and the spin never runs, so the 1.78
measured between one and two saturated workers above stands. It is a cost at partial load that grows
with idleness, and every worker comparison in this document that was not at saturation was inflated
by it, more so the more workers were running. It is also why isolating the workers on their own
cores made it worse: a yield with nothing else to run returns at once, and the budget is spent
faster.

### The fix

`scale/24-worker-wait`: a `ConcurrentQueue` and a `ManualResetEventSlim` with a spin count of ten,
under `SpinWait`'s yield threshold, so the wait spins for a couple of microseconds on pause
instructions, for the item that is about to arrive, and then blocks. The event is cleared before the
queue is re-read, so a wake cannot be lost between the two.

| per second on one worker, Release | rw-2c, before | fw-2, after | fw-4, after |
|---|---|---|---|
| `sched_yield` | 17,942 | 248 | 106 |
| blocked and woken | 284 | 583 | 579 |
| kernel share of the worker | 19.6% | 10.1% | 12.6% |

| | rw-2b | rw-4 | fw-2 | fw-4 |
|---|---|---|---|---|
| µs per packet on the worker | 30.0 | 37.5 | 22.5 | 25.0 |
| the four worker penalty | | +25% | | +11% |
| all workers together | 48% | 60% | 36% | 40% |
| whole server, cores | 2.32 | 2.42 | 2.11 | 2.10 |

Three Release runs of two workers before the fix put the profiled worker at 24, 24 and 20 percent;
after it, 18. The server costs the same at four workers as at two. What is left on the worker is a
microsecond on the send side, where the pool dequeue and `SendOrBufferPacket` grew, and a microsecond
of real block-and-wake, since a worker at four blocks twice as often per packet. The scan side is flat
to within a percent.

### The cast that was a call

The largest single item left on the worker in Release is `CastHelpers.IsInstanceOfClass` at 12.65%,
about 2.9 µs a packet. It is the `is T` inside `TryGetExtraData`, once per candidate scanned, and it
is a call because `Game.PlayerData` was not sealed: for a class nothing can derive from, the same
check is an inline comparison of the method table pointer. `scale/25-sealed-extra-data` seals every
module's private per-player and per-arena data class.

**Measured: nothing.** The helper is gone from the profile and `BroadcastPosition` grew from 23.6% to
36.8% of the worker, which is the same time under a different name; the worker sat at 19 to 20
percent against 18 before, within the spread of repeated runs. So the stall was never the call. It is
the memory the check touches, another player's extra-data slot and then that object's header, once
per candidate scanned, and the helper was only the first instruction to use what was loaded, so the
profile charged the miss to it. Inline, the miss is charged to `BroadcastPosition` instead. The
sealing stays, since it is correct and costs nothing, but it is a tidy-up and not a gain, and a
sample profile attributes a miss to whichever instruction consumes the load, which is not always the
one that matters.

What that leaves on the scan side is about 87 ns per candidate, for a hundred candidates a packet,
and the line being fetched is player state the mainloop writes for every packet it handles. That is
the sharing relationship "What none of them touched" describes, and `Net:HandlePacketsOnArenaThread`
is the setting that removes it, measured at 13% of the server at one worker on a Debug build before
any of the above.

**Measured in Release with the yield fix in, `hw-2`: a loss of about three percent.**

| | sw-2, off | hw-2, on |
|---|---|---|
| whole server, cores | 2.17 | 2.23 |
| mainloop | 9%, 2,891 iterations a second | below the listing, 159 a second |
| each of two workers | 19 to 20% | 26% |
| worker, µs per packet | 23.75 | 32.5 |

The mainloop left the packet path, and the workers took on more than it gave up. Of the extra 8.75 µs
a packet on the worker, about 2 is the handler itself: `HandlePositionPacket`, the work item, the
advisor lookup, the player lock. The rest is the scan and the sends getting dearer on the same thread:
`BroadcastPosition` 8.7 to 11.0 µs, the pool dequeue 1.5 to 2.5, the connection lock 1.6 to 2.3. So
the scan's cost is not the position line the mainloop writes, and moving the write did not buy it
back. The thirteen percent at one worker above was measured in Debug with the yield spin present,
which confounds it. Left off.

What the scan's 87 ns per candidate is remains open. `PERF_EVENT=cache-misses` on
`profile-thread.sh` is the instrument for it, attributing samples to the instructions that missed
rather than to whichever one consumed the load. But the worker is a fifth of a core at this load and
each send thread is four fifths, so it is not where the next player fits, and the worker question is
closed here: the code reason four cost more than two was the wait, it is fixed, and what remains is
the send path's shared lines at about half a microsecond per recipient.

## Everything the arena thread can be handed, and the defect it found

`Net:HandlePacketsOnArenaThread` had been vetted for positions, weapons and deaths only. The load
client can now produce the rest: `--ship-changes`, `--freq-changes`, `--arena-switches`, `--balls`
and `--flags`, each a rate per client per minute like `--deaths`. Ship and freq changes, flag touches
and drops, and ball pickups and shots are all handled on the arena's thread when the setting is on;
the arena switch is a second arena request while playing, which is what `?go` sends, and it goes to
the mainloop by design, which is exactly the race that matters: packets in flight to the old arena's
worker while the mainloop moves the player. Two details shape the simulation. A ball pickup has to
quote the ball's fire time from the server's last ball update, or it is refused as stale, so a client
only tries for a ball the server last reported loose. And the ship change enforcer allows no ship
for five seconds after a change, so a client whose arena entry lands inside that window arrives in
spectator mode; the client asks for a ship once a second until it gets one.

The first run, 200 clients over 4 arenas on a Debug build with everything on, lasted ten seconds:

    FlagGameResetDelegate was fired from an arena thread and reaches handlers registered zone-wide

A flag drop handled on the arena thread ended the flag game, and the reset fired
`FlagGameResetCallback` there. The Game module respawning the winning team and the stats module
scoring the game are both registered zone-wide and both written for the mainloop. The threading
assertion caught it and the server went down with it, which is what the assertion is for. Fixed on
`scale/26-flag-reset-on-arena-thread` the way the map region warp and the enter-game action were
fixed before: a reset that begins on an arena thread hands the firing to the mainloop, and from the
mainloop it fires in place as it always has.

After the fix, 90 seconds with everything on:

| | |
|---|---|
| assertion failures | 0 |
| errors | 0 |
| warnings | 2, both a ball two clients raced for |
| clients survived | 200 of 200 |
| kill notifications | 981/s |
| ship and freq change notifications | 190/s |
| flag pickups | 166/s |
| ball updates | 137/s |
| arena switches completed | 2.1/s |

The client also keeps the chat it receives, since the server explains a refusal to the player in
chat and nowhere else, and prints the most frequent. That showed the freq balancer refusing every
move between two equal teams, and private freqs unavailable in the test arenas: neither a defect,
both worth knowing before reading a warning count. Warnings from the client's own misbehaviour,
touching flags from spec and asking for ships between arenas, went from 189 to 2 once it waited to
be told what it was in.

Not measured for cost: a run that switches arenas and changes ships has clients out of play for
part of it, which the broadcast-versus-offered check reports, so it is a correctness run and not a
throughput one. The 3% cost of the setting stands from `hw-2`.

**On the VPS, 1,600 clients over 16 arenas, 150 seconds, Debug, everything on: 0 assertion failures,
1,599 of 1,600 survived.** 27 warnings: 19 for a ball two clients raced for, 6 for a ship or freq
change sent before the server's notice of the previous one had been acked, 2 for a request landing
mid switch. The one error is the security module wanting `clients/Continuum.exe` for its checksum,
which that box does not have. Not a throughput run in any sense: with no `--spread` the fan-out was
88 recipients a packet, 520,000 sends a second, and two workers sat at 93% each on a Debug build,
so only 36% of the offered positions were broadcast and the mainloop, at 1%, had nothing to
backfill with. The one lost client is most likely a reliable packet that went unacknowledged for
fifteen seconds on a saturated server, which is the client's own give-up rule.

## Where this box runs out with the generator on it, in Release, and what the wall is made of

The first limit series on a Release build. Same shape as every throughput run here: a hundred
players per arena, `--spread 16384` for 18 to 20 in view, positions only, connects staggered 3 ms
apart, 150 second holds, `validate.sh` taking the server's cores, the rest of the machine and the
idle over the same 30 second window. `SendThreads = 2` with `MaxSendThreads = 3`, and the server grew
the third thread on its own at 2,000. Two workers. First with zone-wide player ids, which every run on
this box had used without anyone noticing, then the top two points again with arena-local ids.

| clients | ids | server cores | send threads | workers | rest of box | idle | out KB/s | datagrams/s | delivered | lag specs | overflows |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 2,000 | zone | 3.19 | 83, 81, 68% | 25% | 3.59 | 1.22 | 6,844 | | 99.5% | 0 | |
| 2,500 | zone | 3.56 | 84, 83, 82% | 33% | 3.35 | 1.09 | 10,401 | | 99.2% | 0 | |
| 3,000 | zone | 3.98 | 92, 91, 90% | 36% | 3.26 | 0.76 | 11,063 | | 99.7% | 0 | |
| 3,500 | zone | 4.29 | 96, 96, 96% | 47% | 3.11 | 0.60 | 13,549 | | 99.75% | 0 | |
| 4,000 | zone | 4.53 | 93, 92, 92% | 65% | 2.88 | 0.59 | 16,023 | | 99.8% | 0 | |
| 4,000 | zone, repeat | 4.26 | 96, 96, 95% | 57% | 3.40 | 0.34 | 16,010 | 186,987 | 99.8% | 0 | 158 |
| 5,000 | zone | 4.70 | 93, 93, 92% | 69% | 2.91 | 0.39 | 19,520 | 200,941 | 99.5% | **239** | **810** |
| 4,000 | arena | 4.25 | 96, 96, 95% | 51% | 3.26 | 0.49 | **9,136** | 205,951 | 99.8% | 0 | 0 |
| 5,000 | arena | 4.62 | 94, 93, 93% | 67% | 2.95 | 0.43 | **10,947** | 202,057 | 99.7% | 0 | 0 |

"Delivered" is what the clients received against what the server handed its send threads.
"Overflows" is the kernel's count of datagrams discarded for want of socket buffer, machine wide, in
the 30 second window; the datagram and overflow columns were added to `validate.sh` partway through.
The two 4,000 runs with zone-wide ids are the run-to-run spread at this load: 0.27 cores on the
server, 0.25 on idle. Nothing smaller than that up here is a difference.

**With the generator on it, this box holds 5,000.** Every point was clean on the server's side:
broadcast at 100% of offered, delivery above 99%, all clients surviving. The one blemish, 239 players
specced for lag at 5,000 with zone-wide ids, was the generator's: 810 datagrams discarded from its
sockets in the window, with the server's own drop counter still, and the same 5,000 with arena-local
ids had no specs and no overflows while the server worked just as hard. The box itself was out of
processors from 3,000 on, under a core idle, and the script said so every time.

**The wall is the send threads, and it is made of datagrams.** Arena-local ids cut the bytes sent
by 43% and left the three send threads exactly where they were, at 96%. The datagram count did not
fall, it rose a little, because what sets it is how many connections have something to send each
pass, and at ten position packets a second with 18 in view that is most of them every pass. Three
threads move about 200,000 datagrams a second, about 14 µs of send thread each, a fifth of it the
firewall. From 4,000 to 5,000 the datagram rate stayed flat at that figure while delivery held: the
threads were past their 10 ms interval, each pass took longer, each connection was visited less often,
and more positions went into each datagram. That is graceful, and it is also latency, which nothing
here measures. Overruns began climbing at 2,500, so the send side was holding its interval to about
that many players and stretching it above.

**What arena-local ids are worth, restated.** Bandwidth: 43% at 4,000 and 44% at 5,000, which is
real on a hosted box and on the players' own links. The workers, which build the smaller packet, went
from 57 to 51%. The generator's side, which parses it, got cheaper and stopped overflowing. The send
threads: nothing, because their cost is per datagram. The record above that credited local ids with
more updates delivered per second was measured with the generator on the same box, where the
generator's parsing was what got cheaper.

**The server's own line**, three send threads: 1.7 cores plus 0.73 per thousand players from 2,000
to 3,500, then flattening as the send threads stop growing and start stretching. Beyond that the
server's cores say less, because it was sharing under half a core of idle with the generator.

**What it would carry alone on eight of these vCPUs** is an extrapolation and marked as one. With
the generator gone, six or seven send threads have the cores to run, and each moves about 70,000
datagrams a second before its pass stretches, so roughly 430,000 a second between them, against
the 200,000 that three threads hit here. Everything else, the workers at four, the receive thread
and the mainloop, adds up to under two cores at 10,000. That puts nominal-latency positions-only
capacity near 8,000 and graceful capacity above 10,000, with the firewall's fifth of every datagram
the largest single cost that is not the server's own. Combat roughly doubles the send side.

## With the generator on another machine: clean to 3,000, and the interface gives out at 4,000

The load client on the development box, the server alone on the VPS, over the internet through a
home router and WSL2's own NAT. Release, a hundred per arena, `--spread 16384`, connects 3 ms
apart, 150 second holds, arena-local ids on, `MaxSendThreads = 6`. Run by `remote-loadtest.sh`,
with `server-side.sh` taking the VPS's whole-machine window from `/proc` and the same window taken
here for the load client's process.

Three things had to be fixed in the load client before the first clean run, none of them the
server's. It stamped positions with its own machine's clock, which the server compares against its
own from arena entry, so nothing was ever newer and nothing was broadcast; on the same machine the
two clocks had been the same counter. It reported only its positions in the time sync request, so
the server derived client-to-server loss that was not there and specced 621 of 1,000 for it. And
the sync itself did not exist, for the same same-machine reason. The server needed one fix: its
send socket had the kernel's default buffer, and on a real interface a datagram is charged to the
buffer until the interface takes it, so the connect ramp of 2,000 refused 2,517 datagrams with a
stack trace each. `Net:SendBufferSize` now, with refusals counted in `?netstats` rather than
logged one by one.

| clients | server cores | send threads | workers | idle | datagrams/s out | delivered | rel ping | loss s2c / c2s | specced |
|---|---|---|---|---|---|---|---|---|---|
| 200 | 0.35 | 2 at 10% | 4% | 7.38 | 11k | 100% | | | 0 |
| 1,000 | 1.53 | 3 at 35% | 17% | 5.73 | 74k | 99.8% | | 0 / 0 | 0 |
| 2,000 | 2.86 | 4 at 46% | 31% | 4.87 | 151k | 100% | | 0 / 0 | 0 |
| 3,000 | 3.59 | 5 at 48% | 45% | 4.17 | 214k | 100% | 27 ms | 0.3 / 1.2 | 0 |
| 4,000 | 4.98 | 6 at 49% | 46% | 2.19 | 271k | 71% | 41 ms | 9.0 / 0.5 | 3,772 |

"rel ping" and the loss figures are the server's own view of one client, asked with `?lag -v`
twenty seconds into the hold; the load client now does that itself, and it is the only way to tell
a path that loses packets from a generator that reports its counts wrongly.

**At 4,000 the VPS's network interface stopped taking packets.** The send socket refused 12,600
datagrams a second with its 4 MB buffer full, `fq_codel` on the interface had dropped 400,000 for
latency with its packet limit untouched, and the driver was refusing with its transmit ring full.
About 270,000 datagrams a second is what that virtual interface takes, and that is the host's
limit, not the guest's: the send threads were at half load, two cores were idle, the workers at
46%. Everything else in that row follows from it, the 9% loss, the reliable resends, the clients
receiving 106 updates a second instead of 190, and the server speccing 3,772 of them for the loss.

The generator's side was not the limit: 2.6 cores of 32, no overflows, and the reliable round trip
at 27 to 41 ms. Its position stamps did fall a second behind at 4,000, which is its sends stalling
in its own virtual interface; the total through WSL2's path sat at about 250,000 datagrams a second
in both the 3,000 and 4,000 runs, so it may have a ceiling of its own near the VPS's.

**What the server alone holds on this VPS is about 3,500, at ten millisecond send passes, and the
interface's packet rate is what says so.** With the box to itself the server is 0.35 cores at 200
and about 1.2 per thousand after, growing send threads as it goes, and nothing of the server's is
near a wall at 4,000. Against the same-box series, which reached 5,000 through loopback, the
difference is entirely that loopback has no packet rate.

`Net:SendInterval` is the lever against a packet rate limit, added on `scale/28`. Each pass sends
every connection what queued since the last, merged into as few datagrams as fit, so a twenty
millisecond pass sends about half the datagrams for the same positions, each carrying twice as
much, at up to ten milliseconds more delay before a position goes out.

**Measured at twenty milliseconds:**

| | 4,000 at 10 ms | 4,000 at 20 ms | 5,000 at 20 ms |
|---|---|---|---|
| datagrams out per second | 271k | 190k | 225k |
| send socket refusals | 12,600/s | 0 | 0 |
| server cores | 4.98 | 3.91 | 4.65 |
| send threads | 6 at 49% | 5 at 46% | 6 at 43% |
| workers, each | 46% | 60% | 69% |
| idle on the VPS | 2.19 | 3.77 | 3.04 |
| delivered of sent | 71% | 100% | 78% |
| loss s2c / c2s, server's view of one client | 9.0 / 0.5 | 1.6 / 1.5 | no reply |
| specced | 3,772 | 316 | 4,465 |
| broadcast of offered | 80%, spread 87% | 95%, spread 2% | 83%, spread 78% |

At 4,000 the interval moved the wall as predicted: 30% fewer datagrams, no refusals, everything
delivered, and a core less on the server for the same positions. The workers rose from 46% to 60%,
because a pass now holds each connection's lock for twice the packets and the workers wait on it
more; at two workers that is the next thing on the server's side to run out, near 5,500, and four
is the answer when it does.

At 5,000 the VPS was still under its limits, no refusals at 225,000 datagrams out, six send threads
at 43%, three cores idle, and the run collapsed anyway: 4,465 specced, 2,222 of them for
server-to-client loss, 1,353 for ping and 890 for spikes. Those packets left the VPS and were lost
on the way into this machine, whose own sockets overflowed nothing, so the loss is in WSL2's NAT
path before the sockets. That path carried 225,000 to 250,000 datagrams a second in every run
from 3,000 up whatever the population, which is its ceiling. The 316 specs at 4,000 were the same
thing beginning: the clients' position stamps arriving a second old with the reliable round trip
at 29 ms, since their sends were queuing in that path.

**So the remote series ends where the generator's path ends, not where the server does.** With
the box to itself, at twenty millisecond passes, the server on this VPS is clean at 4,000 and
under every one of its own limits at 5,000; where it runs out is not known, because nothing that
can be aimed at it from here gets that far. What it needs to find out is a generator that is not
behind WSL2's NAT: the same load client on Windows itself, WSL2 in mirrored networking mode, or a
second Linux machine. The next server-side setting on that run is `Workers = 4`.

The first of those turned out to be a one-line change to the harness. The load client's dll is
the same IL on every platform, Windows has the .NET 10 runtime, and WSL's interop runs Windows
executables from a Linux shell, so `CLIENT_DOTNET=dotnet.exe` makes `remote-loadtest.sh` hand the
Release-built `SS.LoadClient.dll` to Windows' own `dotnet.exe` instead of this one. The client's
sockets are then on Windows' stack, on the physical interface, with nothing of WSL2's between them
and the VPS. Two hundred clients aimed that way at a server inside WSL2 gave two hundred survivors,
no loss and a round trip of a millisecond, which proves the mode and nothing about the ceiling: a
Windows process is invisible to `/proc` here, so the harness skips the generator's machine window
in that mode, and the generator's own limit on this path is now a thing to be found rather than a
number already known.

### Five thousand from Windows: the loss is not the generator's, and a race at shutdown

The 5,000 run repeated that way (`win-5000`: 50 arenas, 150 s, `Workers = 4`, `SendInterval = 20`,
Release, six send threads) came back the same shape as the WSL2 one. All 5,000 connected, in 90 s
rather than 15 because each connect is a handshake of round trips to the VPS and they are
sequential; 4,999 survived. The server broadcast the whole offered load, 49,400 positions a second
at 19 in view, for three windows once everyone was in, with its six send threads at 44% of their
interval and the four workers at 36-40%. Then 1,752 clients were specced for `s2c ploss` inside
twenty seconds, 13 more for spikes, and the broadcast rate stepped down to 35,000 and stayed there,
which is what 35% of the players being spectators looks like. The `?lag` probe on the first client
at twenty seconds showed the loss as it began: 3.35% server to client, 1.83% on the reliable
stream, both other directions at zero, and the reliable round trip at 40 ms.

Two things came out of the numbers that were not the run's point.

**The server side's window was a ramp window.** `server-side.sh` took its whole-machine window after
four metrics windows with positions in them, forty seconds after the first position arrived, and
with a ninety second connect that is halfway up the ramp: the 3.88 cores and 148,887 datagrams a
second it reported are for about 3,000 players, not 5,000. The client side now tells the server
side when the hold has begun, over the same SSH connection, and both windows start twenty seconds
after that, so they cover the same stretch. The results line, which comes from the metrics windows
rather than the machine window, was not affected.

**The loss was not in the generator.** That was the whole question, and Windows answers it with
counters this machine did not have from inside WSL2: its UDP layer had recorded no receive errors
and the physical adapter no discards, cumulatively, since the machine was booted, so nothing that
reached this machine was dropped by it. The packets went missing between the VPS's sockets and this
machine's adapter. That puts the WSL2 run's loss in doubt too: at 5,000 the WSL2 generator specced
2,222 and the Windows one 1,752, from the same server sending at the same rate, and the one thing
both runs share is the VPS's own egress at about 225,000 datagrams a second, the rate the earlier
run at ten millisecond passes was refused at from 270,000. `machine-window.sh` now reads the
interface's drop counters, the root queue discipline's drops and requeues, and the backlog queue's
drops over the window, which is where a burst the virtual NIC cannot take at line rate goes
missing, and the Windows side reads the same for its adapter; the next run says which.

The clients sent 9.6 positions a second against a nominal ten. That is Windows' system timer, whose
default resolution is 15.6 ms, under a 100 ms `PeriodicTimer`. The load client now asks for a 1 ms
resolution on Windows at start, the standard `timeBeginPeriod` request.

**And a race at shutdown, which was the run's one real defect.** Eight warnings in the server log,
all the same: a `NullReferenceException` on an arena worker, in `SendOrBufferPacket`, under the
ball timer's `SendToArena`, while the server was unloading. The forced disconnect of every player
that `Network` does at the pre-unload stage frees each connection's bandwidth limiter, and did so
under the player *read* lock; a forced tear down does not change the player's status, and the
status is what a sender checks, under the same read lock, before it uses the connection. The arena
workers were still running the ball timer, since the modules that own timers unload later, and a
worker that reached a torn down connection through a player still marked as playing dereferenced
the freed limiter, because the ball packet's priority flag includes urgent and urgent goes through
the limiter first. Each exception also dropped that arena's ball timer for good, which is what a
worker does with a timer that throws. On the mainloop the same tear down could not race anything,
since the mainloop was the only sender of timer packets; the workers are what made the sender and
the tear down different threads.

It did not reproduce locally until its mechanism was understood: the ball is sent every five
seconds per arena and every arena was created at the same moment, so fifty arenas send in lockstep,
and a forced loop shorter than five seconds either meets the burst or misses it. A loop slowed to
three milliseconds a player, six seconds for two thousand, met it every time: fifty exceptions, one
per arena, the same stack as the VPS. The fix, on `scale/29-forced-teardown`, takes the write lock
for the forced loop, so no sender is mid-send while it runs, and detaches each player from its
connection as it is torn down, so a sender that arrives afterwards finds no player and sends
nothing; the urgent path also stops trusting that the limiter is there. The same slowed loop with
the fix in place: the workers waited on the write lock, then found nothing to send to, and no
exception. This is the third defect the worker path has surfaced, after the flag reset callback
and the send thread's pass-long lock hold, and the first at shutdown.

### The second run from Windows measured the wrong thirty seconds

The rerun with the fix and the new windows (`win-5000` again, same settings) held the whole
offered load for three windows once everyone was in, 49,774 positions a second, then specced 2,941
players inside one ten second window and stepped down to 19,700. The `?lag` probe at twenty
seconds had loss in *both* directions this time, 1.84% server to client and 2.43% client to
server, with the reliable stream at 3.74% and one client-to-server stall of a full second. Loss
towards the server is the new fact: the VPS takes in only 50,000 datagrams a second, a rate its
interface refused nothing at, so whatever is dropping client-to-server packets is on the path, and
the same path carries the server-to-client ones. A home router forwarding 275,000 packets a second
through its NAT is the obvious suspect; the interface counters at both ends over a window inside
the hold are what would say so, and this run did not take them inside the hold.

Both windows were taken after the run. The client's output reaches its log through `tr`, which
buffers by the block when writing to a file, and the "Holding" line the harness waits for is a few
hundred bytes in, so it arrived only when the client exited; the harness then signalled the server
side, waited its twenty seconds, and both machines measured a server sending 30,000 datagrams a
second of reliable retries to five thousand clients that had gone, which Windows counted, all of
them, as datagrams for ports nothing was listening on. That is what "2 datagrams a second in" at the
VPS and "82 in" at Windows were. The fix is `stdbuf`. The Windows window also failed to find the
client's process, a quoting slip in the PowerShell filter, now fixed and checked against a running
client.

The after-run window did show two things worth having. Only 1,705 of the 5,000 goodbyes reached
the server: the client sent all five thousand in the same millisecond, and a burst like that is what
a router's queue drops. The server then carried 3,295 players for its thirty second drop timeout
and kicked them all in one go, and because the kick is applied on the mainloop and the send thread
checks a flag the mainloop sets, every pass until the mainloop got there asked again: a hundred and
eighty four "Kicked for no data" lines per player, three hundred thousand in ten seconds. The client
now paces its goodbyes, a millisecond every sixteen, and the connection remembers that a kick is
pending (`scale/30-kick-once`), so a mass drop logs each player once. The hold also now begins
when the connects end rather than when the process starts, since a "150 s" hold that includes a
forty second connect is a 110 s hold.

### Third run from Windows: the windows landed, the path was clean, and the wave was the generator's

With the windows inside the hold, the path answered. Over the same thirty seconds the VPS's
interface sent every datagram its sockets did, its queue discipline dropped nothing (363 requeues,
the driver's ring momentarily full, which delays but does not lose), and this machine's adapter
received at least as many packets a second as the VPS sent, within the few seconds by which the two
windows are offset. Neither end dropped a packet on receive. What the VPS did show was 4,683
receive buffer overflows in the window, 156 a second against 59,000 in: its socket's receive buffer
is `Net:ReceiveBufferSize`, one megabyte by default, a fifth of a second at that rate, and the
receive thread was held off that long while the mainloop took the write lock for a wave of ship
changes (24 writes a second, 173 ms waited, in the window). Four megabytes is the setting for the
next run; it is what the send side already asks for.

The run itself: 4,990 of 5,000 connected, in 52 s; 4,987 survived; the clients sent 10.0 a second
each, the timer request having worked; and the last seventy seconds of the hold were clean at the
full load, 49,900 positions a second at 18.9 in view, 942,000 sends a second, six send threads at
45-49%, four workers at 43-45%, four vCPUs idle, no loss and no specs. **The server on this VPS
carries 5,000 at twenty millisecond passes with room left.** What preceded that was a wave: 2,179
players specced for server-to-client loss in the first ten seconds of the hold and 515 more twenty
seconds later, after which they asked for ships again and came back.

The wave was not loss. The specced players were the oldest three thousand, the first to connect,
and not the youngest, whose connections were the ones under the most recent load; and the server
had logged 916 spikes for those same players thirty seconds earlier, during the connect ramp, a
spike being a client that stopped sending for longer than the arena's limit. So the generator
stalled for some seconds twenty to thirty seconds into its connects, with two thousand clients in.
A stalled generator stops reading as well as sending; its sockets fill on this machine, which
Windows does not count as a drop, and when the tick loops resume, each client's next time sync
reports what it had read so far, short of what the server had sent by the length of the stall. For
a client thirty seconds old a five second backlog is a sixth of everything it was ever sent, the
server reads it as loss, and `LagAction` specs at fifteen percent. The younger clients connected
after the stall and never carried the shortfall. It is the same signature as the WSL2 run's wave,
which fell on the youngest clients instead, consistent with a stall at a different moment.

The load client now watches itself for exactly this, once a second from its first connect: the
worst lateness of any tick loop, the garbage collector's pause time, and how long after the server
stamped them positions arrive, which is the path's delay plus whatever time the datagram sat on
this machine before the receive loop reached it. A second with a stall is printed as it happens,
with the time since the run began, and the summary carries the totals. The cause of the stall,
whether a collection, the thread pool, or the connects themselves, is what the next run's lines
say; until it is known, the population at which this generator is trusted is the one where those
lines stay empty.

### Fourth run: the stall was not a stall, and the loss is measured by the second now

The watcher's lines came back and said the opposite of what the third run's reading predicted.
Positions arrived 90 to 200 milliseconds after the server stamped them, every second of the hold,
wave included: no receive backlog on this machine, at any point. The collector paused 331 ms in
the whole run. And the "late ticks" were 2,966, 5,990, 9,008, 12,027 and 14,990 ms: multiples of
three seconds, which is the client's reliable send timeout. A client the server had specced asked
for a ship every second, the request is reliable, the tick loop awaited its acknowledgement, and
when the acknowledgement was lost the loop stood still for three seconds an attempt. Those were
single clients waiting on a lost packet, not the generator stalling; the summary's maximum across
five thousand loops made them look like one. The ship request no longer blocks the loop and is
made every five seconds at most.

So the third run's explanation of the wave, a stall and a backlog, was wrong, and the wave is
back to being loss the server measured: 2,414 specced in one window, this time the *youngest*
clients, and the probe on the oldest client at twenty seconds into the hold read 11.1% server to
client, 4.7% on the reliable stream, none the other way. A burst of loss shared by everyone shows
first on the clients with the fewest packets to dilute it, which is what young clients are, and
that is what this run's distribution says; the third run's fell on the oldest, which no single
story yet covers. The machine windows again showed the VPS's interface and queue discipline
dropping nothing over their thirty seconds and this machine's adapter receiving what was sent
within the offset between the two windows, and that offset, a few seconds between two thirty
second windows on a rate that was changing, is exactly what stops them resolving a few percent.

The instrument that resolves it is in the harness now. The server side samples its machine every
second for the life of the server, stamped by the wall clock: datagrams out and in, the queue
discipline's drops and requeues, the interface's drops, the socket overflows. The client prints
the same seconds from its end, the datagrams all its clients received and sent, from the same NTP
clock. The report matches the two by the second over the hold and prints the total shortfall, the
seconds where the clients received eight percent or more less than the server machine sent, and
on each such second what the server machine's own counters dropped. Loss on the path shows as a
shortfall with clean server-side counters; loss at the VPS's egress shows as a shortfall with
queue discipline drops on the same second; a counting fault in the client shows as no shortfall at
all beside a server that still specs. Two clocks and two samplers put up to a second of skew
between the series, so the totals over the hold are the figure to trust and single seconds are
read with that in mind. The next 5,000 run is the first with it.

### Fifth run: the wall is the generator's internet connection

The seconds settled it in one run. Over 161 seconds with everyone in, the VPS sent 209,311
datagrams a second and the clients received 180,536: 13.7% short. In the seconds when the VPS sent
235,000, the clients received 185,000, 22% short, and the VPS's own counters on those seconds were
clean: its interface transmitted every datagram its sockets sent, the queue discipline dropped
1,870 over the whole hold against 34 million sent, requeues a few dozen a second. Delivered against
offered, in steps of the offered rate:

| VPS sent, datagrams/s | clients received | seconds |
|---|---|---|
| 140,000-160,000 | 152,700 | 27 |
| 160,000-180,000 | 164,750 | 28 |
| 180,000-200,000 | 177,000 | 4 |
| 220,000-240,000 | 188,300 | 106 |
| 240,000-260,000 | 185,000 | 3 |

Delivery follows the offered rate to about 160,000 and stops at 185,000 to 190,000 whatever is
offered above it. That is a ceiling on the path between the VPS's interface and this machine, and
its size says what kind: the datagrams average 42.6 bytes of payload, which with the 28 bytes of IP
and UDP headers is 70 bytes each, and 188,000 of them a second is 13 MB/s, 105 Mbit/s at the IP
layer, about 130 at the Ethernet frame. That is the shape of a home internet connection's
downstream rate, or of the shaping in front of it: a hard cap with no standing queue, since the
reliable round trip stayed at 19-25 ms throughout. The next run's byte counters at both ends will
put the exact figure on it, but the conclusion does not wait for them.

So every spec wave in the remote series was this. Above about 4,000 players at nineteen in view,
the server sends more bytes to this machine than its connection delivers, the difference is lost,
the server's `LagAction` specs the players who report the loss, the broadcast falls until the
offered rate fits the link, and the run settles there: the clean tail of the third run was the
population that fits, not the population that was asked for. The 4,000 run at twenty millisecond
passes was clean because 190,000 datagrams a second fit; the 5,000 runs were not because 235,000
do not. **The server on this VPS was never the limit in any of them**: at 5,000 it had four vCPUs
idle, its send threads under half their interval, and its interface passing everything.

The one number in the fifth run that was not what it looked like was the position age: 6.6
seconds, constant, every second of the hold, beside a reliable round trip of 25 ms. A delay that
large would have shown in the round trip; it did not, so it was a clock. A client whose first time
sync was lost, and at 5,000 connects some were, stamped its positions with its own clock, the
server turned the difference into that client's latency and subtracted it from the stamp on every
position it forwarded, and everyone in view of that client read the result as six seconds of age.
The client now repeats the sync every second until the first reply and ignores a reply to a
request more than five seconds old, and the age is read for what it is: the path's delay plus this
machine's queueing, for positions from clients whose clocks are known.

What is left to measure needs a generator on a connection that can take the server's output.
Five thousand players at nineteen in view is 13 MB/s of these datagrams and ten thousand is twice
that: a second VPS at the same provider, on the same network, running this harness against the
first, is the straightforward way, and the harness already runs from any Linux machine with SSH to
the server. A longer `SendInterval` would fit more players through this link by carrying more
positions per datagram, at the cost of latency, and the link would still be the thing measured.

## What to watch next

- **A sampled profile of one send thread — DONE.** `profile-thread.sh` did it and found the firewall,
  see "The firewall is a fifth of the send thread" above; the worker profiles that followed found the
  wait that yielded.
- **The send thread count, re-measured at a load with headroom.** Still open in that form. The
  Release limit series ran three threads to 5,000 and the box never had headroom past 3,000. It is the
  setting that decides how many players fit, and what a thread carries is now known: about 70,000
  datagrams a second before its pass stretches.
- **Why one worker makes the send threads dear — WITHDRAWN.** Run-to-run variance; the pair reversed
  on repetition.
- **Whether the working set explains the four worker stalls — SUPERSEDED.** The stalls were the wait
  yielding, see "Why four workers cost more than two" above.
- **Whole-machine utilisation during every run — DONE.** `validate.sh` records the server's cores, the
  rest of the machine, idle and the kernel's UDP overflows over the perf window, and warns under a
  core idle.
- **Moving the load generator off the box — DONE, see "With the generator on another machine"
  above.** Clean to 3,000; the VPS's interface stops taking packets at about 270,000 a second, which
  the server reaches near 3,500 at ten millisecond passes with its own threads at half load.
- **`Net:SendInterval = 20` — MEASURED.** It moved the interface's wall: 4,000 clean where 10 ms
  had refused 12,600 datagrams a second, and 5,000 with the VPS under all of its own limits. What
  stopped the 5,000 run was the generator's path through WSL2's NAT at about 240,000 datagrams a
  second. Not yet measured: the latency the interval adds as a player would feel it, which the
  harness does not see.
- **A generator that is not behind WSL2's NAT — RUN at 5,000, five times, and SETTLED.** The loss
  in every remote run above 4,000 is the generator's internet connection: delivery to this machine
  stops at about 185,000 of these 70-byte datagrams a second, 105 Mbit/s at the IP layer, whatever
  the VPS sends above it, with the VPS's interface and queue discipline clean and no standing
  queue. The server on this VPS was not the limit in any of them. What is left needs a generator
  on a connection that takes the server's output: a second VPS at the same provider running this
  harness.
- **Position latency, which the send side degrades by and nothing here sees.** Past its interval a
  send thread stretches its pass and batches more per datagram, so delivery stays above 99% while
  every position arrives later. The load client could stamp each position it sends and read the age
  of each one it receives, which would turn "the send threads are at 96%" into milliseconds.
- **Fewer kernel crossings per datagram on the send thread.** The wall is 14 µs of send thread per
  datagram with a fifth of it the firewall, and one system call per datagram. `sendmmsg` sends many
  in one crossing, which would not touch the firewall's share but would take the syscall's, and it is
  the only lever left on the send side that is the server's own. Two cautions before building it.
  The 14 µs was measured with the generator on the same box, where every datagram sent over loopback
  runs the receiver's kernel path in the sender's context, so a good part of it is the generator's
  receive work and disappears with real clients on a real interface; and the syscall boundary itself
  is a small share of what is left, so the expected gain is of the order of a tenth of the send
  thread, not a half. Worth measuring the send thread's profile with the generator elsewhere first.
- **One arena worker against four, with the counters attached — DONE.** The profiles `pw`, `rw` and
  `fw` at two and four workers are that run, with symbols, and they found the wait.
- **`Net:MaxSendThreads = 0` picks `ProcessorCount / 4` clamped to 1-8.** On the 8 vCPU VPS that is 2,
  and 3 measures better — same processor time, 24 points more headroom on the busiest thread. The
  formula has never been measured against anything on any machine.
- **The 34 errors logged in every VPS run — read at last, and harmless.** Every one is an
  `AttachModule failed: Unable to find module` at startup, for the matchmaking modules and the bot
  module, which the arena configs on that box ask for and which are not loaded there.
  Once per arena, nothing in the packet path, and nothing in any measurement above.
- **The four worker floor — FOUND, see "Why four workers cost more than two" above.** It was the
  wait between items, not the work: six interventions on the work helped one worker and none helped
  four because the extra was 65 `sched_yield` calls per idle gap. Fixed on `scale/24-worker-wait`.
  What remains is +11% per packet on the worker at four against two, nothing at the level of the
  whole server, and it sits in the send path's shared lines.
- **The rest of the arena-thread packet path — DONE, see "Everything the arena thread can be handed"
  above.** Ship and freq changes, flags, balls and arena switching are all produced by the load
  client now. They found a third defect, the flag game reset, fixed on `scale/26`, and a 90 second
  run with everything on is clean on a Debug build, and so is 150 seconds at 1,600 clients on the
  VPS.
- **A combat load at four thousand clients.** Both code paths collapsed there on the development box,
  so nothing about combat has been measured for cost at that scale. The arena-thread vetting ran
  combat at 1,600 on the VPS for correctness, not cost. Needs the generator off the box, in Release.
- **What four workers can carry**, which cannot be measured with the generator on the same machine.
  One worker saturates at about 27,300 packets a second at ninety recipients each; the 1.78 measured
  between one and two says the residual penalty costs capacity; whether it keeps costing the same at
  four is not known, and four workers need about a hundred thousand packets a second to reach the
  wall. At ninety recipients that is eight million updates a second back to the generator, which has
  never sustained more than four. A second machine.
- **Re-measuring the worker scaling numbers on the fixed build.** Every figure in this document from
  the arena workers before "Why four workers cost more than two" predates the busy wait fix, and the
  yield fix, and overstates the penalty, the more so the more workers were running. The saturation
  figures are unaffected, since a saturated worker has no gaps to spin in. Two workers are now
  measured in Release to 5,000 on the VPS; four only at 1,600.
- **Whether isolating the workers still makes it worse.** Explained: that result was measured with
  the yield spin present, and a yield on an uncontended core returns at once, so the budget was spent
  faster there. An artefact of the spin, not a clue, and not worth repeating.
- **Where the load goes in the saturated runs**, where nothing is specced and half the offered packets
  still produce no broadcast. A counter on each early return in `HandlePositionPacket` would say in
  one run which of the three it is, and the timestamp check is the one to expect.
- **A load test that reaches a steady state at high fan-out.** The Windows runs above ramp, peak and
  then fall to a common floor rather than settling, which makes any single window unsafe to compare.
  Longer runs, or a load the client can sustain, before trusting any more scaling numbers.
- **Re-measuring `ArenaLoop:Workers` and the send thread count on the machine the server will run
  on.** Both are properties of the hardware and the load, not of the code, and the value found here
  should not be assumed to travel.
- **The `SendToOne` metric counter**, which is an ungated interlocked increment per recipient,
  around four million a second, against the metrics interface's own documented rule that a loop at
  that rate must tally locally and report once. It measured as costing about two percent here, which
  is not why the workers scale as they do, but it is still the wrong shape and
  `PositionRecipientsInView` already reports nearly the same quantity properly.
- **The periodic persist sync**, 5.2 seconds at ten thousand players with 2.3 of it in the commit.
  Chunking it stops it blocking logins for the whole sweep, but the cost itself scales with how much
  data is written, and only dirty records need writing.
- **The receive thread — not the ceiling at ten thousand.** 27% of a core at 5,000 in Release, rising
  about linearly, so about half a core at 10,000, behind the send threads by a wide margin. Sharding
  it, the plan's Phase 7, was not needed for the population; draining per wake (`scale/20`) was what
  it needed. It is still one thread, and it is the next single-thread limit after the mainloop.
- **`Mainloop:MaxQueueDepth`, still unlimited.** The arena queue is bounded now and that was enough
  here, because the overflow lands on the mainloop and the mainloop kept up. A load that overruns the
  mainloop itself has not been produced yet, and until it is, the last line of shedding is untested.
- **Connection bursts — FIXED by the receive drain.** 6,000 of 6,000 connected since `scale/20`,
  against 4,844 before it, and 5,000 connected in 28 seconds on the VPS staggered 3 ms apart.
  `--connect-delay` remains the polite way to bring thousands up at once.
