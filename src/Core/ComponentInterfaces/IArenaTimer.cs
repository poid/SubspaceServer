namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Runs timers on the thread that owns an <see cref="Arena"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="IMainloopTimer"/> for work that belongs to one arena. A timer set
    /// here fires on that arena's thread, so its callback may touch the arena's game state directly, in
    /// the same way a timer on the mainloop may touch anything the mainloop owns.
    /// </para>
    /// <para>
    /// This exists for two reasons. The obvious one is that a callback wanting to work on an arena
    /// should run where that arena runs. The less obvious one is cost: per-arena timers on the mainloop
    /// make it do work proportional to the number of arenas, on top of the work it does per player, and
    /// a zone with a few hundred small arenas has a lot of them. That cost does not go away by being
    /// small; it just does not show up until the arena count is large.
    /// </para>
    /// <para>
    /// Timers are cleared automatically when the arena is destroyed, so a module attached to an arena
    /// does not have to unwind them itself, though it may.
    /// </para>
    /// <para>
    /// There is no ASSS equivalent. ASSS has only its mainloop timers.
    /// </para>
    /// </remarks>
    public interface IArenaTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer that fires on an arena's thread.
        /// </summary>
        /// <remarks>
        /// The callback returns whether it wants to keep running, exactly as for
        /// <see cref="IMainloopTimer"/>.
        /// </remarks>
        /// <typeparam name="TState">The type of the state passed to the callback.</typeparam>
        /// <param name="arena">The arena whose thread should run the timer.</param>
        /// <param name="callback">The delegate to invoke.</param>
        /// <param name="initialDelay">How long to wait before the first call, in milliseconds.</param>
        /// <param name="interval">How long to wait between calls, in milliseconds.</param>
        /// <param name="state">Passed to the callback.</param>
        /// <param name="key">A key that can be used to selectively cancel timers.</param>
        void SetTimer<TState>(
            Arena arena,
            TimerDelegate<TState> callback,
            int initialDelay,
            int interval,
            TState state,
            object? key);

        /// <summary>
        /// Stops and removes timers for an arena.
        /// </summary>
        /// <remarks>
        /// A timer already running when this is called finishes; it simply is not run again. Callers
        /// that need to know it has stopped should arrange that themselves, as with
        /// <see cref="IMainloopTimer"/>.
        /// </remarks>
        /// <typeparam name="TState">The type of the state passed to the callback.</typeparam>
        /// <param name="arena">The arena the timer was set for.</param>
        /// <param name="callback">The delegate to clear timers for.</param>
        /// <param name="key">
        /// Timers matching this key are removed. <see langword="null"/> clears every timer for
        /// <paramref name="callback"/> on this arena, whatever its key.
        /// </param>
        void ClearTimer<TState>(
            Arena arena,
            TimerDelegate<TState> callback,
            object? key);
    }
}
