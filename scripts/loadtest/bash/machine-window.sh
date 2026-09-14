#!/bin/bash

# The whole machine over a window: the server's own processor time, everything else's, idle, steal,
# and the kernel's UDP counters. All from /proc, so no root is needed.
#
# Sourced, not run. validate.sh brackets a perf stat with it on the machine running the load test,
# and server-side.sh brackets a plain sleep with it on a machine running only the server. Either way
# the caller does:
#
#   machine_window_begin "$PID"
#   ... the window ...
#   machine_window_end
#   machine_window_print "$SECONDS_ASKED_FOR"
#
# Why it exists: the server's own processor time cannot tell "slow because of memory traffic" from
# "slow because it is being descheduled". Whole-machine idle is what separates those, and it costs
# nothing to record. And at the edge of what a machine can do, clients receiving less than the server
# sent can be the receiving sockets overflowing rather than the server falling short; the kernel
# counts every datagram it discarded for want of buffer, machine wide, and that is what tells the
# two apart.

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

# The interface the default route leaves by, which is the one the clients are behind when they are
# on another machine.
net_interface() {
    ip route show default 2>/dev/null | awk '{ for (i = 1; i <= NF; i++) if ($i == "dev") { print $(i + 1); exit } }'
}

# The interface's own counters: packets each way, and what it dropped or errored each way. A drop
# here is after the socket and the queue discipline, in the driver or the virtual NIC.
interface_counters() {
    local dir="/sys/class/net/$1/statistics"
    local rx_packets tx_packets rx_dropped tx_dropped rx_errors tx_errors
    read -r rx_packets < "$dir/rx_packets" 2>/dev/null || rx_packets=0
    read -r tx_packets < "$dir/tx_packets" 2>/dev/null || tx_packets=0
    read -r rx_dropped < "$dir/rx_dropped" 2>/dev/null || rx_dropped=0
    read -r tx_dropped < "$dir/tx_dropped" 2>/dev/null || tx_dropped=0
    read -r rx_errors < "$dir/rx_errors" 2>/dev/null || rx_errors=0
    read -r tx_errors < "$dir/tx_errors" 2>/dev/null || tx_errors=0
    echo "$rx_packets $tx_packets $rx_dropped $tx_dropped $rx_errors $tx_errors"
}

# The root queue discipline's drops and requeues, which is where a burst that the interface cannot
# take at line rate goes missing: fq_codel drops for latency once its queue is standing, and a
# requeue is the driver refusing a packet because its ring was full. Reading the statistics needs no
# privilege. The root's line is the first with "dropped" in it; an mq root sums its children there.
qdisc_counters() {
    if ! command -v tc >/dev/null 2>&1; then
        echo "- -"
        return
    fi
    tc -s qdisc show dev "$1" 2>/dev/null | awk '
        /dropped/ {
            for (i = 1; i <= NF; i++) {
                if ($i == "(dropped") dropped = $(i + 1)
                if ($i == "requeues") requeues = $(i + 1)
            }
            gsub(/[,)]/, "", dropped); gsub(/[,)]/, "", requeues)
            print dropped + 0, requeues + 0
            exit
        }'
}

