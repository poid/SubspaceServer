#!/bin/bash

# Runs a load test whose server is on another machine: this machine runs the load client, the other
# runs the server, and the report is the same one loadtest.sh produces for a local run.
#
# Over SSH it starts server-side.sh on the server machine, waits for it to say READY, runs the load
# client here against the server's address, stops the server once the clients have left, copies the
# server log back, and reports through report.sh. The server machine's whole-machine window, taken by
# server-side.sh from /proc, is printed as it arrives.
#
# Usage:
#   remote-loadtest.sh <user@host> <label> <clients> <arenas> [hold-seconds] [extra load client args...]
#
# Example:
#   REMOTE_REPO=/zones/qsplayground/src/SubspaceServer \
#   CONFIGURATION=Release \
#   remote-loadtest.sh poid@85.190.243.122 far-4000 4000 40 150 --spread 16384 --connect-delay 3
#
# Environment:
#   REMOTE_REPO     the repository on the server machine, which must have this branch pulled and the
#                   server built. Required.
#   SERVER_IP       the address the load client connects to. Default: the host part of <user@host>.
#   SERVER_PORT     the game port. Default: 5000
#   CONFIGURATION   Debug or Release, for both the server there and the load client here. Default: Debug
#   LOADTEST_OUT    where logs and results go here. Default: <repo>/loadtest-results
#   CLIENT_DOTNET   a dotnet to run the load client with instead of this one, given the dll built
#                   here, which is the same IL on any platform. From WSL, "dotnet.exe" runs it on
#                   Windows itself, with its sockets on Windows' stack rather than behind WSL's NAT,
#                   which was measured to lose packets at about 240,000 datagrams a second in total.
#                   A Windows process is not visible from /proc, so the load client's machine window
#                   is then taken through powershell.exe instead (windows-window.sh): the client's
#                   processor time, idle, Windows' UDP counters and each adapter's discards.
#
# Requirements:
#   - SSH to the server machine. One connection is opened and shared for the whole run, so a password
#     is asked for once at most; a key makes it unattended.
#   - The server's game port open to this machine, and every client here will arrive from one address,
#     so anything between the two that tracks UDP flows, a home router's NAT table for one, has to
#     hold as many flows as there are clients.
#   - Bandwidth: the server sends each client its view of the arena, about 2 KB/s per client at 18 in
#     view with arena-local ids and twice that without, so thousands of clients are a link measured in
#     tens or hundreds of megabits down to this machine.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

OUT="${LOADTEST_OUT:-$REPO_ROOT/loadtest-results}"
RESULTS="$OUT/results.txt"
CONFIGURATION="${CONFIGURATION:-Debug}"
SERVER_PORT="${SERVER_PORT:-5000}"
STARTUP_ALLOWANCE=420

# shellcheck source=report.sh
. "$SCRIPT_DIR/report.sh"
# shellcheck source=machine-window.sh
. "$SCRIPT_DIR/machine-window.sh"
# shellcheck source=windows-window.sh
. "$SCRIPT_DIR/windows-window.sh"

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' | sed -n '2,36p'
    exit 1
}

