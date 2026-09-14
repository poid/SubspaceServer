#!/bin/bash

# The server half of a load test whose clients are on another machine. Runs on the machine with the
# server; remote-loadtest.sh runs it over SSH from the machine with the load client.
#
# It starts the server, says READY once the server is listening, waits for the client side to say
# the clients are all in and holding (a file named <label>.holding in the results directory, which
# the client side creates over SSH), takes a whole-machine window from /proc twenty seconds into the
# hold, then waits for the server to be stopped, which the client side does once its clients have
# left, and finally puts the configuration back and says DONE with the path of the server log.
# Everything it prints is read by the client side, so the two words it keys on are on lines of their
# own.
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
    echo "config: $(grep -E '^[[:space:]]*(SendThreads|MaxSendThreads|Workers|HandlePacketsOnArenaThread|UseArenaLocalPlayerIds|SendInterval|ReceiveBufferSize|SendBufferSize)[[:space:]]*=' \
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

# Every second for the life of the server, stamped with the wall clock: what this machine sent and
# received, and what it dropped on the way out or refused on the way in. The client side writes the
# same seconds from its end, and the report puts the two together, which is the only way to see a
# loss that lasts seconds rather than a window, and to say which side of the path it was on.
sample_seconds() {
    local pid="$1" iface
    local in_prev out_prev ovf_prev qd_prev rq_prev txd_prev txb_prev rxb_prev
    local in out ovf qd rq txd txb rxb
    iface="$(net_interface)"
    read -r in_prev out_prev _ ovf_prev _ <<< "$(udp_counters)"
    read -r qd_prev rq_prev <<< "$(qdisc_counters "$iface")"
    read -r _ _ _ txd_prev _ _ <<< "$(interface_counters "$iface")"
    read -r txb_prev < "/sys/class/net/$iface/statistics/tx_bytes" 2>/dev/null || txb_prev=0
    read -r rxb_prev < "/sys/class/net/$iface/statistics/rx_bytes" 2>/dev/null || rxb_prev=0
    [ "$qd_prev" = "-" ] && qd_prev=0 && rq_prev=0
    while kill -0 "$pid" 2>/dev/null; do
        sleep 1
        read -r in out _ ovf _ <<< "$(udp_counters)"
        read -r qd rq <<< "$(qdisc_counters "$iface")"
        read -r _ _ _ txd _ _ <<< "$(interface_counters "$iface")"
        read -r txb < "/sys/class/net/$iface/statistics/tx_bytes" 2>/dev/null || txb=0
        read -r rxb < "/sys/class/net/$iface/statistics/rx_bytes" 2>/dev/null || rxb=0
        [ "$qd" = "-" ] && qd=0 && rq=0
        # The bytes are the interface's, so they carry the IP and UDP headers the payload does not,
        # which at forty bytes of payload a datagram is most of what the wire carries.
        printf '%s out=%d in=%d out_bytes=%d in_bytes=%d overflows=%d qdisc_dropped=%d requeues=%d tx_dropped=%d\n' \
            "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)" \
            $(( out - out_prev )) $(( in - in_prev )) $(( txb - txb_prev )) $(( rxb - rxb_prev )) \
            $(( ovf - ovf_prev )) $(( qd - qd_prev )) $(( rq - rq_prev )) $(( txd - txd_prev ))
        in_prev=$in; out_prev=$out; ovf_prev=$ovf; qd_prev=$qd; rq_prev=$rq; txd_prev=$txd; txb_prev=$txb; rxb_prev=$rxb
    done
}
SECONDS_LOG="$OUT/${LABEL// /_}.server-seconds.log"
if [ "${#PIDS[@]}" -eq 1 ]; then
    sample_seconds "${PIDS[0]}" > "$SECONDS_LOG" 2>/dev/null &
    SAMPLER_PID=$!
fi
if [ "${#PIDS[@]}" -ne 1 ]; then
    echo "FAILED: expected exactly one SubspaceServer, found ${#PIDS[@]}."
    exit 1
fi
PID="${PIDS[0]}"

# Steady state: the client side creates this file once every client is in and the hold has begun,
# and then waits the same twenty seconds before its own window, so the two windows cover the same
# stretch of the hold. Counting metrics windows with positions in them, as this once did, fired
# during the ramp whenever the connects took longer than forty seconds, which five thousand clients
# from Windows did. Bounded, because if the clients never come there is nothing to wait for.
HOLDING_FILE="$OUT/${LABEL// /_}.holding"
rm -f "$HOLDING_FILE"
deadline=$(( SECONDS + STARTUP_ALLOWANCE ))
while [ ! -f "$HOLDING_FILE" ]; do
    sleep 2
    if ! kill -0 "$PID" 2>/dev/null; then
        echo "FAILED: the server exited before the clients settled; see $SERVER_LOG"
        exit 1
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "WARNING: the client side never said it was holding within $STARTUP_ALLOWANCE s; taking the window anyway."
        break
    fi
done
rm -f "$HOLDING_FILE"
sleep 20

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

[ -n "${SAMPLER_PID:-}" ] && { kill "$SAMPLER_PID" 2>/dev/null; wait "$SAMPLER_PID" 2>/dev/null; }
restore_conf
trap - EXIT
echo "DONE $SERVER_LOG"
