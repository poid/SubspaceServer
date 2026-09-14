#!/bin/bash

# The Windows side of a window, for when the load client runs on Windows itself through WSL's
# interop (CLIENT_DOTNET=dotnet.exe in remote-loadtest.sh). A Windows process is invisible to /proc,
# so machine-window.sh cannot see it; this asks Windows instead, through powershell.exe, for the
# same things: the client's processor time, the machine's idle, the UDP counters, and each adapter's
# discards. Everything comes from the CIM performance classes, whose names do not change with the
# display language, unlike the counter paths Get-Counter takes.
#
# Sourced, not run. The caller does:
#
#   windows_window_begin
#   ... the window ...
#   windows_window_end
#   windows_window_print "$SECONDS_ASKED_FOR"
#
# Each snapshot is one powershell.exe start, a second or two, so the rates below are divided by the
# time between the two snapshots as Windows stamped them, not by the sleep in between.

windows_window_available() {
    command -v powershell.exe >/dev/null 2>&1
}

# One snapshot as key=value lines. The processor idle is the raw 100 ns idle counter summed over
# every logical processor, so its change divided by the change in the timestamp is idle processors.
# The load client is found by its command line; TotalProcessorTime is its user plus kernel time.
windows_snapshot() {
    powershell.exe -NoProfile -NonInteractive -Command '
        $u = Get-CimInstance Win32_PerfRawData_Tcpip_UDPv4
        "udp_in=$($u.DatagramsReceivedPerSec)"
        "udp_err=$($u.DatagramsReceivedErrors)"
        "udp_noport=$($u.DatagramsNoPortPerSec)"
        "udp_out=$($u.DatagramsSentPerSec)"
        $p = @(Get-CimInstance Win32_PerfRawData_PerfOS_Processor | Where-Object Name -ne "_Total")
        "cpus=$($p.Count)"
        "idle=$(($p | Measure-Object -Property PercentIdleTime -Sum).Sum)"
        "ts=$($p[0].Timestamp_Sys100NS)"
        $c = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*SS.LoadClient*" } | Select-Object -First 1
        if ($c) {
            $pr = Get-Process -Id $c.ProcessId
            "client_cpu=$($pr.TotalProcessorTime.TotalSeconds)"
        } else {
            "client_cpu="
        }
        Get-NetAdapterStatistics | ForEach-Object {
            "nic=$($_.Name)|$($_.ReceivedUnicastPackets)|$($_.SentUnicastPackets)|$($_.ReceivedDiscardedPackets)|$($_.ReceivedPacketErrors)|$($_.OutboundDiscardedPackets)"
        }
    ' 2>/dev/null | tr -d '\r'
}

windows_window_begin() {
    WIN_BEFORE="$(windows_snapshot)"
}

windows_window_end() {
    WIN_AFTER="$(windows_snapshot)"
}

# A value from a snapshot by key; empty if absent.
windows_value() {
    printf '%s\n' "$2" | sed -n "s/^$1=//p" | head -1
}

