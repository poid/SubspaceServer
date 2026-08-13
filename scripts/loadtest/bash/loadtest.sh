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
extract_window() {
    python3 - "$SERVER_LOG" "$LABEL" <<'PY'
import sys, re
log, label = sys.argv[1], sys.argv[2]
blocks = re.split(r"I <Metrics> metrics over ", open(log, errors="replace").read())[1:]
if not blocks:
    print(f"{label}\tNO DATA (is Metrics:ReportInterval set, and the Metrics module loaded?)")
    sys.exit()
block = blocks[-3] if len(blocks) >= 3 else blocks[0]

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
extract_window >> "$RESULTS"

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
echo "Logs: $SERVER_LOG"
echo "      $CLIENT_LOG"
echo "Results: $RESULTS"

pkill -f "SubspaceServer/bin" 2>/dev/null
wait_for_port_free
