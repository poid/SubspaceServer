namespace SS.LoadClient
{
    /// <summary>
    /// Which protocol simulated clients speak, and therefore what they can put load on.
    /// </summary>
    internal enum LoadProtocol
    {
        /// <summary>
        /// The chat protocol over TCP. Drives the player life cycle but sends no position packets.
        /// </summary>
        Chat,

        /// <summary>
        /// The game protocol over UDP. Sends position packets and receives updates, so it is the only
        /// one that loads the position fan out and the network send path. Needs EncryptionNull loaded.
        /// </summary>
        Game,
    }

    /// <summary>
    /// Command line options for the load client.
    /// </summary>
    internal sealed class Options
    {
        /// <summary>
        /// Which protocol the simulated clients speak.
        /// </summary>
        public LoadProtocol Protocol { get; private set; } = LoadProtocol.Chat;

        /// <summary>
        /// How far apart, in pixels, to spread the game clients when they enter an arena.
        /// </summary>
        /// <remarks>
        /// This is what decides how many of them end up within view of each other, which is what the
        /// position fan out costs scale with, so it is the main dial on this kind of run.
        /// </remarks>
        public int SpreadPixels { get; private set; } = 4096;

        /// <summary>
        /// Screen size the game clients claim, which is what the server uses as their view range.
        /// </summary>
        public short ViewWidth { get; private set; } = 1920;

        public short ViewHeight { get; private set; } = 1080;

        public string Host { get; private set; } = "127.0.0.1";

        /// <summary>
        /// The chat protocol's TCP port. This is the Net:ChatListen setting, which defaults to the
        /// [Listen] port when not set.
        /// </summary>
        public int Port { get; private set; } = 5000;

        public int ClientCount { get; private set; } = 100;

        /// <summary>
        /// Arenas to spread the clients over, round robin.
        /// </summary>
        public string[] Arenas { get; private set; } = ["0"];

        public string NamePrefix { get; private set; } = "Load";

        public string Password { get; private set; } = "loadtest";

        /// <summary>
        /// How long to hold the connections open, or zero to hold until interrupted.
        /// </summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>
        /// How long to wait between connecting each client.
        /// </summary>
        /// <remarks>
        /// Connecting several thousand clients as fast as possible is itself a burst that can crowd out
        /// what is being measured. Spacing them out separates the cost of arrival from the steady state.
        /// </remarks>
        public int ConnectDelayMs { get; private set; }

        /// <summary>
        /// Commands to issue once the clients are in, without the leading '?'.
        /// </summary>
        public List<string> Commands { get; } = [];

        public static Options? Parse(string[] args)
        {
            Options options = new();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;

                switch (arg)
                {
                    case "--host" when next is not null:
                        options.Host = next;
                        i++;
                        break;

                    case "--port" when next is not null && int.TryParse(next, out int port):
                        options.Port = port;
                        i++;
                        break;

                    case "--clients" when next is not null && int.TryParse(next, out int clientCount):
                        options.ClientCount = clientCount;
                        i++;
                        break;

                    case "--arenas" when next is not null:
                        options.Arenas = next.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        i++;
                        break;

                    case "--name-prefix" when next is not null:
                        options.NamePrefix = next;
                        i++;
                        break;

                    case "--password" when next is not null:
                        options.Password = next;
                        i++;
                        break;

                    case "--duration" when next is not null && int.TryParse(next, out int seconds):
                        options.Duration = TimeSpan.FromSeconds(seconds);
                        i++;
                        break;

                    case "--connect-delay" when next is not null && int.TryParse(next, out int delay):
                        options.ConnectDelayMs = delay;
                        i++;
                        break;

                    case "--command" when next is not null:
                        options.Commands.Add(next);
                        i++;
                        break;

                    case "--protocol" when next is not null:
                        if (next.Equals("game", StringComparison.OrdinalIgnoreCase))
                            options.Protocol = LoadProtocol.Game;
                        else if (next.Equals("chat", StringComparison.OrdinalIgnoreCase))
                            options.Protocol = LoadProtocol.Chat;
                        else
                            return null;
                        i++;
                        break;

                    case "--spread" when next is not null && int.TryParse(next, out int spread) && spread > 0:
                        options.SpreadPixels = spread;
                        i++;
                        break;

                    case "--view" when next is not null:
                        string[] parts = next.Split('x', StringSplitOptions.TrimEntries);
                        if (parts.Length != 2 || !short.TryParse(parts[0], out short width) || !short.TryParse(parts[1], out short height))
                            return null;
                        options.ViewWidth = width;
                        options.ViewHeight = height;
                        i++;
                        break;

                    case "--help":
                    case "-h":
                        return null;

                    default:
                        Console.WriteLine($"Unrecognized argument: {arg}");
                        return null;
                }
            }

            if (options.ClientCount <= 0 || options.Arenas.Length == 0)
                return null;

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
                Generates load against a Subspace server so that its performance counters mean something.

                Usage:
                  SS.LoadClient [options]

                Options:
                  --host <host>            Server host. Default 127.0.0.1
                  --port <port>            Chat protocol TCP port (Net:ChatListen). Default 5000
                  --clients <n>            How many clients to connect. Default 100
                  --arenas <a,b,c>         Arenas to spread clients over, round robin. Default 0
                  --name-prefix <prefix>   Player name prefix. Default Load
                  --password <password>    Password used for every client. Default loadtest
                  --duration <seconds>     How long to hold, or omit to hold until Ctrl+C
                  --connect-delay <ms>     Wait between connecting each client. Default 0
                  --command <command>      Command to issue once connected, without '?'. Repeatable

                Examples:
                  Drive the player life cycle with 200 clients over four arenas for two minutes:
                    SS.LoadClient --clients 200 --arenas 0,1,2,3 --duration 120

                  Start a bot load test, which is what exercises the position fan out:
                    SS.LoadClient --clients 1 --arenas 0 --command "loadtest 200 4" --duration 120

                Notes:
                  Clients arrive at roughly five logins per second, measured. That is a server side
                  limit, not a limit of this tool: the chat module runs a single threaded loop at 10Hz
                  and a login walks several states of the player state machine, each on a 100ms timer.
                  Allow for the ramp when choosing a duration, or the run will end while clients are
                  still arriving. Lowering Net:ChatMessageDelay does not help; it was measured.

                  Chat clients send no position packets, so this cannot exercise the position fan out
                  on its own. Use --command to start the bots module's ?loadtest for that.
                """);
        }
    }
}
