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
#   - At the edge of what the machine can do, clients receiving less than the server sent can be the
#     generator's sockets overflowing rather than the server falling short. The kernel counts every
#     datagram it discarded for want of buffer, machine-wide, and that count over the window is what
#     tells the two apart.
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
#   CONFIGURATION  which build to run, Debug or Release, passed through to loadtest.sh. Default: Debug
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
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,36p'
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
    echo "config: $(grep -E '^[[:space:]]*(SendThreads|MaxSendThreads|Workers|HandlePacketsOnArenaThread|UseArenaLocalPlayerIds)[[:space:]]*=' \
        "$GLOBAL_CONF" | tr -s ' ' | paste -sd'; ' -)"
else
    echo "WARNING: no global.conf at $GLOBAL_CONF; set ZONE_DIR if the zone is elsewhere." >&2
fi

# Authenticate now rather than at the point of measurement. A password prompt between the two
# /proc readings lands inside the window they bracket, and the processor time the server accumulates
# while somebody types is then divided by the window length as if it had happened during it. That
# reported 8.07 cores for a server using 2.78.
if ! sudo -v; then
    echo "perf needs sudo, and it was refused." >&2
    exit 1
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

# The kernel's UDP counters for the whole machine. /proc/net/snmp gives them as a line of names and
# a line of values, so the two are read together and the ones wanted are picked out by name rather
# than by position, which has changed between kernels.
udp_counters() {
    awk '/^Udp:/ {
        if (!have_names) {
            for (i = 2; i <= NF; i++) name[i] = $i
            have_names = 1
        } else {
            for (i = 2; i <= NF; i++) value[name[i]] = $i
            print value["InDatagrams"], value["OutDatagrams"], value["InErrors"], value["RcvbufErrors"], value["SndbufErrors"]
        }
    }' /proc/net/snmp
}

echo "=== attaching to $PID at steady state, $PERF_SECONDS s ==="

read -ra CPU_BEFORE < /proc/stat
read -r UDP_IN_BEFORE UDP_OUT_BEFORE UDP_INERR_BEFORE UDP_RCVBUF_BEFORE UDP_SNDBUF_BEFORE <<< "$(udp_counters)"
PROC_BEFORE="$(process_ticks "$PID")"
WALL_BEFORE="$(date +%s%N)"

sudo perf stat -e cache-misses,cache-references -p "$PID" -- sleep "$PERF_SECONDS"

WALL_AFTER="$(date +%s%N)"
PROC_AFTER="$(process_ticks "$PID")"
read -r UDP_IN_AFTER UDP_OUT_AFTER UDP_INERR_AFTER UDP_RCVBUF_AFTER UDP_SNDBUF_AFTER <<< "$(udp_counters)"
read -ra CPU_AFTER < /proc/stat

# Divide by the window that was actually bracketed, not the one that was asked for, so that anything
# stretching it produces a figure that is still true rather than one that is quietly inflated.
ELAPSED="$(echo "scale=3; ($WALL_AFTER - $WALL_BEFORE) / 1000000000" | bc)"
if [ "$(echo "$ELAPSED > $PERF_SECONDS * 11 / 10" | bc)" -eq 1 ]; then
    echo "WARNING: the readings bracket ${ELAPSED}s but perf counted ${PERF_SECONDS}s." >&2
    echo "         The core figures below cover the longer window; the counters do not." >&2
fi

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
server_cores="$(echo "scale=2; ($PROC_AFTER - $PROC_BEFORE) / $clk_tck / $ELAPSED" | bc)"
idle_cores="$(echo "scale=2; $nproc_count - $machine_cores" | bc)"

echo
echo "--- the whole machine over the same window ---"
printf 'server %s cores, everything else %s, idle %s of %s vCPUs (steal %s%%)\n' \
    "$server_cores" \
    "$(echo "scale=2; $machine_cores - $server_cores" | bc)" \
    "$idle_cores" \
    "$nproc_count" \
    "$(echo "scale=1; $steal * 100 / $total" | bc)"

if [ "$(echo "$idle_cores < 1" | bc)" -eq 1 ]; then
    echo
    echo "WARNING: under one core idle. This run measures where the machine ran out of processors,"
    echo "         not the change being tested. The load generator runs here too and at this load it"
    echo "         costs more than the server does. Lower the client count or move it to another box."
fi

# InErrors includes the buffer overflows, so the remainder is every other reason a datagram was
# refused: checksum, no socket, memory.
if [ -n "${UDP_IN_AFTER:-}" ] && [ -n "${UDP_IN_BEFORE:-}" ]; then
    overflows=$(( UDP_RCVBUF_AFTER - UDP_RCVBUF_BEFORE ))
    other_errors=$(( (UDP_INERR_AFTER - UDP_INERR_BEFORE) - overflows ))
    echo
    echo "--- the machine's UDP over the same window ---"
    printf '%s datagrams/s in, %s out; receive buffer overflows %s, other receive errors %s, send buffer errors %s\n' \
        "$(echo "($UDP_IN_AFTER - $UDP_IN_BEFORE) / $ELAPSED" | bc)" \
        "$(echo "($UDP_OUT_AFTER - $UDP_OUT_BEFORE) / $ELAPSED" | bc)" \
        "$overflows" \
        "$other_errors" \
        "$(( UDP_SNDBUF_AFTER - UDP_SNDBUF_BEFORE ))"

    if [ "$overflows" -gt 0 ]; then
        echo "NOTE: an overflow is the kernel discarding a datagram that arrived faster than its socket was"
        echo "      drained. The server's socket has its own drop counter in ?metrics; if that did not move,"
        echo "      these were the load generator's sockets, and any shortfall in what the clients received"
        echo "      is the generator's, not the server's."
    fi
fi

wait "$LOADTEST_PID"