[ $# -lt 4 ] && usage

SERVER_SSH="$1"; shift
LABEL="$1"; shift
CLIENTS="$1"; shift
ARENA_COUNT="$1"; shift
DURATION="${1:-120}"
[ $# -gt 0 ] && shift

if [ -z "${REMOTE_REPO:-}" ]; then
    echo "REMOTE_REPO is not set: the path of the repository on the server machine." >&2
    exit 1
fi

SERVER_IP="${SERVER_IP:-${SERVER_SSH#*@}}"

mkdir -p "$OUT"

SERVER_LOG="$OUT/${LABEL// /_}.server.log"
CLIENT_LOG="$OUT/${LABEL// /_}.client.log"
REMOTE_LOG="$OUT/${LABEL// /_}.remote.log"
ARENAS="$(seq -s, 0 $((ARENA_COUNT - 1)))"

# One SSH connection for everything: the server side, the stop, and the copy back. Opened here so
# that the password prompt, if there is one, comes before anything is timed.
SSH_OPTS=(-o ControlMaster=auto -o "ControlPath=$HOME/.ssh/loadtest-%C" -o ControlPersist=15m -o ServerAliveInterval=30)
mkdir -p "$HOME/.ssh"
if ! ssh "${SSH_OPTS[@]}" "$SERVER_SSH" true; then
    echo "Could not open an SSH connection to $SERVER_SSH." >&2
    exit 1
fi

remote() {
    ssh "${SSH_OPTS[@]}" "$SERVER_SSH" "$@"
}

if [ "$(remote "pgrep -xc SubspaceServer || true")" -gt 0 ]; then
    echo "A SubspaceServer is already running on $SERVER_SSH. Kill it first: ssh $SERVER_SSH pkill -x SubspaceServer" >&2
    exit 1
fi

results_header

echo "Remote load test '$LABEL': $CLIENTS clients here over $ARENA_COUNT arenas against $SERVER_IP:$SERVER_PORT, holding $DURATION s, $CONFIGURATION build."

rm -f "$SERVER_LOG" "$CLIENT_LOG" "$REMOTE_LOG"

remote "cd '$REMOTE_REPO' && CONFIGURATION='$CONFIGURATION' bash scripts/loadtest/bash/server-side.sh '$LABEL' '$DURATION'" > "$REMOTE_LOG" 2>&1 &
REMOTE_PID=$!

stop_remote_server() {
    remote "pkill -x SubspaceServer" 2>/dev/null || true
}
trap 'stop_remote_server; kill "$REMOTE_PID" 2>/dev/null' EXIT

# Wait for READY, or for the server side to give up.
ready=0
for _ in $(seq 1 150); do
    if grep -q '^READY$' "$REMOTE_LOG" 2>/dev/null; then
        ready=1
        break
    fi
    if grep -q '^FAILED' "$REMOTE_LOG" 2>/dev/null || ! kill -0 "$REMOTE_PID" 2>/dev/null; then
        break
    fi
    sleep 1
done

# What the server side has said so far, config line included.
sed '/^READY$/d' "$REMOTE_LOG"

if [ "$ready" -ne 1 ]; then
    echo "The server side never said READY; see $REMOTE_LOG" >&2
    exit 1
fi

if [ -n "${CLIENT_DOTNET:-}" ]; then
    # The dll built here, handed to another runtime by a path that runtime understands. wslpath turns
    # a WSL path into the Windows one when the other runtime is Windows'; anywhere else it is absent
    # and the path is used as it is.
    CLIENT_DLL="$REPO_ROOT/src/LoadClient/bin/$CONFIGURATION/net10.0/SS.LoadClient.dll"
    if command -v wslpath >/dev/null 2>&1; then
        CLIENT_DLL="$(wslpath -w "$CLIENT_DLL")"
    fi
    # stdbuf, because tr writing to a file buffers by the block, and the "Holding" line this script
    # waits for is a few hundred bytes into the output: without it the line reached the file only
    # when the client exited, and both machines' windows were taken after the run instead of in it.
    ( cd "$REPO_ROOT" && timeout $((DURATION + STARTUP_ALLOWANCE)) "$CLIENT_DOTNET" "$CLIENT_DLL" \
        --protocol game --host "$SERVER_IP" --port "$SERVER_PORT" --clients "$CLIENTS" --arenas "$ARENAS" --duration "$DURATION" "$@" 2>&1 | stdbuf -oL tr -d '\r' > "$CLIENT_LOG" ) &
else
    ( cd "$REPO_ROOT" && timeout $((DURATION + STARTUP_ALLOWANCE)) dotnet run --project src/LoadClient/LoadClient.csproj --no-build -c "$CONFIGURATION" -- \
        --protocol game --host "$SERVER_IP" --port "$SERVER_PORT" --clients "$CLIENTS" --arenas "$ARENAS" --duration "$DURATION" "$@" > "$CLIENT_LOG" 2>&1 ) &
fi
CLIENT_PID=$!

# This machine over a window too, once the clients are in. The generator is the other half of every
# figure in the report, and at thousands of clients it can be the one short of processor, late with
# its timers, or behind on its receives, all of which the server reports as the client's ping and
# loss and none of which the server's own window can show.
for _ in $(seq 1 $((STARTUP_ALLOWANCE / 2))); do
    grep -q '^Holding' "$CLIENT_LOG" 2>/dev/null && break
    kill -0 "$CLIENT_PID" 2>/dev/null || break
    sleep 2
done

GENERATOR_WINDOW=""
if grep -q '^Holding' "$CLIENT_LOG" 2>/dev/null; then
    # Tell the server side the hold has begun. It waits the same twenty seconds before its window,
    # so the two windows cover the same stretch of the hold.
    remote "touch '$REMOTE_REPO/loadtest-results/${LABEL// /_}.holding'" 2>/dev/null || true
    sleep 20

    if [ -n "${CLIENT_DOTNET:-}" ]; then
        if windows_window_available; then
            windows_window_begin
            sleep 30
            windows_window_end
            GENERATOR_WINDOW="$(windows_window_print 30 2>&1)"
        else
            GENERATOR_WINDOW="(the load client ran under $CLIENT_DOTNET, outside this environment, and powershell.exe is not reachable, so its machine was not measured)"
        fi
    else
        # dotnet run starts the built executable as a child, named after the project; that child is
        # the one doing the work, not the dotnet run process above it.
        GENERATOR_PID="$(pgrep -f '/SS\.LoadClient --protocol' | head -1)"
        if [ -n "$GENERATOR_PID" ]; then
            machine_window_begin "$GENERATOR_PID"
            sleep 30
            machine_window_end
            GENERATOR_WINDOW="$(machine_window_print 30 "load client" 2>&1)"
        fi
    fi
fi

wait "$CLIENT_PID"

# Let the last window with the clients in it be written, then stop the server so the log ends there
# rather than trailing empty windows, which is what the local harness's timing gives the extraction.
sleep 3
stop_remote_server

# The server side prints DONE once the server has gone and the configuration is back.
for _ in $(seq 1 60); do
    grep -q '^DONE' "$REMOTE_LOG" 2>/dev/null && break
    kill -0 "$REMOTE_PID" 2>/dev/null || break
    sleep 1
done
wait "$REMOTE_PID" 2>/dev/null
trap - EXIT

# Everything the server side said after READY: the whole-machine window and any warnings.
echo
sed -n '/^READY$/,$p' "$REMOTE_LOG" | sed '/^READY$/d; /^DONE /d'

if [ -n "$GENERATOR_WINDOW" ]; then
    echo
    echo "=== this machine, running the load client, 30 s in the hold ==="
    printf '%s\n' "$GENERATOR_WINDOW"
fi

if ! scp "${SSH_OPTS[@]}" "$SERVER_SSH:$REMOTE_REPO/loadtest-results/${LABEL// /_}.server.log" "$SERVER_LOG" >/dev/null; then
    echo "Could not copy the server log back from $SERVER_SSH." >&2
    exit 1
fi

# The server machine's second-by-second counters, for the path section of the report.
SERVER_SECONDS_LOG="$OUT/${LABEL// /_}.server-seconds.log"
scp "${SSH_OPTS[@]}" "$SERVER_SSH:$REMOTE_REPO/loadtest-results/${LABEL// /_}.server-seconds.log" "$SERVER_SECONDS_LOG" >/dev/null 2>&1 || SERVER_SECONDS_LOG=""

report_run
