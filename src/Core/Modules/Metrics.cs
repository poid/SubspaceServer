using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        IMainloop mainloop,
        IMainloopTimer mainloopTimer,
        IObjectPoolManager objectPoolManager) : IModule, IModuleLoaderAware, IServerMetrics
    {
        // Required dependencies
        // These are deliberately limited to modules that load early, so that this module can be loaded
        // before the hot paths that report to it (Mainloop, PlayerData, Network, Game).
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
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

        /// <summary>
        /// Processor time the whole process had used when the window started.
        /// </summary>
        /// <remarks>
        /// Sampled from the runtime at the window boundaries, like the garbage collection counters.
        /// The difference over the window says how much processor time the load actually cost.
        /// </remarks>
        private TimeSpan _windowStartCpuTime;

        /// <summary>
        /// Processor time each thread had used when the window started. Key is the thread name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Per thread rather than per core, because a thread is the unit of parallelism here and a
        /// thread cannot use more than one core. Which cores the operating system happens to run those
        /// threads on changes constantly and is not attributed per process, so a per core breakdown
        /// would be guesswork. A saturated thread is effectively a core in use, and naming it says
        /// which part of the server that core is going to.
        /// </para>
        /// <para>
        /// Linux only, since it comes from /proc. Elsewhere only the process total is reported.
        /// </para>
        /// </remarks>
        private readonly Dictionary<string, long> _windowStartThreadCpuTicks = new(32);

        /// <summary>
        /// Reused between samples so that reporting does not allocate a dictionary each window.
        /// </summary>
        private readonly Dictionary<string, long> _threadCpuTicks = new(32);

        /// <summary>
        /// Guards the two thread processor dictionaries.
        /// </summary>
        /// <remarks>
        /// Reset is documented as callable from any thread and rewrites the window's starting sample
        /// while reporting reads it, so without this the report can throw for enumerating a dictionary
        /// that changed underneath it. Taken once per window.
        /// </remarks>
        private readonly Lock _threadCpuLock = new();

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

            // Sampling per thread processor time reads files and allocates strings, so it happens before
            // the allocation baseline is taken. The other way round, the measurement's own allocations
            // land inside the window it is starting and get reported as the server's.
            lock (_threadCpuLock)
            {
                SampleThreadCpu(_windowStartThreadCpuTicks);
            }

            _windowStartCpuTime = Environment.CpuUsage.TotalTime;
            _windowStartGen0 = GC.CollectionCount(0);
            _windowStartGen1 = GC.CollectionCount(1);
            _windowStartGen2 = GC.CollectionCount(2);
            _windowStartAllocatedBytes = GC.GetTotalAllocatedBytes();

            Interlocked.Exchange(ref _windowStartTimestamp, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Reads how much processor time each named thread has used, from /proc.
        /// </summary>
        /// <remarks>
        /// Does nothing on platforms other than Linux, leaving <paramref name="destination"/> empty so
        /// that callers simply report no per thread breakdown.
        /// </remarks>
        /// <param name="destination">Cleared and filled with thread name to processor time in clock ticks.</param>
        private static void SampleThreadCpu(Dictionary<string, long> destination)
        {
            destination.Clear();

            if (!OperatingSystem.IsLinux())
                return;

            // The main thread keeps the process name rather than the managed thread name, so it would
            // otherwise show up as the executable. It is worth calling out, because the mainloop runs
            // there and almost all game logic runs on the mainloop.
            int mainThreadId = Environment.ProcessId;

            try
            {
                foreach (string taskDirectory in Directory.EnumerateDirectories("/proc/self/task"))
                {
                    string name;
                    string stat;

                    try
                    {
                        name = File.ReadAllText(Path.Combine(taskDirectory, "comm")).Trim();
                        stat = File.ReadAllText(Path.Combine(taskDirectory, "stat"));
                    }
                    catch (IOException)
                    {
                        // The thread ended between listing and reading it.
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(name) || !TryGetThreadCpuTicks(stat, out long ticks))
                        continue;

                    if (int.TryParse(Path.GetFileName(taskDirectory.AsSpan()), out int threadId) && threadId == mainThreadId)
                        name = $"{name} (main, runs the mainloop)";

                    // Threads that genuinely share a name, such as the runtime's thread pool workers, are
                    // summed, which is what is wanted: together they are one pool doing one job. The
                    // server's own pools number their threads instead (Network-send-0, Network-rel-0, ...)
                    // and so stay separate, which is what shows whether the work is evenly split.
                    destination[name] = destination.TryGetValue(name, out long existing) ? existing + ticks : ticks;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // No /proc, so no breakdown.
            }
        }

        /// <summary>
        /// Pulls the user and system times out of a /proc stat line.
        /// </summary>
        /// <remarks>
        /// The thread name sits in parentheses and can itself contain spaces and brackets, so the fields
        /// are counted from the last close parenthesis rather than by splitting the whole line.
        /// </remarks>
        private static bool TryGetThreadCpuTicks(ReadOnlySpan<char> stat, out long ticks)
        {
            ticks = 0;

            int commEnd = stat.LastIndexOf(')');
            if (commEnd < 0 || commEnd + 2 >= stat.Length)
                return false;

            // Fields after the name, zero based: 0 is state, so utime is 11 and stime is 12.
            const int UTimeIndex = 11;
            const int STimeIndex = 12;

            ReadOnlySpan<char> remaining = stat[(commEnd + 2)..];
            long uTime = 0;
            long sTime = 0;

            for (int index = 0; index <= STimeIndex; index++)
            {
                int space = remaining.IndexOf(' ');
                ReadOnlySpan<char> field = space < 0 ? remaining : remaining[..space];

                if (index == UTimeIndex && !long.TryParse(field, out uTime))
                    return false;

                if (index == STimeIndex && !long.TryParse(field, out sTime))
                    return false;

                if (space < 0)
                    return false;

                remaining = remaining[(space + 1)..];
            }

            ticks = uTime + sTime;
            return true;
        }

        private bool MainloopTimer_Report()
        {
            // Reporting reads per thread processor time out of /proc, which is blocking file I/O, and
            // it would run on the mainloop thread if done here. That would stall the mainloop on a
            // schedule, so a load test would show a periodic hitch caused by the measurement and then
            // blame whatever change was being evaluated. The counters are all interlocked, and the log
            // and chat interfaces are safe from any thread, so the whole report is done off it.
            _mainloop.QueueThreadPoolWorkItem(ThreadPoolWork_Report, (Player?)null);

            return true;
        }

        private void ThreadPoolWork_Report(Player? player)
        {
            WriteReport(player);

            // Only the timed report starts a new window; ?metrics reports what has accumulated without
            // disturbing it, so that reading the numbers does not change them.
            if (player is null)
                Reset();
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

            // Off the mainloop for the same reason as the timed report: reading per thread processor
            // time out of /proc is blocking file I/O, and this command handler runs on the mainloop.
            _mainloop.QueueThreadPoolWorkItem(ThreadPoolWork_Report, player);
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

                sb.Append($"  arena work: {Read(ServerMetric.ArenaLoopWorkItemsProcessed) / seconds:F0}/s processed, {Read(ServerMetric.ArenaLoopWorkItemsDropped)} dropped");
                Emit(sb, player);

                // Candidates scanned per packet is the headline cost metric, and mean in-view is reported
                // beside it because the two only mean anything together. See IServerMetrics.
                sb.Append($"  position: {positionPackets / seconds:F0} pkt/s, {candidatesScanned / seconds:F0} scanned/s, {PerPacket(candidatesScanned, positionPackets):F1} scanned/pkt, {PerPacket(recipientsInView, positionPackets):F1} in-view/pkt");
                Emit(sb, player);

                sb.Append($"  network: {Read(ServerMetric.NetworkSendToOne) / seconds:F0} SendToOne/s, send thread {Read(ServerMetric.SendThreadIterations) / seconds:F0} iter/s, {Read(ServerMetric.SendThreadOverruns)} overruns");
                Emit(sb, player);

                sb.Append($"  send threads: {Read(ServerMetric.SendThreadsActive)} running, busiest {Read(ServerMetric.SendThreadBusiestPercent)}% of its interval");
                Emit(sb, player);

                sb.Append($"  player lock: {Read(ServerMetric.PlayerDataReadLocks) / seconds:F0} read/s, {Read(ServerMetric.PlayerDataWriteLocks) / seconds:F0} write/s, write held {TotalMilliseconds(Read(ServerMetric.PlayerDataWriteLockHeldTicks)):F1}ms, write waited {TotalMilliseconds(Read(ServerMetric.PlayerDataWriteLockWaitTicks)):F1}ms");
                Emit(sb, player);

                // Queue depth is the one that matters: loading a player's data is a blocking step of
                // entering an arena, so a depth that keeps growing is players waiting to get in.
                long persistWorkItems = Read(ServerMetric.PersistWorkItemsProcessed);
                long persistTransactions = Read(ServerMetric.PersistTransactions);
                sb.Append($"  persist: {persistWorkItems / seconds:F0} item/s, {MeanMilliseconds(Read(ServerMetric.PersistWorkItemElapsedTicks), persistWorkItems):F2}ms/item, {Read(ServerMetric.PersistQueueDepth)} queued, {persistTransactions / seconds:F0} txn/s, {MeanMilliseconds(Read(ServerMetric.PersistCommitElapsedTicks), persistTransactions):F2}ms/commit, {Read(ServerMetric.PersistPlayersSynced)} synced");
                Emit(sb, player);

                // Sustained allocation here means the object pools have stopped keeping up, since the
                // server is written to avoid allocating in steady state.
                sb.Append($"  gc: {(GC.GetTotalAllocatedBytes() - _windowStartAllocatedBytes) / seconds / 1024d:F0} KB/s allocated, gen0 {GC.CollectionCount(0) - _windowStartGen0}, gen1 {GC.CollectionCount(1) - _windowStartGen1}, gen2 {GC.CollectionCount(2) - _windowStartGen2}");
                Emit(sb, player);

                // Cores used is the process' processor time over elapsed time: 1.0 means it kept one
                // core busy. It is reported against the machine's core count so that a number like 2.4
                // can be read as either comfortable or nearly everything available.
                double coresUsed = (Environment.CpuUsage.TotalTime - _windowStartCpuTime).TotalSeconds / seconds;
                sb.Append($"  cpu: {coresUsed:F2} cores used of {Environment.ProcessorCount}, {coresUsed * 100d:F0}% of one core");
                Emit(sb, player);

                AppendThreadCpu(sb, player, seconds);

                sb.Append($"  memory: {Environment.WorkingSet / (1024d * 1024d):F0} MB working set, {GC.GetTotalMemory(false) / (1024d * 1024d):F0} MB managed heap");
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
        /// Writes the per thread processor usage for the window, busiest first.
        /// </summary>
        /// <remarks>
        /// Each figure is a percentage of one core, so a thread at 100% is saturated and is the limit on
        /// what the server can do no matter how many cores the machine has. That is the number worth
        /// watching: the server's parallelism is bounded by its threads, not by the core count.
        /// </remarks>
        /// <param name="sb">The builder to write into. Cleared on return.</param>
        /// <param name="player">Who to send to, or <see langword="null"/> for the log.</param>
        /// <param name="seconds">Length of the window.</param>
        private void AppendThreadCpu(StringBuilder sb, Player? player, double seconds)
        {
            // /proc reports processor time in clock ticks, which is USER_HZ, fixed at 100 on Linux.
            const double TicksPerSecond = 100d;

            // Below this a thread is idle bookkeeping rather than doing work, and listing every one of
            // them would bury the threads that matter.
            const double ActiveThresholdPercent = 1d;

            List<(string Name, double Percent)> active = [];

            // Held across the sample and the comparison, since Reset can rewrite the window's starting
            // sample from another thread and this would otherwise enumerate it as it changed.
            lock (_threadCpuLock)
            {
                SampleThreadCpu(_threadCpuTicks);

                foreach ((string name, long endTicks) in _threadCpuTicks)
                {
                    _windowStartThreadCpuTicks.TryGetValue(name, out long startTicks);

                    double percent = (endTicks - startTicks) / TicksPerSecond / seconds * 100d;
                    if (percent >= ActiveThresholdPercent)
                        active.Add((name, percent));
                }
            }

            if (active.Count == 0)
                return;

            active.Sort(static (a, b) => b.Percent.CompareTo(a.Percent));

            double total = 0d;
            foreach ((_, double percent) in active)
                total += percent;

            sb.Append($"  threads: {active.Count} active, mean {total / active.Count:F0}% of a core each");
            Emit(sb, player);

            sb.Append("   ");
            for (int i = 0; i < active.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                sb.Append($" {active[i].Name} {active[i].Percent:F0}%");
            }

            Emit(sb, player);
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
