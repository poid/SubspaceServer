#!/bin/bash

# Runs a load test against Subspace Server .NET and reports what the server did.
#
# It starts a server, points the load client at it, waits for the run to finish, and pulls a settled
# window out of the server's own ?metrics output. The server is started fresh for each run so that one
# run cannot contaminate the next.
#
# Usage:
#   loadtest.sh <label> <clients> <arenas> [duration-seconds] [extra load client arguments...]
#
# Examples:
#   loadtest.sh small 500 5
#   loadtest.sh busy 2000 20 120
#   loadtest.sh combat 600 12 90 --weapons 20 --deaths 6
#   loadtest.sh thin 4000 80 120 --connect-delay 2
#
# Results are appended to results.txt in the output directory as a tab separated row, so several runs
# can be compared by pasting the file into a spreadsheet.
#
# Environment:
#   LOADTEST_OUT   where to put logs and results. Default: <repo>/loadtest-results
#   ZONE_DIR       the zone directory. Default: <repo>/src/SubspaceServer/Zone
#
# Requirements:
#   - The server and the load client must already be built. This script does not build them, so that a
#     rebuild cannot happen midway through a series and change what is being measured.
#   - The SS.Core.Modules.EncryptionNull module must be loaded, and loaded BEFORE any other encryption
#     module. The load client cannot do encryption, and the first module to answer a connection init
#     wins, so an encryption module ahead of it will answer and then expect something the client cannot
#     provide. The client says so if this is wrong.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

ZONE_DIR="${ZONE_DIR:-$REPO_ROOT/src/SubspaceServer/Zone}"
OUT="${LOADTEST_OUT:-$REPO_ROOT/loadtest-results}"
RESULTS="$OUT/results.txt"
GLOBAL_CONF="$ZONE_DIR/conf/global.conf"

# How long to allow, beyond the requested hold, for connecting and for shutdown. Connecting several
# thousand clients is not instant, and a run killed partway through reports whatever it had reached
# rather than failing visibly, which is worse than waiting.
STARTUP_ALLOWANCE=420

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,32p'
    exit 1
}

