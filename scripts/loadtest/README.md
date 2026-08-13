# Load testing

`bash/loadtest.sh` runs the load client against a freshly started server and reports what the server
did, taken from the server's own `?metrics` counters.

```bash
# Build first. The script deliberately does not, so that a rebuild cannot happen partway through a
# series and change what is being measured.
dotnet build src/SubspaceServer/SubspaceServer.csproj
dotnet build src/LoadClient/LoadClient.csproj

scripts/loadtest/bash/loadtest.sh busy 2000 20 120
```

Arguments are `<label> <clients> <arenas> [duration-seconds] [extra load client arguments...]`.
Anything after the duration is passed through, so `--weapons 20 --deaths 6`, `--connect-delay 2`,
`--spread 3000` and the rest of the load client's options all work.

Each run appends a tab separated row to `results.txt` in the output directory
(`loadtest-results/` by default, or wherever `LOADTEST_OUT` points), so a series can be pasted
straight into a spreadsheet.

## Before the first run

**`SS.Core.Modules.EncryptionNull` must be loaded, and loaded before any other encryption module.**
The load client cannot do encryption. The first encryption module to answer a connection init wins, so
one sitting ahead of `EncryptionNull` will answer and then expect something the client cannot provide.
The client says as much when this is wrong, because it is otherwise close to impossible to guess.

Nothing else needs changing. The script turns the periodic metrics report on for the run and puts the
setting back afterwards, so running a load test does not quietly leave the zone configured differently.

## Reading the results

The columns that usually matter:

| column | what it tells you |
|---|---|
| `pos_pkt_s` | position packets the server processed per second. Should be about clients × 10. |
| `inview_pkt` | how many players each position update was sent to. This is what the fan out cost scales with, and comparing runs at different in-view counts is meaningless. |
| `overruns` | send thread iterations that used their whole interval. Anything above zero means data is going out late. |
| `write_waited_ms` | time spent waiting for the global player lock, per ten second window. Waiting, not holding — a writer waits for every reader, so this is where the cost of that lock actually shows up. |
| `cores` | processor time across the whole server. |
| `ml_mean_ms` | mean mainloop iteration. Careful: this is total time over iterations, so it rises when the mainloop wakes less often, even if it is doing less work. |
| `threads` | per thread processor time, which is what says *which* part of the server is busy. |

The script also prints assertion failures and error lines. **Check those every time.** A debug build
asserts its own threading contract, and a failure there means something no timing number will show.

## Things that will mislead you

Learned the hard way, each one having produced a wrong conclusion at least once.

**The load generator shares the machine.** At a few thousand clients it uses real processor time and
competes with the server. When a figure looks like a server limit, check what the client reported it
managed to send: the summary line says how many position updates each client actually sent against the
nominal ten a second. If that is below ten, the generator ran out of capacity and the run says nothing
about the server.

**Connections take a while.** Several thousand clients do not connect instantly, and the client waits
for each to be acknowledged and in an arena before counting it. Give the run a duration long enough
that the hold is comfortably longer than the ramp, or the measured window will be mostly ramp.

**The end of a run is not steady state.** Thousands of clients disconnecting at once makes the tail of
the log look nothing like the middle. The script takes the third window from the end for this reason.
Reading nearer the end once made a clear improvement look like a regression.

**Compare like with like.** The fan out cost depends on how many players are within view of each other,
which depends on the players per arena and on `--spread`. Two runs at different densities are not
comparable however similar the client count.

## What the load client is and is not

It speaks the smallest useful subset of the game protocol: connection init, enough of the reliable
layer to acknowledge and be acknowledged, login, arena entry, and position at 10 Hz. It claims support
for batched position packets, as Continuum does, so the server sends it the compact forms — pass
`--no-batch-positions` to see what a plain VIE client would get instead.

It is not a game client. It does not simulate movement beyond drifting, does not render, and does not
implement most of the protocol. It exists to put load on the position fan out and the network layer,
which is the part of the server that scales with player count.
