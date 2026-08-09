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
            List<Task> tasks = new(options.ClientCount * 2);

            int connected = 0;
            long startTimestamp = Stopwatch.GetTimestamp();

            Task[] setupTasks = new Task[options.ClientCount];

            for (int i = 0; i < options.ClientCount; i++)
            {
                int index = i;
                GameClient client = new($"{options.NamePrefix}{index:D4}", options.Password);
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
                await Task.WhenAll(setupTasks);
            }
            catch (OperationCanceledException)
            {
                // Expected if the run was cut short while clients were still arriving.
            }

            Console.WriteLine($"Connected {connected}/{options.ClientCount} in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:F1}s");

            if (connected == 0)
            {
                Console.WriteLine("No client connected. First failure: " + (Array.Find(clients, c => c.FailureReason is not null)?.FailureReason ?? "unknown"));
                Console.WriteLine("Check that the EncryptionNull module is enabled in Modules.config.");
                return 1;
            }

            long measureStart = Stopwatch.GetTimestamp();

            Console.WriteLine(options.Duration > TimeSpan.Zero
                ? $"Holding for {options.Duration.TotalSeconds:F0}s at 10Hz. Read the server's ?metrics output now."
                : "Holding until interrupted (Ctrl+C) at 10Hz. Read the server's ?metrics output now.");

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            double seconds = Stopwatch.GetElapsedTime(measureStart).TotalSeconds;

            long positionUpdates = 0;
            long bytes = 0;
            int stillUp = 0;

            foreach (GameClient client in clients)
            {
                positionUpdates += Interlocked.Read(ref client.PositionUpdatesReceived);
                bytes += Interlocked.Read(ref client.BytesReceived);

                if (client.IsConnected && client.FailureReason is null)
                    stillUp++;
            }

            Console.WriteLine($"Finished. {stillUp}/{connected} client(s) survived.");
            Console.WriteLine($"Server sent {positionUpdates / seconds:F0} position updates/s and {bytes / seconds / 1024d:F0} KB/s to these clients ({positionUpdates / (double)Math.Max(connected, 1) / seconds:F1} updates/s each).");

            await Task.WhenAll(clients.Select(c => c.DisconnectAsync(CancellationToken.None)));

            foreach (GameClient client in clients)
                client.Dispose();

            return 0;

            async Task SetupAsync(GameClient client, int index)
            {
                if (!await client.ConnectAsync(options.Host, options.Port, cancellationToken))
                    return;

                Interlocked.Increment(ref connected);

                tasks.Add(client.ReceiveLoopAsync(cancellationToken));

                await client.LoginAsync(cancellationToken);

                // Give the server its state machine ticks to get the login through before asking to
                // enter an arena, or the request arrives too early and is ignored.
                await Task.Delay(1500, cancellationToken);

                string arena = options.Arenas[index % options.Arenas.Length];
                await client.GoToArenaAsync(arena, options.ViewWidth, options.ViewHeight, cancellationToken);

                await Task.Delay(1500, cancellationToken);

                tasks.Add(PositionLoopAsync(client, index, cancellationToken));
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

                try
                {
                    // 10Hz, matching a real client's SendPositionDelay of 10.
                    using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));

                    while (await timer.WaitForNextTickAsync(token))
                    {
                        angle += 0.1d;

                        short x = (short)Math.Clamp(originX + (Radius * Math.Cos(angle)), 0, 16383);
                        short y = (short)Math.Clamp(originY + (Radius * Math.Sin(angle)), 0, 16383);

                        rotation = (sbyte)((rotation + 1) % 40);

                        await client.SendPositionAsync(x, y, 0, 0, rotation, token);
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
