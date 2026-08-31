using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="LogDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    [ShardSafe]
    public static partial class LogCallback
    {
        // Logging has always been called from the receive, reliable and send threads, so its handlers
        // have never been able to assume the mainloop and an arena thread reaching them is nothing new.
        /// <summary>
        /// Delegate for a callback that is invoked when a log is to be written.
        /// </summary>
        /// <param name="message">The log message to write.</param>
        public delegate void LogDelegate(ref readonly LogEntry logEntry);
    }
}
