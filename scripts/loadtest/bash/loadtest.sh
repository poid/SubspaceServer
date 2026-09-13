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
# For a server on another machine, with the load client here, see remote-loadtest.sh. It reports
# through the same code, so its rows and these are comparable.
#
# Environment:
#   LOADTEST_OUT   where to put logs and results. Default: <repo>/loadtest-results
#   ZONE_DIR       the zone directory. Default: <repo>/src/SubspaceServer/Zone
#   CONFIGURATION  which build of the server and load client to run, Debug or Release. Default: Debug,
#                  which is what dotnet run picks when not told otherwise. The runtime compiles every
#                  method of a Debug built assembly without optimisation and never tiers it up, so a
#                  Debug run measures a server that is slower everywhere, by an amount that differs
#                  from one method to the next. Build the one asked for first; this script does not.
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
CONFIGURATION="${CONFIGURATION:-Debug}"

# How long to allow, beyond the requested hold, for connecting and for shutdown. Connecting several
# thousand clients is not instant, and a run killed partway through reports whatever it had reached
# rather than failing visibly, which is worse than waiting.
STARTUP_ALLOWANCE=420

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,40p'
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

# The window extraction and the report are shared with remote-loadtest.sh, so that a run whose server
# was on another machine reports through the same code and its row is comparable with this one's.
# shellcheck source=report.sh
. "$SCRIPT_DIR/report.sh"

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

results_header

echo "Load test '$LABEL': $CLIENTS clients over $ARENA_COUNT arenas, holding $DURATION s, $CONFIGURATION build."

wait_for_port_free
rm -f "$SERVER_LOG"

( cd "$ZONE_DIR/.." && timeout $((DURATION + STARTUP_ALLOWANCE + 60)) dotnet run --project SubspaceServer.csproj --no-build -c "$CONFIGURATION" -- "$(basename "$ZONE_DIR")" > "$SERVER_LOG" 2>&1 & )

wait_for_listening || exit 1

( cd "$REPO_ROOT" && timeout $((DURATION + STARTUP_ALLOWANCE)) dotnet run --project src/LoadClient/LoadClient.csproj --no-build -c "$CONFIGURATION" -- \
    --protocol game --clients "$CLIENTS" --arenas "$ARENAS" --duration "$DURATION" "$@" > "$CLIENT_LOG" 2>&1 )

sleep 3

report_run

pkill -f "SubspaceServer/bin" 2>/dev/null
wait_for_port_free
