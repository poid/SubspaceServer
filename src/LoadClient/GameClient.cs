using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.LoadClient
{
    /// <summary>
    /// One simulated client speaking the game protocol over UDP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="ChatClient"/>, this sends position packets, so it drives the position fan out
    /// and, because it is a standard client rather than a fake player, the server actually sends
    /// position updates back. That makes it the only way to put load on the network layer: the send
    /// thread, the reliable layer, and the bandwidth limiter.
    /// </para>
    /// <para>
    /// It implements the smallest useful subset of the protocol: connection init, the reliable layer
    /// (enough to be acked and to ack), login, arena entry, and position. It requires the server to have
    /// the EncryptionNull module loaded, so that no encryption is negotiated.
    /// </para>
    /// </remarks>
    internal sealed class GameClient(string name, string password, bool batchPositions = true, int receiveBufferSize = 0) : IDisposable
    {
        private const byte ClientTypeVIE = 0x01;

        /// <summary>
        /// How many times the connection init is sent before giving up.
        /// </summary>
        private const int ConnectionInitAttempts = 5;

        /// <summary>
        /// How long to wait for a reply before sending the connection init again.
        /// </summary>
        /// <remarks>
        /// Short, because the thing being recovered from is a dropped packet rather than a slow server:
        /// waiting longer only delays the retry that was always going to be needed.
        /// </remarks>
        private static readonly TimeSpan ConnectionInitTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How many times a reliable packet is sent before giving up on it.
        /// </summary>
        private const int ReliableSendAttempts = 5;

        /// <summary>
        /// How long to wait for an acknowledgement before sending a reliable packet again.
        /// </summary>
        private static readonly TimeSpan ReliableSendTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The highest sequence number the server has acknowledged, or -1.
        /// </summary>
        /// <remarks>
        /// Highest rather than a set, since acknowledgements can arrive out of order and a sender only
        /// needs to know whether its own has gone past.
        /// </remarks>
        private long _highestAckedSequenceNumber = -1;

        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly byte[] _receiveBuffer = new byte[2048];

        /// <summary>
        /// Reused by the receive loop so that parsing a datagram does not allocate.
        /// </summary>
        private readonly List<int> _pendingAcks = new(16);

        private EndPoint? _serverEndPoint;

        /// <summary>
        /// Next sequence number to use for a reliable packet sent to the server.
        /// </summary>
        private int _outgoingSequenceNumber;

        /// <summary>
        /// What to add to this machine's tick count to get the server's, learned from the time sync
        /// exchange, or 0 until the first reply.
        /// </summary>
        /// <remarks>
        /// The server stamps a player's position time from its own clock on arena entry and takes a
        /// position packet only if its timestamp is newer than that. This client used to stamp
        /// positions with its own machine's ticks. On the same machine as the server that is the same
        /// counter, so it agreed by accident; on another machine it is a number from a different uptime
        /// that is never newer, and the server broadcast nothing from two thousand clients while every
        /// one of them sent ten positions a second. Real clients sync their clock to the server's the
        /// same way this now does.
        /// </remarks>
        private uint _serverTickOffset;

        public string Name { get; } = name;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// Whether the server has answered the login.
        /// </summary>
        /// <remarks>
        /// Asking to enter an arena before this is set gets the request thrown away, because the player
        /// is not yet in a state the server will accept one from. Waiting a fixed time instead is what
        /// this used to do, and it silently stopped working under load: the server advances logins on a
        /// ten hertz state machine, so with thousands of clients arriving at once a login takes far
        /// longer than any delay worth hard coding, and the clients that timed out never reached an
        /// arena at all. That understated every measurement taken with this tool.
        /// </remarks>
        public bool IsLoggedIn { get; private set; }

        /// <summary>
        /// Whether the server has put this client into an arena.
        /// </summary>
        /// <remarks>
        /// True once the server has said who this client is, which it does on arena entry. Positions
        /// sent before that are discarded, so there is no point starting to send them earlier.
        /// </remarks>
        public bool IsInArena => Volatile.Read(ref _ownPlayerId) >= 0;

        /// <summary>
        /// The time now on the server's clock, as well as this client knows it.
        /// </summary>
        private ServerTick ServerTime => ServerTick.Now + Volatile.Read(ref _serverTickOffset);

        public string? FailureReason { get; private set; }

        /// <summary>
        /// Position updates received from the server about other players.
        /// </summary>
        /// <remarks>
        /// This is the server's send side load, which fake players cannot produce because the fan out
        /// skips them. Summed across clients it says how much the server is actually pushing out.
        /// </remarks>
        public long PositionUpdatesReceived;

        /// <summary>
        /// Position updates this client handed to its socket.
        /// </summary>
        /// <remarks>
        /// Without this there is no way to tell a server that dropped a packet from a client that never
        /// sent one. Both look identical from the server's counters, and the difference decides whether
        /// a shortfall is the thing being measured or the thing doing the measuring.
        /// </remarks>
        public long PositionUpdatesSent;

        public long PacketsReceived;
        public long BytesReceived;

        /// <summary>
        /// Datagrams this client handed to its socket, of every kind. The time sync request reports it,
        /// and the server derives client-to-server packet loss from it against what it received.
        /// </summary>
        public long PacketsSent;

        /// <summary>
        /// When the last datagram arrived from the server, as <see cref="Environment.TickCount64"/>,
        /// or 0 if none has.
        /// </summary>
        /// <remarks>
        /// A client the server has stopped reaching cannot tell: it keeps sending and nothing comes
        /// back, and the server drops it for timeout half a minute later. Across a NAT that is what a
        /// lost or changed mapping looks like, and the time each client went silent is what says
        /// whether they all went at once, which is the network, or one by one, which is something else.
        /// </remarks>
        public long LastReceiveTicks;

        /// <summary>
        /// How long after the server stamped it the most delayed position received arrived, in
        /// centiseconds, since the stall watcher last looked. That is the path's delay plus any time
        /// the datagram spent queued on this machine before the receive loop got to it, which is what
        /// a generator that has fallen behind looks like. Written on the receive loop, read and reset
        /// by the watcher.
        /// </summary>
        public long MaxPositionAgeCs;

        /// <summary>
        /// Kill notifications seen, which confirms deaths are actually being processed rather than
        /// silently rejected.
        /// </summary>
        public long KillsSeen;

        /// <summary>
        /// Ids of other players seen entering this arena, used to name a killer the server will accept.
        /// </summary>
        private readonly List<short> _knownPlayerIds = new(64);

        private short _ownPlayerId = -1;

        /// <summary>
        /// Ship change notifications seen, for any player in the arena. The server tells the whole arena
        /// about each one, so this confirms ship changes are being processed rather than refused.
        /// </summary>
        public long ShipChangesSeen;

        /// <summary>
        /// Freq change notifications seen, likewise for any player in the arena.
        /// </summary>
        public long FreqChangesSeen;

        /// <summary>
        /// Flag pickup notifications seen, for any player in the arena.
        /// </summary>
        public long FlagPickupsSeen;

        /// <summary>
        /// Ball updates seen. The server sends one to the arena whenever a ball is picked up or shot.
        /// </summary>
        public long BallUpdatesSeen;

        /// <summary>
        /// Times the server has said who this client is, which it does on every arena entry. One more
        /// than the number of arena switches that completed.
        /// </summary>
        public long ArenaEntriesSeen;

        /// <summary>
        /// The ship the server last put this client in, or -1 before it has said.
        /// </summary>
        /// <remarks>
        /// Kept so that a ship change can ask for a different ship. Asking for the one already in is
        /// refused, with a warning in the server log that would then be mistaken for a defect.
        /// </remarks>
        private int _ownShip = -1;

        /// <summary>
        /// The freq the server last put this client on, or -1 before it has said.
        /// </summary>
        private int _ownFreq = -1;

        private const int MaxBalls = 8;

        /// <summary>
        /// What the server last said about each ball, by ball id: who carries it and its fire time.
        /// </summary>
        /// <remarks>
        /// A pickup has to quote the fire time from the server's last update of the ball, or the server
        /// refuses it as coming from stale coordinates, and it has to be for a ball nobody is carrying.
        /// Guarded by its own lock, since the receive loop writes it and the position loop reads it.
        /// </remarks>
        private readonly BallInfo[] _balls = new BallInfo[MaxBalls];

        private struct BallInfo
        {
            public bool Seen;
            public short CarrierId;
            public uint Time;
        }

        public int OwnShip => Volatile.Read(ref _ownShip);

        public int OwnFreq => Volatile.Read(ref _ownFreq);

        private const int MaxDistinctChatMessages = 16;

        /// <summary>
        /// Chat messages received, by text, with how many times each arrived.
        /// </summary>
        /// <remarks>
        /// The server explains a refused ship change, freq change, or flag touch to the player in chat
        /// and nowhere else, so this is the only record of why a request was turned down. Capped at a
        /// handful of distinct texts, since a message that carries a number is a new text every time.
        /// </remarks>
        private readonly Dictionary<string, long> _chatMessages = [];

        /// <summary>
        /// The last few chat messages exactly as received, for reading a command's reply.
        /// </summary>
        private readonly Queue<string> _recentChat = new();

        private const int MaxRecentChat = 12;

        public async Task<bool> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(host), port);

                // Asked for explicitly, because the default is whatever the operating system hands out
                // and that is sized for a socket someone is attending to promptly. Thousands of these
                // share one process, so any individual socket can wait a while between drains, and
                // whatever arrives meanwhile has to fit. Measured with the default: at six thousand
                // clients the kernel discarded four percent of everything arriving, including the login
                // responses and arena acknowledgements that clients were sitting waiting for.
                //
                // The operating system silently caps this at its own maximum, so asking for more than it
                // allows looks like it worked. On Linux that is net.core.rmem_max.
                if (receiveBufferSize > 0)
                {
                    try
                    {
                        _socket.ReceiveBufferSize = receiveBufferSize;
                    }
                    catch (Exception ex)
                    {
                        FailureReason ??= $"receive buffer: {ex.Message}";
                    }
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                // Connection init. Sending the key back unchanged is how the server says "no encryption",
                // which is what EncryptionNull does.
                int key = Random.Shared.Next(int.MinValue, int.MaxValue);
                ConnectionInitPacket init = new(key, ClientTypeVIE);
                byte[] initBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref init, 1)).ToArray();

                // Retried, because this is UDP and it is the one packet with nothing behind it to notice
                // it went missing. A real client retries too. Sending it once and waiting meant a single
                // lost datagram cost that client the whole run, which showed up as a steady few percent
                // of clients never connecting, with the server having no record of them at all.
                for (int attempt = 0; attempt < ConnectionInitAttempts; attempt++)
                {
                    await SendRawAsync(initBytes, cancellationToken);

                    using CancellationTokenSource attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptTimeout.CancelAfter(ConnectionInitTimeout);

                    try
                    {
                        while (true)
                        {
                            int received = await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, attemptTimeout.Token);
                            if (received >= 6 && _receiveBuffer[0] == 0x00 && _receiveBuffer[1] == 0x02)
                            {
                                // EncryptionNull answers with the key unchanged, which is the protocol's
                                // way of saying no encryption. EncryptionVIE answers with it negated and
                                // encrypts everything after, which this client cannot do: the login would
                                // reach the server as garbage and be dropped without a word, and thirty
                                // seconds later the server would kick the connection for sending no data.
                                // That cost two hours once, blamed on code, when it was the zone's module
                                // order under a different branch. Say it here, at once.
                                int responseKey = LittleEndianConverter.Convert(MemoryMarshal.Read<int>(_receiveBuffer.AsSpan(2, 4)));
                                if (responseKey != key)
                                {
                                    FailureReason = "the server answered the connection init with a changed key, so an encryption module took this connection; load EncryptionNull before EncryptionVIE";
                                    return false;
                                }

                                IsConnected = true;

                                // The reply is read by the receive loop once the caller starts it; the
                                // kernel holds it until then.
                                await SendTimeSyncAsync(cancellationToken);
                                return true;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // This attempt timed out. Try again, unless that was the last one.
                    }
                }

                // The other likely cause, and worth naming because it is close to impossible to guess:
                // another encryption module sitting ahead of EncryptionNull answers the handshake and
                // then expects encryption this client cannot do.
                FailureReason = $"no connection init response after {ConnectionInitAttempts} attempts; is EncryptionNull loaded before EncryptionVIE?";
                return false;
            }
            catch (OperationCanceledException)
            {
                FailureReason ??= "cancelled during connect";
                return false;
            }
            catch (Exception ex)
            {
                FailureReason = $"connect: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Sends the login packet.
        /// </summary>
        /// <remarks>
        /// Sent reliably, because the server ignores a login that arrives out of the expected sequence.
        /// The reply is not waited for; the caller polls <see cref="IsInArena"/> style state by simply
        /// carrying on, since a load client does not need to react to the login response.
        /// </remarks>
        public Task<bool> LoginAsync(CancellationToken cancellationToken)
        {
            LoginPacket login = default;
            login.Type = (byte)C2SPacketType.Login;
            login.Flags = 0;
            login.Name.Clear();
            login.Password.Clear();

            WriteString(((Span<byte>)login.Name), Name);
            WriteString(((Span<byte>)login.Password), password);

            login.MacId = (uint)Random.Shared.Next(1, int.MaxValue);
            login.ConnectionType = 0;
            login.TimeZoneBias = 0;
            login.CVersion = 134;

            // The server takes a VIE client's features from this field verbatim, so claiming batched
            // positions here is enough to be sent the compact forms without pretending to be Continuum
            // and having to speak its encryption. Only this one is claimed: the others would have the
            // server sending things this client has no way to handle.
            login.ClientFeatures = batchPositions ? ClientFeatures.BatchPositions : ClientFeatures.None;
            login.D2 = 0;

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref login, 1))[..LoginPacket.VIELength];
            return SendReliableAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Requests entry to an arena.
        /// </summary>
        /// <param name="arenaName">The arena to enter.</param>
        /// <param name="viewWidth">Screen width the client claims, in pixels.</param>
        /// <param name="viewHeight">Screen height the client claims, in pixels.</param>
        /// <remarks>
        /// The claimed screen size is what the server's fan out uses as this client's view range, so it
        /// directly sets how many other players it will be sent updates about.
        /// </remarks>
        public Task<bool> GoToArenaAsync(string arenaName, short viewWidth, short viewHeight, CancellationToken cancellationToken)
        {
            const byte ShipWarbird = 0;

            // Unknown from here until the server says, on entry, what it gave this client. Nothing that
            // depends on being in a ship is attempted in between, because a request that arrives while
            // the server is moving the player is refused with a warning about the player's state.
            Volatile.Write(ref _ownShip, -1);
            Volatile.Write(ref _ownFreq, -1);

            C2S_GoArenaVIE go = new(
                shipType: ShipWarbird,
                wavMsg: 0,
                obscenityFilter: 0,
                xRes: viewWidth,
                yRes: viewHeight,
                arenaType: -3, // any public arena, overridden by a name below
                arenaName: arenaName);

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref go, 1));
            return SendReliableAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Sends one position update, unreliably, exactly as a real client does.
        /// </summary>
        /// <param name="weapon">
        /// The weapon being fired this tick, or <see cref="WeaponCodes.Null"/> for none.
        /// Firing changes what the server does with the packet: it can no longer use either of the
        /// small batched forms, so every recipient gets the largest position packet there is.
        /// </param>
        public Task SendPositionAsync(short x, short y, short xSpeed, short ySpeed, sbyte rotation, WeaponCodes weapon, CancellationToken cancellationToken)
        {
            long sent = Interlocked.Increment(ref PositionUpdatesSent);

            // Clocks drift and the first sync can be lost, so it is repeated every thirty seconds at this
            // client's ten hertz. Not awaited: the reply sets the offset whenever it arrives.
            if (sent % 300 == 1)
                _ = SendTimeSyncAsync(cancellationToken);

            C2S_PositionPacket position = new()
            {
                Rotation = rotation,
                Time = ServerTime,
                X = x,
                Y = y,
                XSpeed = xSpeed,
                YSpeed = ySpeed,
                Bounty = 0,
                Energy = 1000,
                Status = 0,
            };

            if (weapon != WeaponCodes.Null)
            {
                position.Weapon.Type = weapon;
                position.Weapon.Level = 1;
            }

            // The server rejects a position packet whose bytes do not exclusive-or to zero, so this is
            // not optional padding. Without it every packet is discarded as malicious and the run looks
            // like the server is doing nothing.
            position.SetChecksum();

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref position, 1))[..C2S_PositionPacket.Length];
            return SendRawAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Reads from the socket until cancelled, acking reliable data and counting what arrives.
        /// </summary>
        /// <remarks>
        /// Acking matters: an unacked reliable packet is retransmitted, so a client that never acks
        /// would make the server look far busier than it is and would eventually be dropped for lag.
        /// </remarks>
        public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[2048];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int received = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                    if (received <= 0)
                        continue;

                    Interlocked.Increment(ref PacketsReceived);
                    Interlocked.Add(ref BytesReceived, received);
                    Volatile.Write(ref LastReceiveTicks, Environment.TickCount64);

                    // Parsed here rather than in a helper, and only awaited when there is actually an ack
                    // to send. Nearly every packet is a position update needing no reply, and calling an
                    // async method for each one cost a state machine and a continuation per packet. With
                    // thousands of clients each receiving hundreds a second, that was enough to make the
                    // load generator, rather than the server, the thing that could not keep up.
                    //
                    // The span does not cross an await, which is what makes it legal here.
                    _pendingAcks.Clear();
                    Parse(buffer.AsSpan(0, received), _pendingAcks);

                    for (int i = 0; i < _pendingAcks.Count; i++)
                    {
                        await SendAckAsync(_pendingAcks[i], cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                FailureReason ??= $"receive: {ex.Message}";
            }
        }

        /// <summary>
        /// Walks a received datagram, counting position updates and noting what needs acking.
        /// </summary>
        /// <param name="span">The packet, which may be grouped or reliable and so contain others.</param>
        /// <param name="acks">Collects the sequence numbers of reliable packets seen.</param>
        private void Parse(ReadOnlySpan<byte> span, List<int> acks)
        {
            if (span.Length < 1)
                return;

            if (span[0] != 0x00)
            {
                CountIfPosition(span);

                if (span[0] == (byte)S2CPacketType.LoginResponse)
                    IsLoggedIn = true;
                else if (span[0] == (byte)S2CPacketType.PlayerEntering)
                    NotePlayerEntering(span);
                else if (span[0] == (byte)S2CPacketType.WhoAmI && span.Length >= 3)
                    NoteArenaEntry(LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(1, 2))));
                else if (span[0] == (byte)S2CPacketType.Kill)
                    Interlocked.Increment(ref KillsSeen);
                else if (span[0] == (byte)S2CPacketType.ShipChange && span.Length >= S2C_ShipChange.Length)
                    NoteShipChange(span);
                else if (span[0] == (byte)S2CPacketType.FreqChange && span.Length >= S2C_FreqChange.Length)
                    NoteFreqChange(span);
                else if (span[0] == (byte)S2CPacketType.FlagPickup)
                    Interlocked.Increment(ref FlagPickupsSeen);
                else if (span[0] == (byte)S2CPacketType.Ball && span.Length >= BallPacket.Length)
                    NoteBall(span);
                else if (span[0] == (byte)S2CPacketType.Chat && span.Length > ChatPacket.HeaderLength)
                    NoteChat(span);

                return;
            }

            if (span.Length < 2)
                return;

            switch (span[1])
            {
                case 0x03: // reliable data, which has to be acked or the server keeps resending it
                    if (span.Length >= ReliableHeader.Length)
                    {
                        acks.Add(LittleEndianConverter.Convert(MemoryMarshal.Read<int>(span.Slice(2, 4))));

                        // The payload is an ordinary packet.
                        if (span.Length > ReliableHeader.Length)
                            Parse(span[ReliableHeader.Length..], acks);
                    }
                    break;

                case 0x0E: // grouped: a length prefixed sequence of ordinary packets
                    int offset = 2;
                    while (offset < span.Length)
                    {
                        int length = span[offset];
                        if (length == 0 || offset + 1 + length > span.Length)
                            break;

                        Parse(span.Slice(offset + 1, length), acks);
                        offset += 1 + length;
                    }
                    break;

                case 0x04: // the server acknowledging something sent reliably
                    if (span.Length >= AckPacket.Length)
                    {
                        int acked = LittleEndianConverter.Convert(MemoryMarshal.Read<int>(span.Slice(2, 4)));

                        // Highest seen, since acknowledgements can arrive out of order and the senders
                        // only care whether theirs has gone past.
                        long previous;
                        while ((previous = Volatile.Read(ref _highestAckedSequenceNumber)) < acked)
                        {
                            if (Interlocked.CompareExchange(ref _highestAckedSequenceNumber, acked, previous) == previous)
                                break;
                        }
                    }
                    break;

                case 0x06: // the server's answer to a time sync: our time when we asked, its time when it answered
                    if (span.Length >= TimeSyncResponse.Length)
                    {
                        uint clientTime = LittleEndianConverter.Convert(MemoryMarshal.Read<uint>(span.Slice(2, 4)));
                        uint serverTime = LittleEndianConverter.Convert(MemoryMarshal.Read<uint>(span.Slice(6, 4)));

                        // The server's time when it answered against ours when we asked, so this is off by
                        // one trip one way. A tick is ten milliseconds and the trip is a few, so adding
                        // half the round trip is not worth the arithmetic.
                        Volatile.Write(ref _serverTickOffset, serverTime - clientTime);
                    }
                    break;

                case 0x07: // the server dropped us
                    FailureReason ??= "disconnected by server";
                    break;
            }
        }

        /// <summary>
        /// Asks the server what time it is, so that positions can be stamped on its clock.
        /// </summary>
        private Task SendTimeSyncAsync(CancellationToken cancellationToken)
        {
            // The counts are every datagram sent and received, which is what the server compares its own
            // per-connection counts against for packet loss. Reporting only the positions sent made the
            // server see loss that was not there, and it specced six hundred of a thousand clients for it.
            TimeSyncRequest request = new(ServerTick.Now, (uint)Interlocked.Read(ref PacketsSent), (uint)Interlocked.Read(ref PacketsReceived));
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref request, 1));
            return SendRawAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Tells the server this client died, naming another player as the killer.
        /// </summary>
        /// <remarks>
        /// Drives the kill path: the kill advisors, scoring, the green (prize) drop, and the respawn,
        /// none of which a position packet touches. Does nothing until at least one other player has
        /// been seen, since the server rejects a killer it cannot resolve in this arena.
        /// </remarks>
        /// <returns><see langword="true"/> if a death was sent.</returns>
        public Task<bool> SendDeathAsync(CancellationToken cancellationToken)
        {
            short killer;

            lock (_knownPlayerIds)
            {
                if (_knownPlayerIds.Count == 0)
                    return Task.FromResult(false);

                killer = _knownPlayerIds[Random.Shared.Next(_knownPlayerIds.Count)];
            }

            C2S_Die die = new(killer, 0);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref die, 1));
            return SendRawAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Asks for a different ship, sent reliably as a real client does.
        /// </summary>
        /// <returns><see langword="true"/> if the server acknowledged receiving it.</returns>
        public Task<bool> SendShipChangeAsync(sbyte ship, CancellationToken cancellationToken)
        {
            return SendReliableAsync([(byte)C2SPacketType.SetShip, (byte)ship], cancellationToken);
        }

        /// <summary>
        /// Asks for a different freq, sent reliably as a real client does.
        /// </summary>
        /// <returns><see langword="true"/> if the server acknowledged receiving it.</returns>
        public Task<bool> SendFreqChangeAsync(short freq, CancellationToken cancellationToken)
        {
            C2S_SetFreq packet = new(freq);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));
            return SendReliableAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Touches a flag by id, sent reliably. Whether it is on the map to be touched is the server's call.
        /// </summary>
        /// <returns><see langword="true"/> if the server acknowledged receiving it.</returns>
        public Task<bool> SendTouchFlagAsync(short flagId, CancellationToken cancellationToken)
        {
            return SendReliableAsync([(byte)C2SPacketType.TouchFlag, (byte)flagId, (byte)(flagId >> 8)], cancellationToken);
        }

        /// <summary>
        /// Drops every flag carried. The server does nothing if none is.
        /// </summary>
        public Task SendDropFlagsAsync(CancellationToken cancellationToken)
        {
            return SendRawAsync([(byte)C2SPacketType.DropFlags], cancellationToken);
        }

        /// <summary>
        /// Finds a ball the server last reported loose, and the fire time a pickup of it has to quote.
        /// </summary>
        public bool TryGetLooseBall(out byte ballId, out uint time)
        {
            lock (_balls)
            {
                for (int i = 0; i < _balls.Length; i++)
                {
                    if (_balls[i].Seen && _balls[i].CarrierId < 0)
                    {
                        ballId = (byte)i;
                        time = _balls[i].Time;
                        return true;
                    }
                }
            }

            ballId = 0;
            time = 0;
            return false;
        }

        /// <summary>
        /// Whether the server's last update of a ball said this client carries it.
        /// </summary>
        public bool IsCarryingBall(byte ballId)
        {
            lock (_balls)
            {
                return ballId < _balls.Length && _balls[ballId].Seen && _balls[ballId].CarrierId == _ownPlayerId;
            }
        }

        /// <summary>
        /// Asks to pick up a ball, sent reliably.
        /// </summary>
        /// <param name="ballId">The ball.</param>
        /// <param name="time">The ball's fire time from the server's last update of it, which the server checks.</param>
        /// <param name="cancellationToken">To stop waiting.</param>
        /// <returns><see langword="true"/> if the server acknowledged receiving it.</returns>
        public Task<bool> SendPickupBallAsync(byte ballId, uint time, CancellationToken cancellationToken)
        {
            C2S_PickupBall packet = new(ballId, time);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));
            return SendReliableAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Shoots a carried ball, leaving it where this client is for the next client to pick up.
        /// </summary>
        public Task SendShootBallAsync(byte ballId, short x, short y, CancellationToken cancellationToken)
        {
            BallPacket packet = new(isS2C: false, ballId, x, y, xSpeed: 0, ySpeed: 0, playerId: _ownPlayerId, time: ServerTime);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));
            return SendRawAsync(bytes.ToArray(), cancellationToken);
        }

        /// <summary>
        /// Notes an arena entry, which is where the server says who this client is now. What was learned
        /// about the previous arena is forgotten: its player ids name nobody here, and its balls are not
        /// these.
        /// </summary>
        private void NoteArenaEntry(short playerId)
        {
            _ownPlayerId = playerId;
            Volatile.Write(ref _ownShip, -1);
            Volatile.Write(ref _ownFreq, -1);
            Interlocked.Increment(ref ArenaEntriesSeen);

            lock (_knownPlayerIds)
            {
                _knownPlayerIds.Clear();
            }

            lock (_balls)
            {
                Array.Clear(_balls);
            }
        }

        private void NoteShipChange(ReadOnlySpan<byte> span)
        {
            Interlocked.Increment(ref ShipChangesSeen);

            // Type, ship, player id, freq.
            short playerId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(2, 2)));
            if (playerId == _ownPlayerId)
            {
                Volatile.Write(ref _ownShip, (sbyte)span[1]);
                Volatile.Write(ref _ownFreq, LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(4, 2))));
            }
        }

        private void NoteFreqChange(ReadOnlySpan<byte> span)
        {
            Interlocked.Increment(ref FreqChangesSeen);

            // Type, player id, freq.
            short playerId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(1, 2)));
            if (playerId == _ownPlayerId)
                Volatile.Write(ref _ownFreq, LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(3, 2))));
        }

        /// <summary>
        /// The chat messages received so far, by text, with how many times each arrived.
        /// </summary>
        public KeyValuePair<string, long>[] GetChatMessages()
        {
            lock (_chatMessages)
            {
                return [.. _chatMessages];
            }
        }

        private void NoteChat(ReadOnlySpan<byte> span)
        {
            ReadOnlySpan<byte> messageBytes = ChatPacket.GetMessageBytes(span);
            int end = messageBytes.IndexOf((byte)0);
            if (end >= 0)
                messageBytes = messageBytes[..end];

            if (messageBytes.IsEmpty)
                return;

            // Allocates, which is fine here: chat is a message every so often, not a packet every tick.
            // Digits are replaced so that "Frequency 100 is not available" and "Frequency 101 is not
            // available" count as one message, and so that a message naming a player does not take up
            // one of the distinct texts per player.
            string message = string.Create(messageBytes.Length, messageBytes, static (chars, bytes) =>
            {
                for (int i = 0; i < bytes.Length; i++)
                    chars[i] = bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9' ? '#' : (char)bytes[i];
            });

            lock (_chatMessages)
            {
                if (_chatMessages.TryGetValue(message, out long count))
                    _chatMessages[message] = count + 1;
                else if (_chatMessages.Count < MaxDistinctChatMessages)
                    _chatMessages[message] = 1;
                else
                    _chatMessages["(other messages)"] = _chatMessages.GetValueOrDefault("(other messages)") + 1;

                _recentChat.Enqueue(Encoding.ASCII.GetString(messageBytes));
                while (_recentChat.Count > MaxRecentChat)
                    _recentChat.Dequeue();
            }
        }

        /// <summary>
        /// The last few chat messages exactly as received, oldest first.
        /// </summary>
        public string[] GetRecentChat()
        {
            lock (_chatMessages)
            {
                return [.. _recentChat];
            }
        }

        /// <summary>
        /// Says something in public chat, which is also how a command is issued, sent reliably as a real
        /// client does.
        /// </summary>
        public Task<bool> SendChatAsync(string message, CancellationToken cancellationToken)
        {
            const byte ChatTypePublic = 2;

            byte[] packet = new byte[ChatPacket.HeaderLength + ChatPacket.MaxMessageBytes];
            ChatPacket header = new((byte)C2SPacketType.Chat, ChatTypePublic, 0, 0);
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1)).CopyTo(packet);
            int length = ChatPacket.SetMessage(packet, message);
            return SendReliableAsync(packet[..length], cancellationToken);
        }

        private void NoteBall(ReadOnlySpan<byte> span)
        {
            Interlocked.Increment(ref BallUpdatesSeen);

            // Type, ball id, x, y, x speed, y speed, the carrier's player id or -1, fire time.
            byte ballId = span[1];
            if (ballId >= _balls.Length)
                return;

            short carrierId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(10, 2)));
            uint time = LittleEndianConverter.Convert(MemoryMarshal.Read<uint>(span.Slice(12, 4)));

            lock (_balls)
            {
                _balls[ballId] = new BallInfo { Seen = true, CarrierId = carrierId, Time = time };
            }
        }

        /// <summary>
        /// Records another player's id from a player entering packet, so that a death can name a killer
        /// the server will accept.
        /// </summary>
        private void NotePlayerEntering(ReadOnlySpan<byte> span)
        {
            // Offset of the player id within S2C_PlayerData: type, ship and audio flag, then the 20 byte
            // name, the 20 byte squad, and two four byte score fields.
            const int PlayerIdOffset = 51;

            if (span.Length < PlayerIdOffset + 4)
                return;

            short playerId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(PlayerIdOffset, 2)));
            if (playerId < 0)
                return;

            if (playerId == _ownPlayerId)
            {
                // The entry for this client itself is where the server says which ship and freq it was
                // given: the ship is the byte after the type, and the freq follows the player id.
                Volatile.Write(ref _ownShip, (sbyte)span[1]);
                Volatile.Write(ref _ownFreq, LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(PlayerIdOffset + 2, 2))));
                return;
            }

            lock (_knownPlayerIds)
            {
                if (_knownPlayerIds.Count < 64 && !_knownPlayerIds.Contains(playerId))
                    _knownPlayerIds.Add(playerId);
            }
        }

        /// <summary>
        /// Counts the position updates in a packet, which is not always one.
        /// </summary>
        /// <remarks>
        /// The four shapes a position update can arrive in. Which one the server picks depends on the
        /// player's id, whether a weapon was fired, and what the client said it supports.
        /// <para>
        /// The two batched forms carry a packet type byte followed by <em>one or more</em> fixed size
        /// position items: the send thread merges consecutive ones for the same recipient into a single
        /// packet. Counting those as one update each undercounted by roughly a factor of five, which made
        /// the server look like it was sending far fewer, far larger updates than it was.
        /// </para>
        /// </remarks>
        /// <param name="span">The packet, starting at its type byte.</param>
        private void CountIfPosition(ReadOnlySpan<byte> span)
        {
            byte packetType = span[0];

            if (packetType == (byte)S2CPacketType.Position
                || packetType == (byte)S2CPacketType.Weapon)
            {
                Interlocked.Increment(ref PositionUpdatesReceived);

                // The time the server stamped the position with: sixteen bits of its clock, in
                // centiseconds, after the type and rotation bytes.
                if (packetType == (byte)S2CPacketType.Position && span.Length >= S2C_PositionPacket.Length)
                    NotePositionAge(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2)), 0xFFFF);
            }
            else if (packetType == (byte)S2CPacketType.BatchedSmallPosition)
            {
                int count = (span.Length - 1) / SmallPosition.Length;
                Interlocked.Add(ref PositionUpdatesReceived, count);

                // Ten bits of the clock per position here, the low bits of the item's first bit field,
                // which follows its player id; so the age wraps at ten seconds. The first in the batch
                // is enough, since they were sent together.
                if (count > 0)
                    NotePositionAge((ushort)(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2)) & 0x3FF), 0x3FF);
            }
            else if (packetType == (byte)S2CPacketType.BatchedLargePosition)
            {
                Interlocked.Add(ref PositionUpdatesReceived, (span.Length - 1) / LargePosition.Length);
            }
        }

        /// <summary>
        /// Records how long ago, by the server's clock, a position just received was stamped, keeping
        /// the largest for the stall watcher. The client's view of the server's clock comes from the
        /// time sync and runs late by about a round trip, so an age here is never negative; a value
        /// near the wrap is one anyway, from a stamp ahead of that view, and is ignored.
        /// </summary>
        private void NotePositionAge(ushort stampedCs, int mask)
        {
            long ageCs = ((uint)ServerTime - stampedCs) & mask;
            if (ageCs > mask - (mask >> 4))
                return;

            // A stamp the server made from a sender whose own clock was wrong, which a client just
            // in and not yet synced can be, reads as minutes of age here. No socket on this machine
            // holds thirty seconds of a client's traffic, so nothing above that is a backlog.
            if (ageCs > 3000)
                return;

            if (ageCs > MaxPositionAgeCs)
                MaxPositionAgeCs = ageCs;
        }

        private Task SendAckAsync(int sequenceNumber, CancellationToken cancellationToken)
        {
            AckPacket ack = new(sequenceNumber);
            return SendRawAsync(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ack, 1)).ToArray(), cancellationToken);
        }

        /// <summary>
        /// Sends a packet reliably, resending until the server acknowledges it.
        /// </summary>
        /// <remarks>
        /// Resending is what makes it reliable, and this used to send once and hope. The two packets sent
        /// this way, the login and the arena request, are the two with nothing behind them to notice they
        /// went missing: lose either and the client sits waiting for a reply to something the server
        /// never received. Measured over two thousand clients, twenty one lost their login and nineteen
        /// their arena request, and every one of them was simply absent from the run afterwards.
        /// </remarks>
        /// <param name="payload">The packet to send, without the reliable header.</param>
        /// <param name="cancellationToken">To stop waiting.</param>
        /// <returns><see langword="true"/> if it was acknowledged.</returns>
        private async Task<bool> SendReliableAsync(byte[] payload, CancellationToken cancellationToken)
        {
            int sequenceNumber = Interlocked.Increment(ref _outgoingSequenceNumber) - 1;

            byte[] buffer = new byte[ReliableHeader.Length + payload.Length];
            ReliableHeader header = new(sequenceNumber);
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1)).CopyTo(buffer);
            payload.CopyTo(buffer, ReliableHeader.Length);

            for (int attempt = 0; attempt < ReliableSendAttempts; attempt++)
            {
                if (!await SendRawAsync(buffer, cancellationToken))
                    return false;

                // The receive loop records acknowledgements as they arrive, so this only has to watch for
                // this one going past.
                long deadline = Environment.TickCount64 + (long)ReliableSendTimeout.TotalMilliseconds;

                while (Environment.TickCount64 < deadline)
                {
                    if (Volatile.Read(ref _highestAckedSequenceNumber) >= sequenceNumber)
                        return true;

                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    try
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            FailureReason ??= $"no acknowledgement for reliable packet {sequenceNumber} after {ReliableSendAttempts} attempts";
            return false;
        }

        private async Task<bool> SendRawAsync(byte[] data, CancellationToken cancellationToken)
        {
            if (_serverEndPoint is null)
                return false;

            try
            {
                await _socket.SendToAsync(data, SocketFlags.None, _serverEndPoint, cancellationToken);
                Interlocked.Increment(ref PacketsSent);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                FailureReason ??= $"send: {ex.Message}";
                return false;
            }
        }

        private static void WriteString(Span<byte> destination, string value)
        {
            destination.Clear();

            int length = Math.Min(value.Length, destination.Length - 1);
            for (int i = 0; i < length; i++)
                destination[i] = (byte)value[i];
        }

        /// <summary>
        /// Tells the server we are leaving, so that it tears the connection down immediately rather than
        /// waiting for it to time out. Without this a run leaves the server carrying its players for a
        /// while afterwards, which would pollute the next measurement.
        /// </summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                await SendRawAsync([0x00, 0x07], cancellationToken);
            }
            catch
            {
                // Going away regardless.
            }
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}
