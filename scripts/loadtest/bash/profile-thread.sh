#!/bin/bash

# Runs a load test and takes a hardware sampled profile of one server thread during it, resolving
# JIT compiled frames.
#
# Why hardware sampling rather than dotnet-trace: dotnet-trace suspends threads at garbage collection
# safe points, and safe points are not where time is spent. Asked where an arena worker's time went it
# answered DateTime.UtcNow, at 95%, for a call costing 14 ns. perf samples on a counter overflow
# wherever the processor happens to be, so it has no such bias - but it needs a machine that exposes a
# PMU, which is why this could not be done on the development box.
#
# Usage:
#   profile-thread.sh <label> <thread-name> [clients] [arenas] [duration] [extra load client args...]
#
# Examples:
#   profile-thread.sh p-w1 Network-send-0 1600 16 150 --spread 16384
#   profile-thread.sh p-w1 ArenaLoop-0 1600 16 150 --spread 16384
#
# The thread name is matched against /proc/<pid>/task/*/comm, which Linux truncates to 15 characters,
# so a longer name should be given as its first 15.
#
# Environment:
#   ZONE_DIR, LOADTEST_OUT, CONFIGURATION   as loadtest.sh
#   PERF_SECONDS             length of the sample window. Default: 30
#   PERF_FREQ                samples per second. Default: 999, deliberately not 1000 so that the
#                            sampling cannot lock step with anything running on a 1 ms period.
#   PERF_EVENT               what to sample on. Default: perf's own, which is cycles wherever there is
#                            a PMU, and attributes time to the instruction that was executing. Set it
#                            to cache-misses to attribute instead to the instructions that missed. That
#                            is the question when a thread is suspected of being memory bound rather
#                            than instruction bound: a cycles profile shows a stall as time in whichever
#                            method the stalled instruction sits in, indistinguishable from that method
#                            simply doing more work.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
OUT="${LOADTEST_OUT:-$REPO_ROOT/loadtest-results}"
PERF_SECONDS="${PERF_SECONDS:-30}"
PERF_FREQ="${PERF_FREQ:-999}"
PERF_EVENT="${PERF_EVENT:-}"

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,33p'
    exit 1
}

