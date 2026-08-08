using System.Net.Sockets;
using System.Text;

namespace SS.LoadClient
{
    /// <summary>
    /// One simulated client speaking the server's chat protocol (see the ChatNetwork module).
    /// </summary>
    /// <remarks>
    /// The protocol is newline delimited text over TCP, which makes a client small enough to be worth
    /// having purely as a test instrument. It drives the whole player life cycle — connect, authenticate,
    /// enter an arena, leave, disconnect — without needing the game protocol or any encryption, so it
    /// exercises the player state machine, arena entry and exit, and the global player lock.
    /// <para>
    /// It sends no position packets, so it cannot exercise the position fan-out. Use the bots module's
    /// ?loadtest for that, driven through <see cref="SendCommandAsync"/> from one of these clients.
    /// </para>
    /// </remarks>
    internal sealed class ChatClient(string name, string password) : IDisposable
    {
        private static readonly Encoding s_encoding = Encoding.Latin1;

        private readonly TcpClient _tcpClient = new();
        private StreamWriter? _writer;
        private StreamReader? _reader;

        public string Name { get; } = name;

        /// <summary>
        /// Whether the server accepted the login.
        /// </summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>
        /// Why the client stopped, or <see langword="null"/> while it is still going.
        /// </summary>
        public string? FailureReason { get; private set; }

        public async Task<bool> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _tcpClient.ConnectAsync(host, port, cancellationToken);

                NetworkStream stream = _tcpClient.GetStream();
                _reader = new StreamReader(stream, s_encoding);
                _writer = new StreamWriter(stream, s_encoding) { AutoFlush = true, NewLine = "\n" };

                return true;
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
        /// Logs in and waits for the server's verdict.
        /// </summary>
        /// <returns><see langword="true"/> if the server replied LOGINOK.</returns>
        public async Task<bool> LoginAsync(CancellationToken cancellationToken)
        {
            // LOGIN:<version>;<client info>:<name>:<password>
            if (!await SendAsync($"LOGIN:1;SS.LoadClient:{Name}:{password}", cancellationToken))
                return false;

            // The server replies LOGINOK on success or LOGINBAD:<reason> on failure. Other lines can
            // arrive first, so read until one of those shows up.
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    FailureReason ??= "disconnected during login";
                    return false;
                }

                if (line.StartsWith("LOGINOK", StringComparison.Ordinal))
                {
                    IsLoggedIn = true;
                    return true;
                }

                if (line.StartsWith("LOGINBAD", StringComparison.Ordinal))
                {
                    FailureReason = line;
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Enters an arena.
        /// </summary>
        public Task<bool> GoToArenaAsync(string arenaName, CancellationToken cancellationToken)
            => SendAsync($"GO:{arenaName}", cancellationToken);

        /// <summary>
        /// Issues a server command, as though the player had typed it with a leading '?'.
        /// </summary>
        /// <param name="command">The command and its arguments, without the leading '?'.</param>
        public Task<bool> SendCommandAsync(string command, CancellationToken cancellationToken)
            => SendAsync($"SEND:CMD:{command}", cancellationToken);

        /// <summary>
        /// Sends a public chat message, which is the cheapest way to generate arena wide traffic.
        /// </summary>
        public Task<bool> SendPublicMessageAsync(string message, CancellationToken cancellationToken)
            => SendAsync($"SEND:PUB:{message}", cancellationToken);

        /// <summary>
        /// Reads and discards whatever the server sends until cancelled.
        /// </summary>
        /// <remarks>
        /// Something has to drain the socket, or the server's send buffer for this client fills and it
        /// starts reporting lag or drops the connection, which would look like a server problem.
        /// </remarks>
        /// <param name="onLine">Called for each line received, for a client that is watching for output.</param>
        public async Task DrainAsync(Action<string>? onLine, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        FailureReason ??= "disconnected";
                        return;
                    }

                    onLine?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                FailureReason ??= $"read: {ex.Message}";
            }
        }

        private async Task<bool> SendAsync(string line, CancellationToken cancellationToken)
        {
            if (_writer is null)
                return false;

            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                FailureReason ??= $"send: {ex.Message}";
                return false;
            }
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_reader is null)
                return null;

            try
            {
                return await _reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The run ended while waiting on the server. Report it as end of stream so that callers
                // wind down normally instead of the cancellation escaping mid-login.
                return null;
            }
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _tcpClient.Dispose();
        }
    }
}
