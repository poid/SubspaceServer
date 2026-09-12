#!/bin/bash

# Runs a load test with hardware performance counters and whole-machine utilisation taken over the
# same window, then prints them beside what loadtest.sh measured.
#
# Why this exists, rather than running perf by hand in a second terminal:
#
#   - Timing. A window opened after loadtest.sh prints its results measures nothing, because the
#     harness has already shut the server down by then. The first attempt at this attached to a stray
#     server left over from an earlier run and recorded a fifteenth of the real cache traffic.
#   - The server's own processor time cannot tell "slow because of memory traffic" from "slow because
#     it is being descheduled", and loadtest.sh runs the load generator on the same machine as the
#     server. Whole-machine idle is what separates those, and it costs nothing to record.
#
# Usage:
#   validate.sh <label> [clients] [arenas] [duration-seconds] [extra load client arguments...]
#
# Examples:
#   validate.sh w1 1600 16 150 --spread 16384
#   validate.sh st3 2000 20 150
#
# Environment:
#   ZONE_DIR       the zone directory. Default: <repo>/src/SubspaceServer/Zone
#   LOADTEST_OUT   where logs and results go. Default: <repo>/loadtest-results
#   PERF_SECONDS   length of the counter window. Default: 30
#
# Requirements:
#   - Everything loadtest.sh requires, and it does not build either.
#   - perf, run under sudo. A virtualised host may expose only some events; LLC-load-misses and
#     LLC-store-misses are commonly unavailable, which is why only the generic pair is asked for.
#   - bc, for the fractional core arithmetic.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

ZONE_DIR="${ZONE_DIR:-$REPO_ROOT/src/SubspaceServer/Zone}"
OUT="${LOADTEST_OUT:-$REPO_ROOT/loadtest-results}"
GLOBAL_CONF="$ZONE_DIR/conf/global.conf"
PERF_SECONDS="${PERF_SECONDS:-30}"

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,29p'
    exit 1
}

[ $# -lt 1 ] && usage

LABEL="$1"
CLIENTS="${2:-2000}"
ARENAS="${3:-20}"
DURATION="${4:-150}"
shift $(( $# < 4 ? $# : 4 ))
EXTRA=("$@")
[ ${#EXTRA[@]} -eq 0 ] && EXTRA=(--spread 16384)

SERVER_LOG="$OUT/${LABEL// /_}.server.log"

# A server left over from an earlier run is the one failure that produces plausible numbers rather
# than an error, so refuse to start rather than measure the wrong process.
if [ "$(pgrep -xc SubspaceServer || true)" -gt 0 ]; then
    echo "A SubspaceServer is already running. Kill it first: pkill -x SubspaceServer" >&2
    echo "Do not use pkill -f with a path pattern; it matches its own shell." >&2
    exit 1
fi

# Say what is actually being measured, so a label cannot misdescribe a run.
if [ -f "$GLOBAL_CONF" ]; then
    echo "config: $(grep -E '^[[:space:]]*(SendThreads|MaxSendThreads|Workers|HandlePacketsOnArenaThread)[[:space:]]*=' \
        "$GLOBAL_CONF" | tr -s ' ' | paste -sd'; ' -)"
else
    echo "WARNING: no global.conf at $GLOBAL_CONF; set ZONE_DIR if the zone is elsewhere." >&2
fi

rm -f "$SERVER_LOG"

bash "$SCRIPT_DIR/loadtest.sh" "$LABEL" "$CLIENTS" "$ARENAS" "$DURATION" "${EXTRA[@]}" &
LOADTEST_PID=$!

# Wait for steady state. The server writes a metrics summary per window, so a few "position:" lines
# mean the ramp is over and the counters will describe the settled part of the run.
windows=0
while [ "$windows" -lt 4 ]; do
    sleep 2
    if ! kill -0 "$LOADTEST_PID" 2>/dev/null; then
        echo "The load test exited before reaching steady state; see $SERVER_LOG" >&2
        exit 1
    fi
    windows=0
    [ -f "$SERVER_LOG" ] && windows=$(grep -c 'position: ' "$SERVER_LOG")
done

mapfile -t PIDS < <(pgrep -x SubspaceServer)
if [ "${#PIDS[@]}" -ne 1 ]; then
    echo "Expected exactly one SubspaceServer, found ${#PIDS[@]}. Not measuring." >&2
    exit 1
fi
PID="${PIDS[0]}"

# utime + stime in clock ticks. comm is stripped by cutting through the last ')', because it is the
# one field that can contain spaces.
process_ticks() {
    local stat fields
    stat="$(cat "/proc/$1/stat" 2>/dev/null)" || { echo 0; return; }
    stat="${stat#*) }"
    read -ra fields <<< "$stat"
    # Zero based now that pid and comm are gone, so utime is 11 and stime is 12.
    echo $(( fields[11] + fields[12] ))
}

echo "=== attaching to $PID at steady state, $PERF_SECONDS s ==="

read -ra CPU_BEFORE < /proc/stat
PROC_BEFORE="$(process_ticks "$PID")"

sudo perf stat -e cache-misses,cache-references -p "$PID" -- sleep "$PERF_SECONDS"

PROC_AFTER="$(process_ticks "$PID")"
read -ra CPU_AFTER < /proc/stat

# Fields 1..8 are user nice system idle iowait irq softirq steal. guest and guest_nice are already
# counted inside user and nice, so including them would double count.
total=0
for i in 1 2 3 4 5 6 7 8; do
    total=$(( total + CPU_AFTER[i] - CPU_BEFORE[i] ))
done
idle=$(( (CPU_AFTER[4] - CPU_BEFORE[4]) + (CPU_AFTER[5] - CPU_BEFORE[5]) ))
steal=$(( CPU_AFTER[8] - CPU_BEFORE[8] ))
nproc_count="$(nproc)"
clk_tck="$(getconf CLK_TCK)"

machine_cores="$(echo "scale=2; ($total - $idle) * $nproc_count / $total" | bc)"
server_cores="$(echo "scale=2; ($PROC_AFTER - $PROC_BEFORE) / $clk_tck / $PERF_SECONDS" | bc)"

echo
echo "--- the whole machine over the same $PERF_SECONDS s ---"
printf 'server %s cores, everything else %s, idle %s of %s vCPUs (steal %s%%)\n' \
    "$server_cores" \
    "$(echo "scale=2; $machine_cores - $server_cores" | bc)" \
    "$(echo "scale=2; $nproc_count - $machine_cores" | bc)" \
    "$nproc_count" \
    "$(echo "scale=1; $steal * 100 / $total" | bc)"
echo "Idle near zero means the machine ran out of processors and the comparison measures the"
echo "machine rather than the change being tested. The load generator runs here too."

wait "$LOADTEST_PID"
