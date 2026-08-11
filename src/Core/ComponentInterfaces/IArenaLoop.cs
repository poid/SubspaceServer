using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Runs work on the thread that owns an <see cref="Arena"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost all game logic has historically run on the mainloop thread, which means the whole zone's
    /// game logic runs on one core no matter how many the machine has. Arenas are independent of each
    /// other for the purposes of gameplay, so they can be spread over several threads instead.
    /// </para>
    /// <para>
    /// An arena is assigned to a worker when it is created and stays there for its whole life, so the
    /// work queued for one arena runs in the order it was queued, on one thread at a time. That is the
    /// property everything else here depends on: it means an arena's game state has a single writer,
    /// the same guarantee the mainloop gave, without the whole zone sharing one.
    /// </para>
    /// <para>
    /// Several arenas share a worker. This is deliberately not one thread per arena: a zone with two
    /// hundred arenas would spend more on context switching than it saved.
    /// </para>
    /// <para>
    /// This does not replace <see cref="IMainloop"/>. Anything zone-wide — the player state machine,
    /// arena creation and destruction, chat, billing — stays on the mainloop, and work that touches
    /// more than one arena must go there rather than being queued to either one.
    /// </para>
    /// </remarks>
    public interface IArenaLoop : IComponentInterface
    {
        /// <summary>
        /// Queues work to run on the thread that owns an arena.
        /// </summary>
        /// <remarks>
        /// The work runs after anything already queued for the same arena, and never at the same time as
        /// it. Nothing is guaranteed about its order relative to work queued for a different arena.
        /// <para>
        /// Work queued this way is never discarded to relieve load. Use
        /// <see cref="QueueSheddableArenaWorkItem{TState}"/> for work that may be dropped.
        /// </para>
        /// </remarks>
        /// <typeparam name="TState">The type of the state object.</typeparam>
        /// <param name="arena">The arena whose thread should run the work.</param>
        /// <param name="callback">The delegate to call.</param>
        /// <param name="state">The state to pass to the delegate.</param>
        /// <returns><see langword="true"/> if the work was queued; otherwise <see langword="false"/>.</returns>
        bool QueueArenaWorkItem<TState>(Arena arena, Action<TState> callback, TState state);

        /// <summary>
        /// Queues work to run on the thread that owns an arena, which may be refused when that thread is
        /// already too far behind.
        /// </summary>
        /// <remarks>
        /// This is for work whose loss is recoverable and preferable to unbounded latency, which in
        /// practice means incoming game packets. See <see cref="IMainloop.QueueSheddableMainWorkItem"/>,
        /// which this mirrors, for what does and does not belong here.
        /// <para>
        /// Callers must handle a <see langword="false"/> result, including releasing anything the state
        /// was holding.
        /// </para>
        /// </remarks>
        /// <typeparam name="TState">The type of the state object.</typeparam>
        /// <param name="arena">The arena whose thread should run the work.</param>
        /// <param name="callback">The delegate to call.</param>
        /// <param name="state">The state to pass to the delegate.</param>
        /// <returns><see langword="true"/> if the work was queued; <see langword="false"/> if it was refused and will not run.</returns>
        bool QueueSheddableArenaWorkItem<TState>(Arena arena, Action<TState> callback, TState state);

        /// <summary>
        /// Blocks until everything queued for an arena has finished running.
        /// </summary>
        /// <remarks>
        /// For use when an arena is being torn down, so that no work is still touching it afterwards.
        /// Must not be called from the arena's own thread, which would deadlock.
        /// </remarks>
        /// <param name="arena">The arena to wait for.</param>
        void WaitForArenaWorkItemDrain(Arena arena);

        /// <summary>
        /// Gets whether the current thread is the one that owns an arena.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="IMainloop.IsMainloop"/>, and the basis of the debug assertions
        /// that document which thread a piece of arena code expects to be on.
        /// </remarks>
        /// <param name="arena">The arena to check.</param>
        /// <returns><see langword="true"/> if the calling thread owns <paramref name="arena"/>.</returns>
        bool IsArenaThread(Arena arena);

        /// <summary>
        /// Gets how many worker threads arenas are spread over.
        /// </summary>
        int WorkerCount { get; }
    }
}
