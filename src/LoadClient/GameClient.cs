using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
    internal sealed class GameClient(string name, string password) : IDisposable
    {
        private const byte ClientTypeVIE = 0x01;

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

        public string Name { get; } = name;

        public bool IsConnected { get; private set; }

        public string? FailureReason { get; private set; }

        /// <summary>
        /// Position updates received from the server about other players.
        /// </summary>
        /// <remarks>
        /// This is the server's send side load, which fake players cannot produce because the fan out
        /// skips them. Summed across clients it says how much the server is actually pushing out.
        /// </remarks>
        public long PositionUpdatesReceived;

        public long PacketsReceived;
        public long BytesReceived;

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

        public async Task<bool> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                // Connection init. Sending the key back unchanged is how the server says "no encryption",
                // which is what EncryptionNull does.
                int key = Random.Shared.Next(int.MinValue, int.MaxValue);
                ConnectionInitPacket init = new(key, ClientTypeVIE);
                await SendRawAsync(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref init, 1)).ToArray(), cancellationToken);

                // Wait for the response, ignoring anything else that turns up first.
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    while (true)
                    {
                        int received = await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, timeout.Token);
                        if (received >= 6 && _receiveBuffer[0] == 0x00 && _receiveBuffer[1] == 0x02)
                        {
                            IsConnected = true;
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Our own timeout, rather than the run ending. Worth saying so specifically: by far
                    // the most likely cause is another encryption module sitting ahead of EncryptionNull,
                    // which answers the handshake and then expects encryption this client cannot do.
                    // Reported as a plain cancellation, that is close to impossible to guess.
                    FailureReason = "no connection init response; is EncryptionNull loaded before EncryptionVIE?";
                    return false;
                }
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
            login.ClientFeatures = ClientFeatures.None;
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
            C2S_PositionPacket position = new()
            {
                Rotation = rotation,
                Time = ServerTick.Now,
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
                CountIfPosition(span[0]);

                if (span[0] == (byte)S2CPacketType.PlayerEntering)
                    NotePlayerEntering(span);
                else if (span[0] == (byte)S2CPacketType.WhoAmI && span.Length >= 3)
                    _ownPlayerId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(1, 2)));
                else if (span[0] == (byte)S2CPacketType.Kill)
                    Interlocked.Increment(ref KillsSeen);

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

                case 0x07: // the server dropped us
                    FailureReason ??= "disconnected by server";
                    break;
            }
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
        /// Records another player's id from a player entering packet, so that a death can name a killer
        /// the server will accept.
        /// </summary>
        private void NotePlayerEntering(ReadOnlySpan<byte> span)
        {
            // Offset of the player id within S2C_PlayerData: type, ship and audio flag, then the 20 byte
            // name, the 20 byte squad, and two four byte score fields.
            const int PlayerIdOffset = 51;

            if (span.Length < PlayerIdOffset + 2)
                return;

            short playerId = LittleEndianConverter.Convert(MemoryMarshal.Read<short>(span.Slice(PlayerIdOffset, 2)));
            if (playerId < 0 || playerId == _ownPlayerId)
                return;

            lock (_knownPlayerIds)
            {
                if (_knownPlayerIds.Count < 64 && !_knownPlayerIds.Contains(playerId))
                    _knownPlayerIds.Add(playerId);
            }
        }

        private void CountIfPosition(byte packetType)
        {
            // The four shapes a position update can arrive in. Which one the server picks depends on
            // the player's id, whether a weapon was fired, and what the client said it supports.
            if (packetType == (byte)S2CPacketType.Position
                || packetType == (byte)S2CPacketType.Weapon
                || packetType == (byte)S2CPacketType.BatchedSmallPosition
                || packetType == (byte)S2CPacketType.BatchedLargePosition)
            {
                Interlocked.Increment(ref PositionUpdatesReceived);
            }
        }

        private Task SendAckAsync(int sequenceNumber, CancellationToken cancellationToken)
        {
            AckPacket ack = new(sequenceNumber);
            return SendRawAsync(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ack, 1)).ToArray(), cancellationToken);
        }

        private async Task<bool> SendReliableAsync(byte[] payload, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[ReliableHeader.Length + payload.Length];

            ReliableHeader header = new(Interlocked.Increment(ref _outgoingSequenceNumber) - 1);
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1)).CopyTo(buffer);
            payload.CopyTo(buffer, ReliableHeader.Length);

            return await SendRawAsync(buffer, cancellationToken);
        }

        private async Task<bool> SendRawAsync(byte[] data, CancellationToken cancellationToken)
        {
            if (_serverEndPoint is null)
                return false;

            try
            {
                await _socket.SendToAsync(data, SocketFlags.None, _serverEndPoint, cancellationToken);
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
