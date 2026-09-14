#!/bin/bash

# The report half of a load test: pulls one settled window out of the server's periodic metrics,
# appends the row to results.txt, and prints what the client saw and what the server did.
#
# Sourced, not run. loadtest.sh uses it for a server on this machine and remote-loadtest.sh for a
# server on another, so that the two report through exactly the same code and their rows in
# results.txt can be compared with each other. The caller sets LABEL, CLIENTS, SERVER_LOG,
# CLIENT_LOG, RESULTS and CONFIGURATION, then calls results_header once and report_run at the end.

results_header() {
    if [ ! -s "$RESULTS" ]; then
        printf 'label\tpos_pkt_s\tscanned_pkt\tinview_pkt\tsendtoone_s\toverruns\tlockread_s\twrite_waited_ms\tcores\tworkset_mb\tml_iter_s\tml_mean_ms\tthreads\n' > "$RESULTS"
    fi
}

# Pulls one settled window out of the server's periodic metrics.
#
# Deliberately not the last window, nor the one before it. When a few thousand clients disconnect at
# once the tail of the log is shutdown rather than steady state, and reading it once turned a clear
# improvement into an apparent regression. The third from last is safely inside the hold.
#
# The leading windows are dropped as well, and for the mirror image of that reason: clients arrive
# over the first part of the run and the load is not yet what was asked for. On a long run the two
# rules never meet, but on a short one they do -- a thirty second hold produces three windows, so
# "third from last" is the first of them, which is the one where half the clients had not yet reached
# an arena. That read as the server broadcasting half its offered load.
extract_window() {
    python3 - "$SERVER_LOG" "$LABEL" <<'PY'
import sys, re, time
log, label = sys.argv[1], sys.argv[2]

# The zone directory usually lives on a mount that occasionally fails a read outright - a run was
# lost to "OSError: [Errno 61] No data available" on a log that was perfectly readable a second
# later. Worth a couple of retries before giving up on a run that has already taken minutes.
text = None
for attempt in range(3):
    try:
        text = open(log, errors="replace").read()
        break
    except OSError as error:
        print(f"WARNING: could not read {log} ({error}). Retrying.", file=sys.stderr)
        time.sleep(2)

if text is None:
    print(f"{label}\tCOULD NOT READ THE SERVER LOG")
    sys.exit()

blocks = re.split(r"I <Metrics> metrics over ", text)[1:]
if not blocks:
    print(f"{label}\tNO DATA (is Metrics:ReportInterval set, and the Metrics module loaded?)")
    sys.exit()

def position_rate(text):
    match = re.search(r"position: ([\d.]+) pkt/s", text)
    return float(match.group(1)) if match else 0.0

# Only leading windows are dropped. A run that peaks and then degrades keeps its degraded windows,
# because that is a result rather than a ramp.
rates = [position_rate(b) for b in blocks]
peak = max(rates)
first = 0
while first < len(blocks) - 1 and rates[first] < peak * 0.9:
    first += 1
settled = blocks[first:]

if len(settled) < 3:
    print(f"WARNING: only {len(settled)} settled window(s) after {first} of ramp. Hold for longer.", file=sys.stderr)

# One window is only worth reporting if the others agree with it. A run that swings between windows
# has a number for every argument and supports none of them, and reading one window out of it looks
# exactly like reading one out of a steady run. This is reported every time rather than only when it
# is bad, so that a comparison between two runs can be checked rather than assumed.
# Measured over the windows the reported one is actually chosen from, which excludes the last two:
# the tail of a run is clients disconnecting, and counting it made steady runs look unstable. A run
# that held 14,953 to 15,047 across fifteen windows and then reported 12,315 as everyone left was
# called an 18% spread, and a four worker comparison was thrown away and repeated over it.
settled_rates = rates[first:-2] if len(settled) >= 3 else rates[first:]
mean = sum(settled_rates) / len(settled_rates)
low, high = min(settled_rates), max(settled_rates)
spread = (high - low) / mean * 100 if mean else 0.0
print(f"Settled windows:     {len(settled_rates)} after {first} of ramp and 2 of shutdown, "
      f"mean {mean:,.0f} pkt/s, spread {spread:.0f}%", file=sys.stderr)

if spread > 10:
    print(f"WARNING: the settled windows range from {low:,.0f} to {high:,.0f} pkt/s. One window out of"
          f"\n         that is not a measurement, whichever one is picked. Find what is making the run"
          f"\n         lumpy before comparing these numbers with anything.", file=sys.stderr)

block = settled[-3] if len(settled) >= 3 else settled[-1]

def field(pattern, default="-"):
    match = re.search(pattern, block)
    return match.group(1) if match else default

# The line after "threads:" only if it actually looks like one, since the per thread breakdown is not
# reported on every platform and without this the next section gets picked up and labelled as threads.
threads = re.search(r"threads:.*\n\s*I <Metrics>\s+(\S+ \d+%.*)", block)

print("\t".join([
    label,
    field(r"position: ([\d.]+) pkt/s"),
    field(r"([\d.]+) scanned/pkt"),
    field(r"([\d.]+) in-view/pkt"),
    field(r"network: (\d+) SendToOne/s"),
    field(r"(\d+) overruns"),
    field(r"player lock: (\d+) read/s"),
    field(r"write waited ([\d.]+)ms"),
    field(r"cpu: ([\d.]+) cores"),
    field(r"(\d+) MB working set"),
    field(r"mainloop: (\d+) iter/s"),
    field(r"mean ([\d.]+)ms"),
    threads.group(1).strip() if threads else "-",
]))
PY
}

