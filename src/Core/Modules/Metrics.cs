using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MetricsSettings = SS.Core.ConfigHelp.Constants.Global.Metrics;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that collects server-wide performance counters and reports them, implementing <see cref="IServerMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists to make scaling work measurable. The server has no automated performance tests, so the
    /// counters here are the only way to tell a real improvement from a regression when changing hot paths
    /// such as the position fan-out, the mainloop queue, or the network send thread.
    /// </para>
    /// <para>
    /// Counters are plain interlocked adds on an array, and callers batch their reporting, so the cost on
    /// hot paths is one interlocked operation per packet rather than per iteration. See
    /// <see cref="IServerMetrics"/> for the contract callers must follow.
    /// </para>
    /// <para>
    /// There is no ASSS equivalent of this module.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Metrics(
        IConfigManager configManager,
        ILogManager logManager,
        IMainloopTimer mainloopTimer,
        IObjectPoolManager objectPoolManager) : IModule, IModuleLoaderAware, IServerMetrics
    {
        // Required dependencies
        // These are deliberately limited to modules that load early, so that this module can be loaded
        // before the hot paths that report to it (Mainloop, PlayerData, Network, Game).
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloopTimer _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

        // Optional dependencies
        // Retrieved in PostLoad since the modules that provide them load after this one.
        private IChat? _chat;
        private ICommandManager? _commandManager;

        // Component registrations
        private InterfaceRegistrationToken<IServerMetrics>? _iServerMetricsToken;

        /// <summary>
        /// The counters and gauges, indexed by <see cref="ServerMetric"/>.
        /// </summary>
        private readonly long[] _values = new long[Enum.GetValues<ServerMetric>().Length];

        /// <summary>
        /// Timestamp that the current sampling window started at.
        /// </summary>
        private long _windowStartTimestamp = Stopwatch.GetTimestamp();

        // Garbage collection is sampled from the runtime at the start and end of the window rather than
        // reported by callers, so it is kept here instead of in ServerMetric.
        // These tell whether the object pools are actually doing their job. The pools are sized from
        // Constants.TargetPlayerCount (512), so at much higher player counts they stop retaining enough
        // and the server starts allocating in steady state instead of reusing.
        private long _windowStartAllocatedBytes;
        private int _windowStartGen0;
        private int _windowStartGen1;
        private int _windowStartGen2;

        private bool _isEnabled;

        #region Module members

        [ConfigHelp<bool>("Metrics", "Enabled", ConfigScope.Global, Default = true,
            Description = """
                Whether to collect server performance counters.
                Collection is cheap (callers batch their reporting), so this is on whenever the module is loaded.
                Turn it off to remove the cost entirely without unloading the module.
                """)]
        [ConfigHelp<int>("Metrics", "ReportInterval", ConfigScope.Global, Default = 0, Min = 0,
            Description = """
                How often to write a metrics summary to the log (in seconds).
                0 to never write one automatically, in which case ?metrics reports on demand.
                """)]
        bool IModule.Load(IComponentBroker broker)
        {
            _isEnabled = _configManager.GetBool(_configManager.Global, "Metrics", "Enabled", MetricsSettings.Enabled.Default);

            int reportInterval = _configManager.GetInt(_configManager.Global, "Metrics", "ReportInterval", MetricsSettings.ReportInterval.Default);
            if (reportInterval < 0)
                reportInterval = 0;

            if (reportInterval > 0)
            {
                int intervalMilliseconds = reportInterval * 1000;
                _mainloopTimer.SetTimer(MainloopTimer_Report, intervalMilliseconds, intervalMilliseconds, null);
            }

            // Establishes the garbage collection baselines for the first window.
            Reset();

            _iServerMetricsToken = broker.RegisterInterface<IServerMetrics>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iServerMetricsToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(MainloopTimer_Report, null);

            return true;
        }

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            _chat = broker.GetInterface<IChat>();
            _commandManager = broker.GetInterface<ICommandManager>();

            _commandManager?.AddCommand("metrics", Command_Metrics);
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            _commandManager?.RemoveCommand("metrics", Command_Metrics);

            if (_chat is not null)
            {
                broker.ReleaseInterface(ref _chat);
            }

            if (_commandManager is not null)
            {
                broker.ReleaseInterface(ref _commandManager);
            }
        }

        #endregion

        #region IServerMetrics

        bool IServerMetrics.IsEnabled => _isEnabled;

        void IServerMetrics.Add(ServerMetric metric, long value)
        {
            if (!_isEnabled)
                return;

            Interlocked.Add(ref _values[(int)metric], value);
        }

        void IServerMetrics.Increment(ServerMetric metric)
        {
            if (!_isEnabled)
                return;

            Interlocked.Increment(ref _values[(int)metric]);
        }

        void IServerMetrics.SetGauge(ServerMetric metric, long value)
        {
            if (!_isEnabled)
                return;

            Interlocked.Exchange(ref _values[(int)metric], value);
        }

        long IServerMetrics.Get(ServerMetric metric)
        {
            return Interlocked.Read(ref _values[(int)metric]);
        }

        void IServerMetrics.Reset()
        {
            Reset();
        }

        TimeSpan IServerMetrics.SamplingWindow => Stopwatch.GetElapsedTime(Interlocked.Read(ref _windowStartTimestamp));

        #endregion

        private void Reset()
        {
            for (int i = 0; i < _values.Length; i++)
            {
                Interlocked.Exchange(ref _values[i], 0);
            }

            _windowStartAllocatedBytes = GC.GetTotalAllocatedBytes();
            _windowStartGen0 = GC.CollectionCount(0);
            _windowStartGen1 = GC.CollectionCount(1);
            _windowStartGen2 = GC.CollectionCount(2);

            Interlocked.Exchange(ref _windowStartTimestamp, Stopwatch.GetTimestamp());
        }

        private bool MainloopTimer_Report()
        {
            WriteReport(null);
            Reset();

            return true;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[reset]",
            Description = """
                Reports the server performance counters collected since the last reset.
                Use ?metrics reset to zero the counters and restart the sampling window without reporting.
                """)]
        private void Command_Metrics(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!_isEnabled)
            {
                _chat?.SendMessage(player, "Metrics collection is disabled (Metrics:Enabled).");
                return;
            }

            if (parameters.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                _chat?.SendMessage(player, "Metrics reset.");
                return;
            }

            WriteReport(player);
        }

        /// <summary>
        /// Writes a human readable summary of the current sampling window, one metric group per line.
        /// </summary>
        /// <param name="player">The player to send the report to, or <see langword="null"/> to write it to the log.</param>
        private void WriteReport(Player? player)
        {
            double seconds = Stopwatch.GetElapsedTime(Interlocked.Read(ref _windowStartTimestamp)).TotalSeconds;
            if (seconds <= 0d)
                seconds = double.Epsilon;

            long mainloopIterations = Read(ServerMetric.MainloopIterations);
            long positionPackets = Read(ServerMetric.PositionPacketsHandled);
            long candidatesScanned = Read(ServerMetric.PositionCandidatesScanned);
            long recipientsInView = Read(ServerMetric.PositionRecipientsInView);

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append($"metrics over {seconds:F1}s");
                Emit(sb, player);

                sb.Append($"  mainloop: {mainloopIterations / seconds:F0} iter/s, mean {MeanMilliseconds(Read(ServerMetric.MainloopElapsedTicks), mainloopIterations):F3}ms, queue depth {Read(ServerMetric.MainloopQueueDepth)}");
                Emit(sb, player);

                sb.Append($"  mainloop work: {Read(ServerMetric.MainloopWorkItemsProcessed) / seconds:F0}/s processed, {Read(ServerMetric.MainloopWorkItemsDropped)} dropped, {Read(ServerMetric.MainloopDrainLimitHits)} drain-limit hits");
                Emit(sb, player);

                // Candidates scanned per packet is the headline cost metric, and mean in-view is reported
                // beside it because the two only mean anything together. See IServerMetrics.
                sb.Append($"  position: {positionPackets / seconds:F0} pkt/s, {candidatesScanned / seconds:F0} scanned/s, {PerPacket(candidatesScanned, positionPackets):F1} scanned/pkt, {PerPacket(recipientsInView, positionPackets):F1} in-view/pkt");
                Emit(sb, player);

                sb.Append($"  network: {Read(ServerMetric.NetworkSendToOne) / seconds:F0} SendToOne/s, send thread {Read(ServerMetric.SendThreadIterations) / seconds:F0} iter/s, {Read(ServerMetric.SendThreadOverruns)} overruns");
                Emit(sb, player);

                sb.Append($"  player lock: {Read(ServerMetric.PlayerDataReadLocks) / seconds:F0} read/s, {Read(ServerMetric.PlayerDataWriteLocks) / seconds:F0} write/s, write held {TotalMilliseconds(Read(ServerMetric.PlayerDataWriteLockHeldTicks)):F1}ms");
                Emit(sb, player);

                // Sustained allocation here means the object pools have stopped keeping up, since the
                // server is written to avoid allocating in steady state.
                sb.Append($"  gc: {(GC.GetTotalAllocatedBytes() - _windowStartAllocatedBytes) / seconds / 1024d:F0} KB/s allocated, gen0 {GC.CollectionCount(0) - _windowStartGen0}, gen1 {GC.CollectionCount(1) - _windowStartGen1}, gen2 {GC.CollectionCount(2) - _windowStartGen2}");
                Emit(sb, player);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            long Read(ServerMetric metric) => Interlocked.Read(ref _values[(int)metric]);

            static double PerPacket(long total, long packets) => packets > 0 ? (double)total / packets : 0d;

            static double TotalMilliseconds(long stopwatchTicks) => stopwatchTicks * 1000d / Stopwatch.Frequency;

            static double MeanMilliseconds(long stopwatchTicks, long count) => count > 0 ? TotalMilliseconds(stopwatchTicks) / count : 0d;
        }

        /// <summary>
        /// Sends one built line and clears the builder ready for the next.
        /// </summary>
        /// <param name="sb">The line to send. Cleared on return.</param>
        /// <param name="player">The player to send to, or <see langword="null"/> to write to the log.</param>
        private void Emit(StringBuilder sb, Player? player)
        {
            if (player is not null && _chat is not null)
                _chat.SendMessage(player, sb);
            else
                _logManager.LogM(LogLevel.Info, nameof(Metrics), sb);

            sb.Clear();
        }
    }
}
