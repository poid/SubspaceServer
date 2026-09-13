using SS.Packets.Game;
using System.Diagnostics;
using System.Text;

namespace SS.LoadClient
{
    /// <summary>
    /// A load generator for the Subspace server, for measuring the effect of performance work.
    /// </summary>
    /// <remarks>
    /// The server has no automated performance tests, so changes to hot paths can only be judged by
    /// putting load on a running server and reading its counters (the Metrics module, ?metrics).
    /// This produces that load.
    /// </remarks>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // The packet structs encode strings as Windows-1252, which .NET does not carry by default.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Options? options = Options.Parse(args);
            if (options is null)
            {
                Options.PrintUsage();
                return 1;
            }

            using CancellationTokenSource cancellationTokenSource = new();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Stopping...");
                cancellationTokenSource.Cancel();
            };

            if (options.Duration > TimeSpan.Zero)
                cancellationTokenSource.CancelAfter(options.Duration);

            return options.Protocol == LoadProtocol.Game
                ? await RunGameAsync(options, cancellationTokenSource.Token)
                : await RunAsync(options, cancellationTokenSource.Token);
        }

        /// <summary>
        /// Runs simulated game clients, which send position packets and receive updates.
        /// </summary>
        /// <remarks>
        /// Unlike the chat run, this loads the position fan out and the network send path, and it can
        /// measure the server's send side: how many position updates it pushed back out, which is the
        /// half of the cost that fake players cannot produce.
        /// </remarks>
        private static async Task<int> RunGameAsync(Options options, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Connecting {options.ClientCount} game client(s) to {options.Host}:{options.Port} over UDP, arenas: {string.Join(", ", options.Arenas)}");
            Console.WriteLine($"Spread {options.SpreadPixels}px, view {options.ViewWidth}x{options.ViewHeight}. The server needs the EncryptionNull module loaded.");

            GameClient[] clients = new GameClient[options.ClientCount];

            // Indexed per client rather than appended to a shared list, because every client is set up
            // concurrently and List is not safe to add to from several threads at once.
            Task?[] receiveTasks = new Task?[options.ClientCount];
            Task?[] positionTasks = new Task?[options.ClientCount];

            int connected = 0;
            long startTimestamp = Stopwatch.GetTimestamp();

            Task?[] setupTasks = new Task?[options.ClientCount];

            for (int i = 0; i < options.ClientCount; i++)
            {
                int index = i;
                GameClient client = new($"{options.NamePrefix}{index:D4}", options.Password, options.BatchPositions, options.ReceiveBufferSize);
                clients[index] = client;

                setupTasks[index] = SetupAsync(client, index);

                if (options.ConnectDelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(options.ConnectDelayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            try
            {
                // Interrupting the staggered start leaves the rest of the array unset, and WhenAll
                // throws on a null element, so only the entries that were filled are awaited.
                await Task.WhenAll(setupTasks.Where(t => t is not null)!);
            }
            catch (OperationCanceledException)
            {
                // Expected if the run was cut short while clients were still arriving.
            }

            // Same reason: only the clients that were actually constructed.
            GameClient[] startedClients = clients.Where(c => c is not null).ToArray()!;

            Console.WriteLine($"Connected {connected}/{options.ClientCount} in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:F1}s");

            if (connected == 0)
            {
                Console.WriteLine("No client connected. First failure: " + (Array.Find(startedClients, c => c.FailureReason is not null)?.FailureReason ?? "unknown"));
                Console.WriteLine("Check that EncryptionNull is loaded, and loaded before EncryptionVIE.");
                return 1;
            }

            long measureStart = Stopwatch.GetTimestamp();

            // Counters are taken as deltas across the hold, not as totals. Clients that connected early
            // have been sending and receiving throughout the connect ramp, which for thousands of them
            // takes longer than a minute. Dividing their whole life's counters by the hold alone made
            // every per second figure here too high, in proportion to how long the ramp was.
            long[] sentBaseline = new long[startedClients.Length];
            long[] receivedBaseline = new long[startedClients.Length];
            long[] bytesBaseline = new long[startedClients.Length];
            long[] killsBaseline = new long[startedClients.Length];
            long[] shipChangesBaseline = new long[startedClients.Length];
            long[] freqChangesBaseline = new long[startedClients.Length];
            long[] flagPickupsBaseline = new long[startedClients.Length];
            long[] ballUpdatesBaseline = new long[startedClients.Length];
            long[] arenaEntriesBaseline = new long[startedClients.Length];

            for (int i = 0; i < startedClients.Length; i++)
            {
                sentBaseline[i] = Interlocked.Read(ref startedClients[i].PositionUpdatesSent);
                receivedBaseline[i] = Interlocked.Read(ref startedClients[i].PositionUpdatesReceived);
                bytesBaseline[i] = Interlocked.Read(ref startedClients[i].BytesReceived);
                killsBaseline[i] = Interlocked.Read(ref startedClients[i].KillsSeen);
                shipChangesBaseline[i] = Interlocked.Read(ref startedClients[i].ShipChangesSeen);
                freqChangesBaseline[i] = Interlocked.Read(ref startedClients[i].FreqChangesSeen);
                flagPickupsBaseline[i] = Interlocked.Read(ref startedClients[i].FlagPickupsSeen);
                ballUpdatesBaseline[i] = Interlocked.Read(ref startedClients[i].BallUpdatesSeen);
                arenaEntriesBaseline[i] = Interlocked.Read(ref startedClients[i].ArenaEntriesSeen);
            }

            Console.WriteLine(options.Duration > TimeSpan.Zero
                ? $"Holding for {options.Duration.TotalSeconds:F0}s at 10Hz. Read the server's ?metrics output now."
                : "Holding until interrupted (Ctrl+C) at 10Hz. Read the server's ?metrics output now.");

            try
            {
                await Task.WhenAll(receiveTasks.Concat(positionTasks).Where(t => t is not null)!);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            double seconds = Stopwatch.GetElapsedTime(measureStart).TotalSeconds;

            long positionUpdates = 0;
            long positionUpdatesSent = 0;
            long bytes = 0;
            long kills = 0;
            long shipChanges = 0;
            long freqChanges = 0;
            long flagPickups = 0;
            long ballUpdates = 0;
            long arenaEntries = 0;
            int stillUp = 0;

            for (int i = 0; i < startedClients.Length; i++)
            {
                GameClient client = startedClients[i];

                positionUpdates += Interlocked.Read(ref client.PositionUpdatesReceived) - receivedBaseline[i];
                positionUpdatesSent += Interlocked.Read(ref client.PositionUpdatesSent) - sentBaseline[i];
                bytes += Interlocked.Read(ref client.BytesReceived) - bytesBaseline[i];
                kills += Interlocked.Read(ref client.KillsSeen) - killsBaseline[i];
                shipChanges += Interlocked.Read(ref client.ShipChangesSeen) - shipChangesBaseline[i];
                freqChanges += Interlocked.Read(ref client.FreqChangesSeen) - freqChangesBaseline[i];
                flagPickups += Interlocked.Read(ref client.FlagPickupsSeen) - flagPickupsBaseline[i];
                ballUpdates += Interlocked.Read(ref client.BallUpdatesSeen) - ballUpdatesBaseline[i];
                arenaEntries += Interlocked.Read(ref client.ArenaEntriesSeen) - arenaEntriesBaseline[i];

                if (client.IsConnected && client.FailureReason is null)
                    stillUp++;
            }

            Console.WriteLine($"Finished. {stillUp}/{connected} client(s) survived.");
            Console.WriteLine($"Server sent {positionUpdates / seconds:F0} position updates/s and {bytes / seconds / 1024d:F0} KB/s to these clients ({positionUpdates / (double)Math.Max(connected, 1) / seconds:F1} updates/s each).");

            // Reported so that a shortfall in what the server received can be told apart from a shortfall
            // in what was sent to it. Compare this against the server's own position packet rate: if they
            // agree, this generator is the thing falling short, not the server.
            Console.WriteLine($"These clients sent {positionUpdatesSent / seconds:F0} position updates/s ({positionUpdatesSent / (double)Math.Max(stillUp, 1) / seconds:F1} each, against a nominal 10.0).");

            if (options.DeathsPerMinute > 0)
                Console.WriteLine($"Kill notifications seen: {kills / seconds:F0}/s, which confirms deaths were processed rather than rejected.");

            // Each of these is something the server told every client in the arena, so it is counted once
            // per client that saw it. They confirm the server processed the requests rather than refusing
            // them: a refusal is a warning in the server log and nothing here.
            if (options.ShipChangesPerMinute > 0)
                Console.WriteLine($"Ship change notifications seen: {shipChanges / seconds:F0}/s.");

            if (options.FreqChangesPerMinute > 0)
                Console.WriteLine($"Freq change notifications seen: {freqChanges / seconds:F0}/s.");

            if (options.FlagsPerMinute > 0)
                Console.WriteLine($"Flag pickup notifications seen: {flagPickups / seconds:F0}/s.");

            if (options.BallsPerMinute > 0)
                Console.WriteLine($"Ball updates seen: {ballUpdates / seconds:F0}/s.");

            if (options.ArenaSwitchesPerMinute > 0)
                Console.WriteLine($"Arena entries: {arenaEntries / seconds:F1}/s, each one a completed switch.");

            await Task.WhenAll(startedClients.Select(c => c.DisconnectAsync(CancellationToken.None)));

            foreach (GameClient client in startedClients)
                client.Dispose();

            return 0;

            async Task SetupAsync(GameClient client, int index)
            {
                if (!await client.ConnectAsync(options.Host, options.Port, cancellationToken))
                    return;

                Interlocked.Increment(ref connected);

                receiveTasks[index] = client.ReceiveLoopAsync(cancellationToken);

                // Both of these are sent reliably and now report whether the server acknowledged them, so
                // a client that lost one stops here with a reason rather than waiting for a reply to
                // something that never arrived.
                if (!await client.LoginAsync(cancellationToken))
                    return;

                // Wait for the server to actually answer the login before asking for an arena, rather
                // than guessing how long it will take. A request that arrives first is thrown away,
                // and the client is then connected and logged in but never in an arena, which is
                // invisible from this end and quietly understates every measurement.
                //
                // This was a fixed 1.5 second wait, which was fine for hundreds of clients and not for
                // thousands: the server advances logins on a ten hertz state machine, so the busier it
                // is the longer a login takes, exactly when the measurement matters most.
                if (!await WaitUntilAsync(() => client.IsLoggedIn, TimeSpan.FromSeconds(60), cancellationToken))
                    return;

                string arena = options.Arenas[index % options.Arenas.Length];
                if (!await client.GoToArenaAsync(arena, options.ViewWidth, options.ViewHeight, cancellationToken))
                    return;

                // Likewise, positions sent before the arena is entered are discarded.
                if (!await WaitUntilAsync(() => client.IsInArena, TimeSpan.FromSeconds(60), cancellationToken))
                    return;

                positionTasks[index] = PositionLoopAsync(client, index, cancellationToken);
            }

            /// <summary>
            /// Waits for something to become true, or gives up.
            /// </summary>
            /// <remarks>
            /// Polled rather than signalled because the states being waited on are set while parsing a
            /// received packet, and that path is hot enough that it should not be publishing events.
            /// </remarks>
            static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token)
            {
                long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

                while (!condition())
                {
                    if (Environment.TickCount64 >= deadline || token.IsCancellationRequested)
                        return false;

                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }

                return true;
            }

            async Task PositionLoopAsync(GameClient client, int index, CancellationToken token)
            {
                // Spread the clients out, then have each fly a slow circle. Movement matters: a
                // stationary population would sit in whatever pattern it started in, and how many
                // clients are within view of each other is exactly what the fan out cost depends on.
                int columns = (int)Math.Ceiling(Math.Sqrt(options.ClientCount));
                int spacing = Math.Max(1, options.SpreadPixels / Math.Max(1, columns));

                int originX = 8192 - (options.SpreadPixels / 2) + (index % columns * spacing);
                int originY = 8192 - (options.SpreadPixels / 2) + (index / columns * spacing);

                const int Radius = 200;
                sbyte rotation = 0;
                double angle = index;
                int tick = 0;

                // Deaths per minute, converted to a probability per 10Hz tick. The rest likewise.
                double deathChancePerTick = options.DeathsPerMinute / 60d / 10d;
                double shipChangeChancePerTick = options.ShipChangesPerMinute / 60d / 10d;
                double freqChangeChancePerTick = options.FreqChangesPerMinute / 60d / 10d;
                double arenaSwitchChancePerTick = options.Arenas.Length > 1 ? options.ArenaSwitchesPerMinute / 60d / 10d : 0d;
                double ballChancePerTick = options.BallsPerMinute / 60d / 10d;
                double flagChancePerTick = options.FlagsPerMinute / 60d / 10d;

                int currentArena = index % options.Arenas.Length;

                // A ball pickup and a flag touch are each followed by their release a tick later, so that
                // the server sees both halves and the ball or flag is there for the next client.
                int ballToShoot = -1;
                bool flagToDrop = false;

                try
                {
                    // 10Hz, matching a real client's SendPositionDelay of 10.
                    using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));

                    while (await timer.WaitForNextTickAsync(token))
                    {
                        angle += 0.1d;
                        tick++;

                        short x = (short)Math.Clamp(originX + (Radius * Math.Cos(angle)), 0, 16383);
                        short y = (short)Math.Clamp(originY + (Radius * Math.Sin(angle)), 0, 16383);

                        rotation = (sbyte)((rotation + 1) % 40);

                        // Bullets and bombs, mixed, since they take different paths through the weapon
                        // range logic. Spread across clients by index so they do not all fire together.
                        WeaponCodes weapon = WeaponCodes.Null;
                        if (options.WeaponPercent > 0 && (tick + index) % 100 < options.WeaponPercent)
                            weapon = (tick + index) % 5 == 0 ? WeaponCodes.Bomb : WeaponCodes.Bullet;

                        await client.SendPositionAsync(x, y, 0, 0, rotation, weapon, token);

                        if (deathChancePerTick > 0d && Random.Shared.NextDouble() < deathChancePerTick)
                            await client.SendDeathAsync(token);

                        // At most one of these a tick. The server refuses a ship or freq change until the
                        // client has acked the previous one, and an arena switch makes both moot.
                        if (arenaSwitchChancePerTick > 0d && Random.Shared.NextDouble() < arenaSwitchChancePerTick)
                        {
                            currentArena = (currentArena + 1 + Random.Shared.Next(options.Arenas.Length - 1)) % options.Arenas.Length;
                            ballToShoot = -1;
                            flagToDrop = false;
                            await client.GoToArenaAsync(options.Arenas[currentArena], options.ViewWidth, options.ViewHeight, token);
                        }
                        else if (shipChangeChancePerTick > 0d && Random.Shared.NextDouble() < shipChangeChancePerTick)
                        {
                            // A different ship from the current one, and never spectator mode, which would
                            // take the client out of the fan out and have it refused flags and balls.
                            int ship = client.OwnShip;
                            if (ship >= 0)
                                await client.SendShipChangeAsync((sbyte)((ship + 1 + Random.Shared.Next(7)) % 8), token);
                        }
                        else if (freqChangeChancePerTick > 0d && Random.Shared.NextDouble() < freqChangeChancePerTick)
                        {
                            int freq = client.OwnFreq;
                            if (freq >= 0)
                                await client.SendFreqChangeAsync((short)(freq == 0 ? 1 : 0), token);
                        }

                        if (ballToShoot >= 0)
                        {
                            // Only if the server's own update since says this client got it. Another client
                            // may have asked first, and shooting a ball not carried is refused.
                            if (client.IsCarryingBall((byte)ballToShoot))
                                await client.SendShootBallAsync((byte)ballToShoot, x, y, token);

                            ballToShoot = -1;
                        }
                        else if (ballChancePerTick > 0d && Random.Shared.NextDouble() < ballChancePerTick
                            && client.TryGetLooseBall(out byte ballId, out uint ballTime))
                        {
                            if (await client.SendPickupBallAsync(ballId, ballTime, token))
                                ballToShoot = ballId;
                        }

                        if (flagToDrop)
                        {
                            await client.SendDropFlagsAsync(token);
                            flagToDrop = false;
                        }
                        else if (flagChancePerTick > 0d && Random.Shared.NextDouble() < flagChancePerTick)
                        {
                            // Flag ids start at zero. The test arena is configured with at least five; an id
                            // past what an arena has is refused with a note in the log, not an error.
                            if (await client.SendTouchFlagAsync((short)Random.Shared.Next(5), token))
                                flagToDrop = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            }
        }

        private static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Connecting {options.ClientCount} client(s) to {options.Host}:{options.Port}, arenas: {string.Join(", ", options.Arenas)}");

            ChatClient[] clients = new ChatClient[options.ClientCount];
            Task[] setupTasks = new Task[options.ClientCount];
            Task?[] drainTasks = new Task?[options.ClientCount];

            int connected = 0;
            int loggedIn = 0;

            long startTimestamp = Stopwatch.GetTimestamp();

            // Clients are brought up concurrently. A login is a round trip that the server answers from
            // its player state machine, which runs on a 100ms timer, so doing this one at a time costs
            // roughly a tick per client and a few hundred clients would take longer than the run itself.
            for (int i = 0; i < options.ClientCount; i++)
            {
                int index = i;
                ChatClient client = new($"{options.NamePrefix}{index:D4}", options.Password);
                clients[index] = client;

                setupTasks[index] = SetupClientAsync(client, index);

                // Staggering arrivals separates the cost of connecting from the steady state being
                // measured. Awaited here rather than inside the task so that it actually spaces starts.
                if (options.ConnectDelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(options.ConnectDelayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            try
            {
                await Task.WhenAll(setupTasks.Where(t => t is not null));
            }
            catch (OperationCanceledException)
            {
                // Expected if the run was cut short while clients were still arriving.
            }

            Console.WriteLine($"Connected {connected}/{options.ClientCount}, logged in {loggedIn}, in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:F1}s");

            async Task SetupClientAsync(ChatClient client, int index)
            {
                if (!await client.ConnectAsync(options.Host, options.Port, cancellationToken))
                    return;

                Interlocked.Increment(ref connected);

                if (!await client.LoginAsync(cancellationToken))
                    return;

                Interlocked.Increment(ref loggedIn);

                // Spread clients over the arenas round robin, so a run with several arenas produces an
                // even population rather than piling everyone into the first one.
                string arena = options.Arenas[index % options.Arenas.Length];
                await client.GoToArenaAsync(arena, cancellationToken);

                // The first client watches for output, so that a command's reply is visible. The rest
                // discard, but still have to read or the server's buffer for them fills up.
                Action<string>? onLine = index == 0 ? line => Console.WriteLine($"  [{client.Name}] {line}") : null;
                drainTasks[index] = client.DrainAsync(onLine, cancellationToken);
            }

            if (loggedIn == 0)
            {
                Console.WriteLine("No client logged in. First failure: " + (Array.Find(clients, c => c.FailureReason is not null)?.FailureReason ?? "unknown"));
                return 1;
            }

            // Run any requested commands from the first logged in client. This is how a bot load test is
            // started, since ?loadtest is a normal server command and needs a player to issue it.
            if (options.Commands.Count > 0)
            {
                ChatClient? commander = Array.Find(clients, c => c.IsLoggedIn);
                if (commander is not null)
                {
                    // Entering an arena is not instant; the command is rejected if it arrives first.
                    await Task.Delay(1000, cancellationToken);

                    foreach (string command in options.Commands)
                    {
                        Console.WriteLine($"Sending command: ?{command}");
                        await commander.SendCommandAsync(command, cancellationToken);
                        await Task.Delay(500, cancellationToken);
                    }
                }
            }

            Console.WriteLine(options.Duration > TimeSpan.Zero
                ? $"Holding for {options.Duration.TotalSeconds:F0}s. Read the server's ?metrics output now."
                : "Holding until interrupted (Ctrl+C). Read the server's ?metrics output now.");

            try
            {
                await Task.WhenAll(drainTasks.Where(t => t is not null)!);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            int stillUp = 0;
            foreach (ChatClient client in clients)
            {
                if (client.IsLoggedIn && client.FailureReason is null)
                    stillUp++;

                client.Dispose();
            }

            Console.WriteLine($"Finished. {stillUp}/{loggedIn} client(s) survived the run.");

            return 0;
        }
    }
}
