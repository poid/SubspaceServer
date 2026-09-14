#!/bin/bash

# The server half of a load test whose clients are on another machine. Runs on the machine with the
# server; remote-loadtest.sh runs it over SSH from the machine with the load client.
#
# It starts the server, says READY once the server is listening, waits for the clients to arrive and
# settle, takes a whole-machine window from /proc, then waits for the server to be stopped, which
# the client side does once its clients have left, and finally puts the configuration back and says
# DONE with the path of the server log. Everything it prints is read by the client side, so the two
# words it keys on are on lines of their own.
#
# Usage:
#   server-side.sh <label> <hold-seconds>
#
# Environment:
#   ZONE_DIR, LOADTEST_OUT, CONFIGURATION   as loadtest.sh
#   WINDOW_SECONDS                          length of the whole-machine window. Default: 30
#
# Why /proc and not perf: perf needs root, and over SSH there is nobody at the prompt. Processor time
# and idle from /proc need nothing, and the hardware counters were never what decided a run.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

ZONE_DIR="${ZONE_DIR:-$REPO_ROOT/src/SubspaceServer/Zone}"
OUT="${LOADTEST_OUT:-$REPO_ROOT/loadtest-results}"
GLOBAL_CONF="$ZONE_DIR/conf/global.conf"
CONFIGURATION="${CONFIGURATION:-Debug}"
WINDOW_SECONDS="${WINDOW_SECONDS:-30}"

# As loadtest.sh: how long to allow beyond the hold for the clients to arrive and leave.
STARTUP_ALLOWANCE=420

# shellcheck source=machine-window.sh
. "$SCRIPT_DIR/machine-window.sh"

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,20p'
    exit 1
}

[ $# -lt 2 ] && usage

LABEL="$1"
DURATION="$2"
SERVER_LOG="$OUT/${LABEL// /_}.server.log"

if [ "$(pgrep -xc SubspaceServer || true)" -gt 0 ]; then
    echo "FAILED: a SubspaceServer is already running here. Kill it first: pkill -x SubspaceServer"
    exit 1
fi

if [ -f "$GLOBAL_CONF" ]; then
    echo "config: $(grep -E '^[[:space:]]*(SendThreads|MaxSendThreads|Workers|HandlePacketsOnArenaThread|UseArenaLocalPlayerIds|SendInterval)[[:space:]]*=' \
        "$GLOBAL_CONF" | tr -s ' ' | paste -sd'; ' -)"
else
    echo "FAILED: no global.conf at $GLOBAL_CONF; set ZONE_DIR if the zone is elsewhere."
    exit 1
fi

# Periodic metrics on for the run and back afterwards, as loadtest.sh does.
ORIGINAL_REPORT_INTERVAL="$(grep -oP '^ReportInterval\s*=\s*\K.*' "$GLOBAL_CONF" 2>/dev/null | head -1)"
restore_conf() {
    if [ -n "${ORIGINAL_REPORT_INTERVAL:-}" ]; then
        sed -i "s/^ReportInterval\s*=.*/ReportInterval = $ORIGINAL_REPORT_INTERVAL/" "$GLOBAL_CONF"
    fi
}
trap 'restore_conf; pkill -f "SubspaceServer/bin" 2>/dev/null' EXIT

sed -i "s/^ReportInterval\s*=.*/ReportInterval = 10/" "$GLOBAL_CONF"

mkdir -p "$OUT"
rm -f "$SERVER_LOG"

echo "Server side of '$LABEL': holding $DURATION s, $CONFIGURATION build, log $SERVER_LOG"

( cd "$ZONE_DIR/.." && timeout $((DURATION + STARTUP_ALLOWANCE + 60)) dotnet run --project SubspaceServer.csproj --no-build -c "$CONFIGURATION" -- "$(basename "$ZONE_DIR")" > "$SERVER_LOG" 2>&1 & )

listening=0
for _ in $(seq 1 120); do
    if grep -q "Listening on" "$SERVER_LOG" 2>/dev/null && ss -uln 2>/dev/null | grep -q ":5000 "; then
        sleep 3   # let the permanent arenas finish being created
        listening=1
        break
    fi
    if grep -q "Cannot bind" "$SERVER_LOG" 2>/dev/null; then
        echo "FAILED: the server could not bind its socket. See $SERVER_LOG"
        exit 1
    fi
    sleep 1
done

if [ "$listening" -ne 1 ]; then
    echo "FAILED: the server never started listening. See $SERVER_LOG"
    exit 1
fi

# The word the client side waits for before it starts connecting.
echo "READY"

mapfile -t PIDS < <(pgrep -x SubspaceServer)
if [ "${#PIDS[@]}" -ne 1 ]; then
    echo "FAILED: expected exactly one SubspaceServer, found ${#PIDS[@]}."
    exit 1
fi
PID="${PIDS[0]}"

# Steady state: a few metrics windows with positions in them mean the clients have arrived and the
# ramp is over. Bounded, because if the clients never come there is nothing to wait for.
windows=0
deadline=$(( SECONDS + STARTUP_ALLOWANCE ))
while [ "$windows" -lt 4 ]; do
    sleep 2
    if ! kill -0 "$PID" 2>/dev/null; then
        echo "FAILED: the server exited before the clients settled; see $SERVER_LOG"
        exit 1
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "WARNING: no steady state within $STARTUP_ALLOWANCE s; taking the window anyway."
        break
    fi
    # grep -c prints its count even when it is zero, and exits 1 then, so its exit code is not an
    # error here and must not be turned into a second number.
    windows=$(grep -c 'position: [1-9]' "$SERVER_LOG" 2>/dev/null)
    windows=${windows:-0}
done

echo "=== whole-machine window, $WINDOW_SECONDS s at steady state ==="
machine_window_begin "$PID"
sleep "$WINDOW_SECONDS"
machine_window_end
machine_window_print "$WINDOW_SECONDS"

# The client side stops the server once its clients have left. Until then, wait; the server's own
# timeout is the backstop if the client side never does.
while kill -0 "$PID" 2>/dev/null; do
    sleep 2
done

restore_conf
trap - EXIT
echo "DONE $SERVER_LOG"
