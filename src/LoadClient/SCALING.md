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

### Isolating the workers makes it worse, which is the first real clue

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

## What to watch next

- **What is left in the send path is the connection lock and the outgoing queue**, which is real work
  rather than bookkeeping. Ten causes have now been ruled out and the honest summary is that four
  workers cost 1.44 times one for the same work, that the extra is in the sends rather than the scan,
  and that nothing removable has been found to account for it. The clean figures on the fixed build
  are 72% of a core at one worker and 104% at four.
- **The two shared per-recipient counters**, which cost four points of a core at one worker and are
  not what scales. Worth removing anyway: the metrics interface's own rule says a loop at that rate
  must tally locally and report once, and `PositionRecipientsInView` already reports nearly the same
  quantity properly.
- **Re-measuring the worker scaling numbers on the fixed build.** Every figure in this document from
  the arena workers predates the busy wait fix and overstates the penalty, the more so the more
  workers were running. The direction and the eliminations hold; the magnitudes need redoing.
- **Whether isolating the workers still makes it worse.** That result was measured with the spin
  present, and an uncontended core is exactly where a spin costs most, so it is the one earlier
  finding that could be an artefact rather than a clue.
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
- **The receive thread**, at 20% of a core at six thousand and climbing faster than anything else.
  It is a single thread, and sharding it is the plan's Phase 7.
- **The global player write lock**, held 6.5 ms, 14.7 ms and 19.9 ms per ten second window at 3,000,
  4,500 and 6,000. Still under a quarter of a percent of wall time, but it is the one figure that
  grows with player count rather than staying flat.
- **Connection bursts.** Only 4,909 of 6,000 clients connected, staggered 3 ms apart. The receive
  thread drops connection inits under burst arrival, which is a known limit and unrelated to steady
  state, but it will matter for a zone that fills quickly.