[ $# -lt 2 ] && usage

LABEL="$1"
THREAD_NAME="$2"
CLIENTS="${3:-1600}"
ARENAS="${4:-16}"
DURATION="${5:-150}"
shift $(( $# < 5 ? $# : 5 ))
EXTRA=("$@")
[ ${#EXTRA[@]} -eq 0 ] && EXTRA=(--spread 16384)

SERVER_LOG="$OUT/${LABEL// /_}.server.log"
PERF_DATA="$OUT/${LABEL// /_}.perf.data"
PERF_TEXT="$OUT/${LABEL// /_}.perf.txt"

if [ "$(pgrep -xc SubspaceServer || true)" -gt 0 ]; then
    echo "A SubspaceServer is already running. Kill it first: pkill -x SubspaceServer" >&2
    exit 1
fi

if ! sudo -v; then
    echo "perf needs sudo, and it was refused." >&2
    exit 1
fi

# Makes the runtime write /tmp/perf-<pid>.map, without which every managed frame reports as an
# address in an anonymous mapping and the profile is unreadable. It must be set before the server
# starts, which is why this wrapper exports it rather than the caller being asked to remember.
export DOTNET_PerfMapEnabled=1

# The map alone is not enough. By default the runtime maps JIT compiled code twice - writable in one
# mapping, executable in another, backed by a memfd - so that no page is ever both. perf attributes
# the samples to that memfd, reports them as "memfd:doublemapper", and never consults the map, which
# is keyed by the addresses the map file describes. Turning the double mapping off puts JIT code
# where the map says it is. It weakens a hardening measure, which is why it is set here for a
# profiling run rather than anywhere a server would be started for real.
export DOTNET_EnableWriteXorExecute=0

# Say what is actually being measured, so a label cannot misdescribe a run. Two profiles were once
# taken of configurations that differed in a second setting left over from the run before.
GLOBAL_CONF="${ZONE_DIR:-$REPO_ROOT/src/SubspaceServer/Zone}/conf/global.conf"
if [ -f "$GLOBAL_CONF" ]; then
    echo "config: $(grep -E '^[[:space:]]*(SendThreads|MaxSendThreads|Workers|HandlePacketsOnArenaThread)[[:space:]]*=' \
        "$GLOBAL_CONF" | tr -s ' ' | paste -sd'; ' -)"
else
    echo "WARNING: no global.conf at $GLOBAL_CONF; set ZONE_DIR if the zone is elsewhere." >&2
fi

mkdir -p "$OUT"
rm -f "$SERVER_LOG"

bash "$SCRIPT_DIR/loadtest.sh" "$LABEL" "$CLIENTS" "$ARENAS" "$DURATION" "${EXTRA[@]}" &
LOADTEST_PID=$!

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

TID=""
for task in /proc/"$PID"/task/*; do
    [ -r "$task/comm" ] || continue
    if [ "$(cat "$task/comm")" = "$THREAD_NAME" ]; then
        TID="$(basename "$task")"
        break
    fi
done

if [ -z "$TID" ]; then
    echo "No thread named '$THREAD_NAME' in process $PID. Threads running now:" >&2
    cat /proc/"$PID"/task/*/comm 2>/dev/null | sort -u >&2
    kill "$LOADTEST_PID" 2>/dev/null
    exit 1
fi

if [ ! -f "/tmp/perf-$PID.map" ]; then
    echo "WARNING: /tmp/perf-$PID.map does not exist, so managed frames will not resolve." >&2
    echo "         The server was probably started without DOTNET_PerfMapEnabled=1." >&2
fi

# The map is consulted only for addresses perf cannot attribute to a file. JIT code in the double
# mapping belongs to a memfd, which is a file, so the map is never reached and every managed frame
# reports as an offset into "memfd:doublemapper". Say so here, with the evidence, rather than leaving
# it to be inferred from the report.
if grep -q doublemapper "/proc/$PID/maps" 2>/dev/null; then
    echo "WARNING: JIT code is still double mapped, so managed frames will report as" >&2
    echo "         'memfd:doublemapper' and the perf map will not be used." >&2
    echo "         DOTNET_EnableWriteXorExecute in this process's environment:" >&2
    echo "         $(tr '\0' '\n' < "/proc/$PID/environ" 2>/dev/null | grep -E '^DOTNET_(EnableWriteXorExecute|PerfMapEnabled)=' | paste -sd' ' - || echo '(not set - it did not reach the process)')" >&2
    echo "         If it is set to 0 and this warning still appears, the runtime is ignoring it and" >&2
    echo "         the jitdump route is needed instead: DOTNET_PerfMapEnabled=3, perf record -k 1," >&2
    echo "         then perf inject --jit." >&2
fi

echo "=== profiling thread $THREAD_NAME (tid $TID) of $PID for $PERF_SECONDS s at $PERF_FREQ Hz${PERF_EVENT:+ on $PERF_EVENT} ==="

# How often the thread slept during the window, from the kernel's own count. A thread that blocks
# between items pays to be woken for each of them, and that cost lands in the kernel and in the
# runtime's wait code rather than in any method of the server's, so a profile alone cannot say
# whether a kernel share grew because of it. Two arena workers at 42% and four at 28% doubled the
# kernel's share per packet, and this is what tells the two readings of that apart.
thread_switches() {
    awk -v key="$1:" '$1 == key { print $2 }' "/proc/$PID/task/$TID/status" 2>/dev/null || echo 0
}
VOLUNTARY_BEFORE="$(thread_switches voluntary_ctxt_switches)"
INVOLUNTARY_BEFORE="$(thread_switches nonvoluntary_ctxt_switches)"

# Counted over the same window, beside the samples: how many times the thread gave up the processor
# of its own accord (sched_yield), asked to be blocked (futex), was switched out, and was moved to
# another CPU. The samples say where the thread's time went; these say how many times the events
# that put it in the kernel happened, which is what turns "a fifth of the thread is in the kernel"
# into a cost per event. The distinction they were added for: a thread switched out 5,000 times a
# second that blocked only 400 of them is yielding, which is what SemaphoreSlim does for up to 140
# iterations before it will block, and that is spinning between items rather than sleeping.
COUNTS_FILE="$OUT/${LABEL// /_}.counts.txt"
sudo perf stat -e syscalls:sys_enter_sched_yield,syscalls:sys_enter_futex,context-switches,cpu-migrations \
    --tid "$TID" -o "$COUNTS_FILE" -- sleep "$PERF_SECONDS" &
COUNTS_PID=$!

sudo perf record ${PERF_EVENT:+-e "$PERF_EVENT"} -F "$PERF_FREQ" --tid "$TID" -o "$PERF_DATA" -- sleep "$PERF_SECONDS"

wait "$COUNTS_PID" 2>/dev/null
VOLUNTARY_AFTER="$(thread_switches voluntary_ctxt_switches)"
INVOLUNTARY_AFTER="$(thread_switches nonvoluntary_ctxt_switches)"

# Reported while the process is alive, because the JIT map is keyed by pid and a later run's server
# would be a different one. Under sudo, because /proc/kallsyms is restricted to root on a default
# kernel and without it every kernel frame reports as a bare address.
{
    echo "=== by library ==="
    sudo perf report -i "$PERF_DATA" --stdio --sort dso --percent-limit 0.4 2>/dev/null
    echo
    echo "=== by library and symbol ==="
    sudo perf report -i "$PERF_DATA" --stdio --sort dso,symbol --percent-limit 0.4 2>/dev/null
} > "$PERF_TEXT"
sudo chown "$(id -u):$(id -g)" "$PERF_DATA" "$PERF_TEXT"
[ -f "$COUNTS_FILE" ] && sudo chown "$(id -u):$(id -g)" "$COUNTS_FILE"

echo
echo "--- which library $THREAD_NAME spent its time in ---"
sed -n '/^=== by library ===/,/^=== by library and symbol ===/p' "$PERF_TEXT" | sed -n '/^ *[0-9]/p' | head -12
echo
echo "--- how often $THREAD_NAME slept ---"
echo "$(( (VOLUNTARY_AFTER - VOLUNTARY_BEFORE) / PERF_SECONDS )) times/s blocked and woken, $(( (INVOLUNTARY_AFTER - INVOLUNTARY_BEFORE) / PERF_SECONDS )) times/s switched out while still runnable (preempted, or yielded)"
if [ -s "$COUNTS_FILE" ]; then
    awk -v seconds="$PERF_SECONDS" '
        /<not (supported|counted)>/ { printf "%12s     %s\n", "n/a", $NF; next }
        /sched_yield|futex|context-switches|cpu-migrations/ {
            gsub(",", "", $1)
            if ($1 ~ /^[0-9]+$/) printf "%12d/s  %s\n", $1 / seconds, $2
        }' "$COUNTS_FILE"
fi
echo
echo "--- and where in it ---"
sed -n '/^=== by library and symbol ===/,$p' "$PERF_TEXT" | sed -n '/^ *[0-9]/p' | head -20
echo
echo "--- the kernel's part of that, since it is what changes when a thread sleeps more ---"
sed -n '/^=== by library and symbol ===/,$p' "$PERF_TEXT" | grep '\[k\] ' | head -10
echo
echo "full profile: $PERF_TEXT"

# perf names the frames it resolved through /tmp/perf-<pid>.map as belonging to "[JIT] tid <pid>", so
# that, and not the map's own file name, is the sign that the map was used.
if ! grep -qE 'perf-.*\.map|\[JIT\] tid' "$PERF_TEXT"; then
    echo
    echo "NOTE: no JIT map appears in the profile, so managed frames are unresolved. If most of the"
    echo "      time is in libcoreclr.so with no symbols, install the runtime's debug symbols to name"
    echo "      them; if it is in the kernel, the symbols are already there now that report runs as root."
fi

# The map tags every method with how it was compiled, and [MinOptJitted] is code the JIT compiled with
# optimisation off and will never tier up: nothing inlined, every local kept in memory. The runtime does
# that to every method of an assembly built in Debug. It is why a Debug profile shows methods that a
# Release build would have inlined out of existence, at percentages that say nothing about the server
# that will be deployed. Measured over every symbol, not only the ones printed above.
MINOPT_PERCENT="$(sudo perf report -i "$PERF_DATA" --stdio --sort symbol --percent-limit 0 2>/dev/null \
    | awk '/MinOptJitted/ { sum += $1 } END { printf "%.0f", sum }')"
if [ "${MINOPT_PERCENT:-0}" -ge 5 ]; then
    echo
    echo "WARNING: ${MINOPT_PERCENT}% of the samples are in methods compiled without optimisation"
    echo "         ([MinOptJitted]), which is what every method of a Debug built assembly gets. The"
    echo "         proportions above are a Debug build's, not the deployed server's. For those, build"
    echo "         with 'dotnet build -c Release src/SubspaceServer.slnx' and run with CONFIGURATION=Release."
fi

wait "$LOADTEST_PID"