[ $# -lt 3 ] && usage

LABEL="$1"; shift
CLIENTS="$1"; shift
ARENA_COUNT="$1"; shift
DURATION="${1:-120}"
[ $# -gt 0 ] && shift

mkdir -p "$OUT"

SERVER_LOG="$OUT/${LABEL// /_}.server.log"
CLIENT_LOG="$OUT/${LABEL// /_}.client.log"

# Arena names 0..n-1, which are the default public arenas.
ARENAS="$(seq -s, 0 $((ARENA_COUNT - 1)))"

wait_for_port_free() {
    for _ in $(seq 1 90); do
        ss -uln 2>/dev/null | grep -q ":5000 " || return 0
        sleep 1
    done
    echo "WARNING: port 5000 is still in use; is another server running?" >&2
}

wait_for_listening() {
    for _ in $(seq 1 120); do
        if grep -q "Listening on" "$SERVER_LOG" 2>/dev/null && ss -uln 2>/dev/null | grep -q ":5000 "; then
            sleep 3   # let the permanent arenas finish being created
            return 0
        fi
        if grep -q "Cannot bind" "$SERVER_LOG" 2>/dev/null; then
            echo "FAILED: the server could not bind its socket. See $SERVER_LOG" >&2
            return 1
        fi
        sleep 1
    done
    echo "FAILED: the server never started listening. See $SERVER_LOG" >&2
    return 1
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
import sys, re
log, label = sys.argv[1], sys.argv[2]
blocks = re.split(r"I <Metrics> metrics over ", open(log, errors="replace").read())[1:]
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
settled_rates = rates[first:]
mean = sum(settled_rates) / len(settled_rates)
low, high = min(settled_rates), max(settled_rates)
spread = (high - low) / mean * 100 if mean else 0.0
print(f"Settled windows:     {len(settled)} after {first} of ramp, "
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

# The server ships with periodic metrics off, which is right for a real server and leaves this script
# with nothing to read. Turned on for the run and put back afterwards, so running a load test does not
# quietly change the configuration of the zone it was run against.
ORIGINAL_REPORT_INTERVAL="$(grep -oP '^ReportInterval\s*=\s*\K.*' "$GLOBAL_CONF" 2>/dev/null | head -1)"
restore_conf() {
    if [ -n "${ORIGINAL_REPORT_INTERVAL:-}" ]; then
        sed -i "s/^ReportInterval\s*=.*/ReportInterval = $ORIGINAL_REPORT_INTERVAL/" "$GLOBAL_CONF"
    fi
}
trap 'restore_conf; pkill -f "SubspaceServer/bin" 2>/dev/null' EXIT

sed -i "s/^ReportInterval\s*=.*/ReportInterval = 10/" "$GLOBAL_CONF"

if [ ! -s "$RESULTS" ]; then
    printf 'label\tpos_pkt_s\tscanned_pkt\tinview_pkt\tsendtoone_s\toverruns\tlockread_s\twrite_waited_ms\tcores\tworkset_mb\tml_iter_s\tml_mean_ms\tthreads\n' > "$RESULTS"
fi

echo "Load test '$LABEL': $CLIENTS clients over $ARENA_COUNT arenas, holding $DURATION s."

wait_for_port_free
rm -f "$SERVER_LOG"

( cd "$ZONE_DIR/.." && timeout $((DURATION + STARTUP_ALLOWANCE + 60)) dotnet run --project SubspaceServer.csproj --no-build -- "$(basename "$ZONE_DIR")" > "$SERVER_LOG" 2>&1 & )

wait_for_listening || exit 1

( cd "$REPO_ROOT" && timeout $((DURATION + STARTUP_ALLOWANCE)) dotnet run --project src/LoadClient/LoadClient.csproj --no-build -- \
    --protocol game --clients "$CLIENTS" --arenas "$ARENAS" --duration "$DURATION" "$@" > "$CLIENT_LOG" 2>&1 )

sleep 3

# The row goes to the results file and the notes about which window it came from are kept for the
# report below, so that they appear beside the other checks rather than ahead of the run's output.
WINDOW_NOTES="$(extract_window 2>&1 >> "$RESULTS")"

echo
echo "--- what the load client saw ---"
grep -E "Connected|survived|clients sent|Server sent" "$CLIENT_LOG"

echo
echo "--- what the server did ---"
tail -1 "$RESULTS"

# Worth checking every time. An assertion failure means a debug build caught the server doing something
# its own threading contract forbids, which no timing number will tell you about.
ASSERTIONS="$(grep -c 'Assertion failed' "$SERVER_LOG")"
ERRORS="$(grep -c '^E <' "$SERVER_LOG")"
echo
echo "Assertion failures: $ASSERTIONS    Errors logged: $ERRORS"

# The number this script reports is what the server broadcast, not what the clients offered, and the
# two can differ by a lot without anything failing visibly. The known way is lag: a player specced for
# ping is still connected and still sending, but "spectators don't get their position sent to anyone"
# (Game.HandlePositionPacket), so they contribute nothing to the measurement. A run that specced half
# its clients reports half the throughput and looks perfectly stable doing it.
#
# This is not hypothetical. Two runs of the same configuration differed twofold for exactly this
# reason, and the difference was read as a threading defect for some time before anyone counted the
# specced players. So report the ratio every time, whatever the cause: a run that broadcast markedly
# less than it was offered is not comparable with one that broadcast all of it.
SPECCED="$(grep -c 'specced for' "$SERVER_LOG")"
OFFERED="$(grep -oE 'These clients sent [0-9]+' "$CLIENT_LOG" | grep -oE '[0-9]+$' | tail -1)"
MEASURED="$(tail -1 "$RESULTS" | cut -f2 | cut -d. -f1)"

echo "Specced for lag:    $SPECCED of $CLIENTS"

if [[ "$OFFERED" =~ ^[0-9]+$ ]] && [[ "$MEASURED" =~ ^[0-9]+$ ]] && [ "$OFFERED" -gt 0 ]; then
    PERCENT=$((MEASURED * 100 / OFFERED))
    echo "Offered vs measured: $OFFERED pkt/s offered, $MEASURED broadcast (${PERCENT}%)"
    if [ "$PERCENT" -lt 90 ]; then
        echo "WARNING: the server broadcast only ${PERCENT}% of the load the clients offered, so this run"
        echo "         measured less than it asked for and is NOT comparable with one that did not."
        echo "         Check the specced count above first - it is the usual cause, and it usually means"
        echo "         the load generator could not keep up rather than that the server is slow."
    elif [ "$PERCENT" -gt 110 ]; then
        # Over the offered load means the window caught a burst rather than a steady state, since the
        # server cannot broadcast more position packets than were sent to it.
        echo "WARNING: the server appears to have broadcast ${PERCENT}% of what was offered, which it"
        echo "         cannot do. The window caught a burst rather than a steady state - see the settled"
        echo "         window spread below."
    fi
fi

[ -n "$WINDOW_NOTES" ] && echo "$WINDOW_NOTES"
echo "Logs: $SERVER_LOG"
echo "      $CLIENT_LOG"
echo "Results: $RESULTS"

pkill -f "SubspaceServer/bin" 2>/dev/null
wait_for_port_free