# The server machine's seconds (server-side.sh's sampler) beside the load client's (its stall
# watcher), matched by wall clock. What the server machine sent minus what the clients received in
# the same second is the path's loss in that second; the server machine's own drop counters on the
# same line say whether it was lost before it left. Two clocks kept by NTP and two samplers that are
# not in step put up to a second of skew between the series, so single seconds are read with that
# in mind and the totals over the hold are the figure to trust.
align_seconds() {
    python3 - "$SERVER_SECONDS_LOG" "$CLIENT_LOG" "${WINDOWS_SECONDS_LOG:-}" <<'PY'
import re
import sys
from datetime import datetime, timezone

def stamp(text):
    return datetime.strptime(text, "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc).timestamp()

# The server machine: differences over roughly a second, keyed by the second they were taken in.
server = {}
for line in open(sys.argv[1], errors="replace"):
    m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)\.\d+Z out=(\d+) in=(\d+) out_bytes=(\d+) in_bytes=(\d+) overflows=(\d+) qdisc_dropped=(\d+) requeues=(\d+) tx_dropped=(\d+)", line)
    if m:
        v = [int(x) for x in m.groups()[1:]]
        server[m.group(1)] = dict(out=v[0], inn=v[1], out_bytes=v[2], in_bytes=v[3], overflows=v[4], qdrop=v[5], requeues=v[6], txdrop=v[7])

# The load client: the same, from its own watcher.
client = {}
for line in open(sys.argv[2], errors="replace"):
    m = re.match(r"\s+second (\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)\.\d+Z \(\+(\d+) s, (\d+) in\): received (\d+) datagrams \((\d+) KB\), sent (\d+)", line)
    if m:
        client[m.group(1)] = dict(t=int(m.group(2)), inn=int(m.group(3)), rx=int(m.group(4)), rx_kb=int(m.group(5)), tx=int(m.group(6)))

# Windows, when the client ran there: cumulative counts at irregular moments, turned into per-second
# amounts by interpolating the cumulative count at each whole second.
windows = {}
if len(sys.argv) > 3 and sys.argv[3]:
    samples = []
    try:
        for line in open(sys.argv[3], errors="replace"):
            m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d+)Z adapter_in=(\d+) adapter_bytes_in=(\d+) adapter_discarded=(\d+) adapter_errors=(\d+) udp_in=(\d+) udp_errors=(\d+) udp_noport=(\d+)", line)
            if m:
                samples.append((stamp(m.group(1)), [int(x) for x in m.groups()[1:]]))
    except OSError:
        samples = []
    samples.sort()
    if len(samples) >= 2:
        def at(t):
            # Linear interpolation of the cumulative counts at time t, between the samples around it.
            lo, hi = 0, len(samples) - 1
            if t <= samples[0][0] or t >= samples[-1][0]:
                return None
            while hi - lo > 1:
                mid = (lo + hi) // 2
                if samples[mid][0] <= t:
                    lo = mid
                else:
                    hi = mid
            (t0, a), (t1, b) = samples[lo], samples[hi]
            f = (t - t0) / (t1 - t0) if t1 > t0 else 0.0
            return [x + (y - x) * f for x, y in zip(a, b)]
        first = int(samples[0][0]) + 1
        last = int(samples[-1][0])
        for sec in range(first, last):
            a, b = at(float(sec)), at(float(sec + 1))
            if a is None or b is None:
                continue
            d = [y - x for x, y in zip(a, b)]
            key = datetime.fromtimestamp(sec, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S")
            windows[key] = dict(adapter=d[0], adapter_bytes=d[1], adapter_discarded=d[2], adapter_errors=d[3], udp=d[4], udp_errors=d[5], noport=d[6])

# Only the hold, and only once the clients are all in: the ramp's rates change too fast within a
# second for the two clocks' skew.
peak = max((c["inn"] for c in client.values()), default=0)
seconds = sorted(k for k in server if k in client and client[k]["inn"] >= peak and server[k]["out"] > 1000)
if not seconds:
    print("no second had both machines' counters; the server-seconds log or the client's second lines are missing")
    sys.exit(0)

n = len(seconds)
def total(series, field, keys):
    return sum(series[k][field] for k in keys if k in series)
def pct(short, whole):
    return 100.0 * short / whole if whole else 0.0
def mbit(bytes_per_second):
    return bytes_per_second * 8 / 1_000_000

out = total(server, "out", seconds)
out_bytes = total(server, "out_bytes", seconds)
rx = total(client, "rx", seconds)
rx_bytes = total(client, "rx_kb", seconds) * 1024
inn = total(server, "inn", seconds)
tx = total(client, "tx", seconds)

print(f"Over {n} seconds with everyone in, server machine to clients: sent {out // n:,} datagrams/s ({mbit(out_bytes / n):.0f} Mbit/s on its interface, headers included),"
      f" received {rx // n:,}/s ({mbit(rx_bytes / n):.0f} Mbit/s of payload), {pct(out - rx, out):.1f}% short.")
print(f"Clients to server machine: sent {tx // n:,}/s, received {inn // n:,}/s, {pct(tx - inn, tx):.1f}% short.")
print(f"The server machine's own drops over those seconds: queue discipline {total(server, 'qdrop', seconds)}, requeues {total(server, 'requeues', seconds)},"
      f" interface {total(server, 'txdrop', seconds)} on send, socket receive overflows {total(server, 'overflows', seconds)}.")

wkeys = [k for k in seconds if k in windows]
if wkeys:
    wn = len(wkeys)
    w_out = total(server, "out", wkeys)
    w_adapter = total(windows, "adapter", wkeys)
    w_udp = total(windows, "udp", wkeys)
    w_rx = total(client, "rx", wkeys)
    print(f"Where the shortfall sits, over the {wn} of those seconds Windows sampled too: before this machine's adapter {pct(w_out - w_adapter, w_out):.1f}%,"
          f" between the adapter and the sockets {pct(w_adapter - w_udp, w_out):.1f}%, between the sockets and the client {pct(w_udp - w_rx, w_out):.1f}%"
          f" (adapter received {w_adapter / wn:,.0f}/s at {mbit(total(windows, 'adapter_bytes', wkeys) / wn):.0f} Mbit/s; Windows' UDP errors {total(windows, 'udp_errors', wkeys):.0f}, no listener {total(windows, 'noport', wkeys):.0f}, adapter discards {total(windows, 'adapter_discarded', wkeys):.0f}).")
elif len(sys.argv) > 3 and sys.argv[3]:
    print("Windows' seconds did not overlap the hold, so the shortfall is not split by where it sits.")

# What was delivered against what was offered, in steps of the offered rate: a ceiling shows as the
# delivered rate stopping where the offered rate keeps going.
buckets = {}
for k in seconds:
    b = (server[k]["out"] // 20000) * 20000
    buckets.setdefault(b, []).append(client[k]["rx"])
rows = [(b, v) for b, v in sorted(buckets.items()) if len(v) >= 2]
if len(rows) >= 2:
    print("Delivered against offered (server machine sent, datagrams/s -> clients received, mean; seconds):")
    for b, v in rows:
        print(f"  {b:>8,}-{b + 19999:<8,} -> {sum(v) // len(v):>8,}   {len(v)}")

bad = [(k, pct(server[k]["out"] - client[k]["rx"], server[k]["out"])) for k in seconds]
bad = [(k, p) for k, p in bad if p >= 8.0]
if bad:
    worst = max(bad, key=lambda kp: kp[1])
    print(f"Seconds where the clients received at least 8% less than the server machine sent: {len(bad)} of {n}, worst {worst[1]:.0f}% at {worst[0]}Z (+{client[worst[0]]['t']} s).")
    for k, p in bad[:8]:
        s = server[k]
        w = windows.get(k)
        where = f"; adapter got {w['adapter']:,.0f}, Windows delivered {w['udp']:,.0f}" if w else ""
        print(f"  {k}Z (+{client[k]['t']} s): sent {s['out']:,}, received {client[k]['rx']:,} ({p:.0f}% short); server machine qdisc dropped {s['qdrop']}, requeues {s['requeues']}, overflows {s['overflows']}{where}")
    if len(bad) > 8:
        print(f"  ... and {len(bad) - 8} more")
else:
    print("No second where the clients received 8% or more less than the server machine sent.")
PY
}

report_run() {
    # The row is held rather than appended straight away, so that a failed extraction cannot leave the
    # report reading the previous run's row out of the results file and presenting it as this one's.
    # That is not hypothetical: a transient read error on the server log produced exactly that, and the
    # offered-versus-measured check passed because the previous run's number happened to be close.
    local notes_file row window_notes assertions errors warnings specced offered measured percent
    notes_file="$(mktemp)"
    row="$(extract_window 2>"$notes_file")"
    window_notes="$(cat "$notes_file")"
    rm -f "$notes_file"

    if [ -n "$row" ] && [ "${row%%$'\t'*}" = "$LABEL" ]; then
        printf '%s\n' "$row" >> "$RESULTS"
    else
        echo
        echo "FAILED: no metrics window could be read for '$LABEL', so nothing was written to the results"
        echo "        file. The server log is still there and can be re-read by hand; what must not happen"
        echo "        is this run being reported with the last run's numbers."
        row=""
    fi

    echo
    echo "--- what the load client saw ---"
    grep -E "Connected|survived|clients sent|Server sent|stopped reaching|STALL|^Generator:|lag view|^  [A-Za-z0-9]+: (avg|s2c|c2s|rel|effective|ploss|reliable)" "$CLIENT_LOG"

    if [ -n "${SERVER_SECONDS_LOG:-}" ] && [ -f "$SERVER_SECONDS_LOG" ]; then
        echo
        echo "--- the path, second by second ---"
        align_seconds
    fi

    echo
    echo "--- what the server did ---"
    [ -n "$row" ] && printf '%s\n' "$row"

    # Worth checking every time. An assertion failure means a debug build caught the server doing
    # something its own threading contract forbids, which no timing number will tell you about. A
    # Release build has no assertions in it, so its zero is not evidence of anything and is not
    # reported as though it were.
    assertions="$(grep -c 'Assertion failed' "$SERVER_LOG")"
    errors="$(grep -c '^E <' "$SERVER_LOG")"
    warnings="$(grep -c '^W <' "$SERVER_LOG")"
    echo
    if [ "$CONFIGURATION" = "Release" ]; then
        echo "Assertion failures: none possible (Release build)    Errors logged: $errors    Warnings logged: $warnings"
    else
        echo "Assertion failures: $assertions    Errors logged: $errors    Warnings logged: $warnings"
    fi

    # The most frequent of them, with the arena and player tags stripped so that the same message from
    # different players counts as one kind. A run that drives the handlers position packets never reach
    # is read from here: a refused ship change or a stale ball pickup is a warning naming its reason,
    # and a hundred of one kind is a pattern where one is noise.
    if [ "$errors" -gt 0 ] || [ "$warnings" -gt 0 ]; then
        grep -E '^[EW] <' "$SERVER_LOG" \
            | sed -E 's/^([EW] <[^>]+>) (\{[^}]*\} )?(\[[^]]*\] )?/\1 /' \
            | cut -c1-110 | sort | uniq -c | sort -rn | head -6 | sed 's/^/    /'
    fi

    # The number this script reports is what the server broadcast, not what the clients offered, and
    # the two can differ by a lot without anything failing visibly. The known way is lag: a player
    # specced for ping is still connected and still sending, but "spectators don't get their position
    # sent to anyone" (Game.HandlePositionPacket), so they contribute nothing to the measurement. A run
    # that specced half its clients reports half the throughput and looks perfectly stable doing it.
    #
    # This is not hypothetical. Two runs of the same configuration differed twofold for exactly this
    # reason, and the difference was read as a threading defect for some time before anyone counted the
    # specced players. So report the ratio every time, whatever the cause: a run that broadcast
    # markedly less than it was offered is not comparable with one that broadcast all of it.
    specced="$(grep -c 'specced for' "$SERVER_LOG")"
    offered="$(grep -oE 'These clients sent [0-9]+' "$CLIENT_LOG" | grep -oE '[0-9]+$' | tail -1)"
    measured="$(printf '%s' "$row" | cut -f2 | cut -d. -f1)"

    echo "Specced for lag:    $specced of $CLIENTS"

    if [[ "$offered" =~ ^[0-9]+$ ]] && [[ "$measured" =~ ^[0-9]+$ ]] && [ "$offered" -gt 0 ]; then
        percent=$((measured * 100 / offered))
        echo "Offered vs measured: $offered pkt/s offered, $measured broadcast (${percent}%)"
        if [ "$percent" -lt 90 ]; then
            echo "WARNING: the server broadcast only ${percent}% of the load the clients offered, so this run"
            echo "         measured less than it asked for and is NOT comparable with one that did not."
            echo "         Check the specced count above first - it is the usual cause, and it usually means"
            echo "         the load generator could not keep up rather than that the server is slow."
        elif [ "$percent" -gt 110 ]; then
            # Over the offered load means the window caught a burst rather than a steady state, since
            # the server cannot broadcast more position packets than were sent to it.
            echo "WARNING: the server appears to have broadcast ${percent}% of what was offered, which it"
            echo "         cannot do. The window caught a burst rather than a steady state - see the settled"
            echo "         window spread below."
        fi
    fi

    [ -n "$window_notes" ] && echo "$window_notes"
    echo "Logs: $SERVER_LOG"
    echo "      $CLIENT_LOG"
    echo "Results: $RESULTS"
}