# Datagrams the kernel dropped for want of room in a processor's backlog queue, summed over the
# processors; the second column of each softnet_stat line, in hex.
softnet_dropped() {
    local total=0 line
    while read -r line; do
        set -- $line
        total=$(( total + 16#$2 ))
    done < /proc/net/softnet_stat
    echo "$total"
}

machine_window_begin() {
    MACHINE_WINDOW_PID="$1"
    MACHINE_WINDOW_IF="$(net_interface)"
    read -ra CPU_BEFORE < /proc/stat
    read -r UDP_IN_BEFORE UDP_OUT_BEFORE UDP_INERR_BEFORE UDP_RCVBUF_BEFORE UDP_SNDBUF_BEFORE <<< "$(udp_counters)"
    if [ -n "$MACHINE_WINDOW_IF" ]; then
        read -r IF_RX_BEFORE IF_TX_BEFORE IF_RXDROP_BEFORE IF_TXDROP_BEFORE IF_RXERR_BEFORE IF_TXERR_BEFORE <<< "$(interface_counters "$MACHINE_WINDOW_IF")"
        read -r QDISC_DROP_BEFORE QDISC_REQUEUE_BEFORE <<< "$(qdisc_counters "$MACHINE_WINDOW_IF")"
        SOFTNET_BEFORE="$(softnet_dropped)"
    fi
    PROC_BEFORE="$(process_ticks "$MACHINE_WINDOW_PID")"
    WALL_BEFORE="$(date +%s%N)"
}

machine_window_end() {
    WALL_AFTER="$(date +%s%N)"
    PROC_AFTER="$(process_ticks "$MACHINE_WINDOW_PID")"
    if [ -n "$MACHINE_WINDOW_IF" ]; then
        SOFTNET_AFTER="$(softnet_dropped)"
        read -r QDISC_DROP_AFTER QDISC_REQUEUE_AFTER <<< "$(qdisc_counters "$MACHINE_WINDOW_IF")"
        read -r IF_RX_AFTER IF_TX_AFTER IF_RXDROP_AFTER IF_TXDROP_AFTER IF_RXERR_AFTER IF_TXERR_AFTER <<< "$(interface_counters "$MACHINE_WINDOW_IF")"
    fi
    read -r UDP_IN_AFTER UDP_OUT_AFTER UDP_INERR_AFTER UDP_RCVBUF_AFTER UDP_SNDBUF_AFTER <<< "$(udp_counters)"
    read -ra CPU_AFTER < /proc/stat
}

# Prints the window. The first argument is how long the window was meant to be, so that a window
# that was stretched by something in it can be flagged; the second names the process whose time was
# taken, "server" unless told otherwise.
machine_window_print() {
    local asked="$1"
    local label="${2:-server}"
    local elapsed total idle steal nproc_count clk_tck machine_cores server_cores idle_cores i
    local overflows other_errors

    # Divide by the window that was actually bracketed, not the one that was asked for, so that
    # anything stretching it produces a figure that is still true rather than one that is quietly
    # inflated.
    elapsed="$(echo "scale=3; ($WALL_AFTER - $WALL_BEFORE) / 1000000000" | bc)"
    if [ "$(echo "$elapsed > $asked * 11 / 10" | bc)" -eq 1 ]; then
        echo "WARNING: the readings bracket ${elapsed}s but the window was meant to be ${asked}s." >&2
        echo "         The core figures below cover the longer window." >&2
    fi

    # Fields 1..8 are user nice system idle iowait irq softirq steal. guest and guest_nice are
    # already counted inside user and nice, so including them would double count.
    total=0
    for i in 1 2 3 4 5 6 7 8; do
        total=$(( total + CPU_AFTER[i] - CPU_BEFORE[i] ))
    done
    idle=$(( (CPU_AFTER[4] - CPU_BEFORE[4]) + (CPU_AFTER[5] - CPU_BEFORE[5]) ))
    steal=$(( CPU_AFTER[8] - CPU_BEFORE[8] ))
    nproc_count="$(nproc)"
    clk_tck="$(getconf CLK_TCK)"

    machine_cores="$(echo "scale=2; ($total - $idle) * $nproc_count / $total" | bc)"
    server_cores="$(echo "scale=2; ($PROC_AFTER - $PROC_BEFORE) / $clk_tck / $elapsed" | bc)"
    idle_cores="$(echo "scale=2; $nproc_count - $machine_cores" | bc)"

    echo
    echo "--- the whole machine over the same window ---"
    printf '%s %s cores, everything else %s, idle %s of %s vCPUs (steal %s%%)\n' \
        "$label" \
        "$server_cores" \
        "$(echo "scale=2; $machine_cores - $server_cores" | bc)" \
        "$idle_cores" \
        "$nproc_count" \
        "$(echo "scale=1; $steal * 100 / $total" | bc)"

    if [ "$(echo "$idle_cores < 1" | bc)" -eq 1 ]; then
        echo
        echo "WARNING: under one core idle. This run measures where the machine ran out of processors,"
        echo "         not the change being tested. If the load generator runs here too, it is likely"
        echo "         what ran it out; lower the client count or move the generator to another box."
    fi

    # InErrors includes the buffer overflows, so the remainder is every other reason a datagram was
    # refused: checksum, no socket, memory.
    overflows=$(( UDP_RCVBUF_AFTER - UDP_RCVBUF_BEFORE ))
    other_errors=$(( (UDP_INERR_AFTER - UDP_INERR_BEFORE) - overflows ))
    echo
    echo "--- the machine's UDP over the same window ---"
    printf '%s datagrams/s in, %s out; receive buffer overflows %s, other receive errors %s, send buffer errors %s\n' \
        "$(echo "($UDP_IN_AFTER - $UDP_IN_BEFORE) / $elapsed" | bc)" \
        "$(echo "($UDP_OUT_AFTER - $UDP_OUT_BEFORE) / $elapsed" | bc)" \
        "$overflows" \
        "$other_errors" \
        "$(( UDP_SNDBUF_AFTER - UDP_SNDBUF_BEFORE ))"

    if [ "$overflows" -gt 0 ]; then
        echo "NOTE: an overflow is the kernel discarding a datagram that arrived faster than its socket was"
        echo "      drained. The server's socket has its own drop counter in ?metrics; if that did not move,"
        echo "      these were other sockets on this machine, and with the load generator here that means"
        echo "      any shortfall in what the clients received is the generator's, not the server's."
    fi

    # Past the sockets: the interface itself and the queue discipline in front of it. A datagram the
    # socket accepted can still be lost here, and this is the only place that shows it. Send buffer
    # errors above are the socket refusing; qdisc drops are the queue giving up on latency; interface
    # drops are the driver or the virtual NIC.
    if [ -n "${MACHINE_WINDOW_IF:-}" ]; then
        echo
        echo "--- the interface $MACHINE_WINDOW_IF over the same window ---"
        printf '%s packets/s in, %s out; dropped in %s, out %s; errors in %s, out %s\n' \
            "$(echo "($IF_RX_AFTER - $IF_RX_BEFORE) / $elapsed" | bc)" \
            "$(echo "($IF_TX_AFTER - $IF_TX_BEFORE) / $elapsed" | bc)" \
            "$(( IF_RXDROP_AFTER - IF_RXDROP_BEFORE ))" \
            "$(( IF_TXDROP_AFTER - IF_TXDROP_BEFORE ))" \
            "$(( IF_RXERR_AFTER - IF_RXERR_BEFORE ))" \
            "$(( IF_TXERR_AFTER - IF_TXERR_BEFORE ))"
        if [ "$QDISC_DROP_AFTER" = "-" ]; then
            echo "queue discipline: tc is not installed here, so its drops are not known"
        else
            printf 'queue discipline dropped %s, requeues %s; backlog queue dropped %s\n' \
                "$(( QDISC_DROP_AFTER - QDISC_DROP_BEFORE ))" \
                "$(( QDISC_REQUEUE_AFTER - QDISC_REQUEUE_BEFORE ))" \
                "$(( SOFTNET_AFTER - SOFTNET_BEFORE ))"
        fi
    fi
}