windows_window_print() {
    local asked="$1"
    local ts_before ts_after elapsed cpus idle_before idle_after idle_cores busy_cores
    local cpu_before cpu_after client_cores
    local in_before in_after out_before out_after err_before err_after noport_before noport_after

    ts_before="$(windows_value ts "$WIN_BEFORE")"
    ts_after="$(windows_value ts "$WIN_AFTER")"
    if [ -z "$ts_before" ] || [ -z "$ts_after" ]; then
        echo "(Windows did not answer; no window for the load client's machine)"
        return
    fi

    # Timestamp_Sys100NS is in 100 ns units.
    elapsed="$(echo "scale=3; ($ts_after - $ts_before) / 10000000" | bc)"
    if [ "$(echo "$elapsed > $asked * 11 / 10" | bc)" -eq 1 ]; then
        echo "WARNING: the snapshots bracket ${elapsed}s but the window was meant to be ${asked}s." >&2
        echo "         The figures below cover the longer window." >&2
    fi

    cpus="$(windows_value cpus "$WIN_AFTER")"
    idle_before="$(windows_value idle "$WIN_BEFORE")"
    idle_after="$(windows_value idle "$WIN_AFTER")"
    idle_cores="$(echo "scale=2; ($idle_after - $idle_before) / ($ts_after - $ts_before)" | bc)"
    busy_cores="$(echo "scale=2; $cpus - $idle_cores" | bc)"

    cpu_before="$(windows_value client_cpu "$WIN_BEFORE")"
    cpu_after="$(windows_value client_cpu "$WIN_AFTER")"
    if [ -n "$cpu_before" ] && [ -n "$cpu_after" ]; then
        client_cores="$(echo "scale=2; ($cpu_after - $cpu_before) / $elapsed" | bc)"
    else
        client_cores=""
    fi

    echo
    echo "--- the load client's machine (Windows) over the same window ---"
    if [ -n "$client_cores" ]; then
        printf 'load client %s cores, everything else %s, idle %s of %s logical processors\n' \
            "$client_cores" \
            "$(echo "scale=2; $busy_cores - $client_cores" | bc)" \
            "$idle_cores" \
            "$cpus"
    else
        printf 'load client not found among the dotnet.exe processes; the machine was %s cores busy, idle %s of %s logical processors\n' \
            "$busy_cores" "$idle_cores" "$cpus"
    fi

    in_before="$(windows_value udp_in "$WIN_BEFORE")"; in_after="$(windows_value udp_in "$WIN_AFTER")"
    out_before="$(windows_value udp_out "$WIN_BEFORE")"; out_after="$(windows_value udp_out "$WIN_AFTER")"
    err_before="$(windows_value udp_err "$WIN_BEFORE")"; err_after="$(windows_value udp_err "$WIN_AFTER")"
    noport_before="$(windows_value udp_noport "$WIN_BEFORE")"; noport_after="$(windows_value udp_noport "$WIN_AFTER")"

    echo
    echo "--- Windows' UDP over the same window ---"
    # Windows counts a datagram it could not deliver for want of buffer or memory as a receive
    # error, and one that arrived for a port nothing was listening on separately.
    printf '%s datagrams/s in, %s out; receive errors %s, no listener %s\n' \
        "$(echo "($in_after - $in_before) / $elapsed" | bc)" \
        "$(echo "($out_after - $out_before) / $elapsed" | bc)" \
        "$(( err_after - err_before ))" \
        "$(( noport_after - noport_before ))"

    # Every adapter that received anything in the window: what it received and sent, and what it
    # discarded. An adapter that discards on receive is losing packets before any socket sees them.
    printf '%s\n' "$WIN_AFTER" | grep '^nic=' | while IFS= read -r line; do
        local name rx_a tx_a rxd_a rxe_a txd_a before rx_b tx_b rxd_b rxe_b txd_b
        line="${line#nic=}"
        name="${line%%|*}"
        IFS='|' read -r _ rx_a tx_a rxd_a rxe_a txd_a <<< "$line"
        before="$(printf '%s\n' "$WIN_BEFORE" | grep -F "nic=$name|" | head -1)"
        [ -z "$before" ] && continue
        before="${before#nic=}"
        IFS='|' read -r _ rx_b tx_b rxd_b rxe_b txd_b <<< "$before"
        if [ "$(( rx_a - rx_b ))" -gt 0 ]; then
            printf 'adapter %s: %s packets/s in, %s out; discarded on receive %s, receive errors %s, discarded on send %s\n' \
                "$name" \
                "$(echo "($rx_a - $rx_b) / $elapsed" | bc)" \
                "$(echo "($tx_a - $tx_b) / $elapsed" | bc)" \
                "$(( rxd_a - rxd_b ))" \
                "$(( rxe_a - rxe_b ))" \
                "$(( txd_a - txd_b ))"
        fi
    done
}
