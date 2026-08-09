using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// A server-wide counter or gauge collected by <see cref="IServerMetrics"/>.
    /// </summary>
    /// <remarks>
    /// Values marked as a counter accumulate until the next <see cref="IServerMetrics.Reset"/>.
    /// Values marked as a gauge hold the most recently reported sample.
    /// </remarks>
    public enum ServerMetric
    {
        /// <summary>Counter. Iterations of the mainloop.</summary>
        MainloopIterations,

        /// <summary>Counter. Stopwatch ticks spent in mainloop iterations. Divide by <see cref="MainloopIterations"/> for the mean.</summary>
        MainloopElapsedTicks,

        /// <summary>Counter. Work items processed by the mainloop.</summary>
        MainloopWorkItemsProcessed,

        /// <summary>
        /// Counter. Work items rejected because the mainloop queue was full.
        /// Any non-zero value means the server is shedding work and the numbers around it are suspect.
        /// </summary>
        MainloopWorkItemsDropped,

        /// <summary>
        /// Counter. Drains that processed the whole batch they were allowed.
        /// Consistently hitting the limit means the mainloop is already saturated.
        /// </summary>
        MainloopDrainLimitHits,

        /// <summary>Gauge. Work items waiting in the mainloop queue.</summary>
        MainloopQueueDepth,

        /// <summary>Counter. Position packets put through the fan-out.</summary>
        PositionPacketsHandled,

        /// <summary>
        /// Counter. Players examined by the position fan-out loop.
        /// This is the headline cost metric: it is O(zone population) per packet today and
        /// O(arena population) per packet once a per-arena player collection exists.
        /// </summary>
        PositionCandidatesScanned,

        /// <summary>
        /// Counter. Players that passed the fan-out distance check and were sent to.
        /// Divide by <see cref="PositionPacketsHandled"/> for the mean in-view count, without which
        /// <see cref="PositionCandidatesScanned"/> cannot be compared between runs.
        /// </summary>
        PositionRecipientsInView,

        /// <summary>Counter. Single-player sends issued by the network module.</summary>
        NetworkSendToOne,

        /// <summary>Counter. Iterations of the network send thread.</summary>
        SendThreadIterations,

        /// <summary>Counter. Send thread iterations that overran their interval, meaning the send thread is falling behind.</summary>
        SendThreadOverruns,

        /// <summary>Counter. Read locks taken on the global player data lock.</summary>
        PlayerDataReadLocks,

        /// <summary>Counter. Write locks taken on the global player data lock.</summary>
        PlayerDataWriteLocks,

        /// <summary>Counter. Stopwatch ticks the global player data write lock was held for.</summary>
        PlayerDataWriteLockHeldTicks,
    }

    /// <summary>
    /// Collects server-wide performance counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All methods are safe to call from any thread.
    /// </para>
    /// <para>
    /// Reporting once per operation is fine for anything happening at up to a few hundred thousand a
    /// second: an interlocked add is a few nanoseconds, so at that rate it costs a fraction of a
    /// percent of one core. Sends and lock acquisitions are counted that way.
    /// </para>
    /// <para>
    /// Inner loops that run in the millions per second are different, and there callers must accumulate
    /// locally and report once at a natural boundary. The position fan-out tallies candidates and
    /// recipients in locals and reports once per position packet rather than once per candidate,
    /// because that loop runs at several million iterations a second at a few hundred players, where
    /// per-iteration reporting would be a measurable tax on the hottest loop in the server.
    /// </para>
    /// <para>
    /// Check <see cref="IsEnabled"/> before doing any work that exists only to produce a metric.
    /// Reporting while disabled is harmless but not free.
    /// </para>
    /// </remarks>
    public interface IServerMetrics : IComponentInterface
    {
        /// <summary>
        /// Whether metrics collection is turned on.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Adds to a counter.
        /// </summary>
        /// <param name="metric">The counter to add to.</param>
        /// <param name="value">The amount to add.</param>
        void Add(ServerMetric metric, long value);

        /// <summary>
        /// Adds one to a counter.
        /// </summary>
        /// <param name="metric">The counter to add to.</param>
        void Increment(ServerMetric metric);

        /// <summary>
        /// Sets a gauge to its latest sample.
        /// </summary>
        /// <param name="metric">The gauge to set.</param>
        /// <param name="value">The sampled value.</param>
        void SetGauge(ServerMetric metric, long value);

        /// <summary>
        /// Gets the current value of a counter or gauge.
        /// </summary>
        /// <param name="metric">The metric to read.</param>
        /// <returns>The current value.</returns>
        long Get(ServerMetric metric);

        /// <summary>
        /// Zeroes every counter and gauge, and restarts the sampling window.
        /// </summary>
        void Reset();

        /// <summary>
        /// Gets how long the current sampling window has been running, for converting counters into rates.
        /// </summary>
        TimeSpan SamplingWindow { get; }
    }
}
