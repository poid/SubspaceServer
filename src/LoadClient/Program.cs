using System.Diagnostics;

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

            return await RunAsync(options, cancellationTokenSource.Token);
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
