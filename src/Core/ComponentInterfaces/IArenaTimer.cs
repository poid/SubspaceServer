namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface with methods to schedule timers that run on the thread that owns an <see cref="Arena"/>.
    /// See <see cref="IMainloopTimer"/> for timers that run on the mainloop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the timer counterpart of <see cref="IArenaLoop"/>, and exists for the same reason: a timer
    /// that only ever touches one arena has no need of the mainloop, and putting every arena's timers there
    /// makes the mainloop the place where all of them queue up behind each other.
    /// </para>
    /// <para>
    /// A callback set here runs on the thread that owns its arena, in order with the rest of that arena's
    /// work and never at the same time as it. That means a timer callback may read and write its arena's
    /// game state directly, exactly as it could when it ran on the mainloop.
    /// </para>
    /// <para>
    /// A timer that sweeps every arena does not belong here. Set one timer per arena instead, which is what
    /// gets the work onto more than one thread; a single callback looping over <see cref="IArenaManager.Arenas"/>
    /// would run on whichever arena's thread happened to own it and would touch all the others from there.
    /// </para>
    /// <para>
    /// There is no ASSS equivalent. ASSS has only its mainloop timers.
    /// </para>
    /// </remarks>
    public interface IArenaTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer that runs on the thread that owns an arena.
        /// </summary>
        /// <remarks>
        /// The timer starts running shortly after this returns rather than immediately, because setting it
        /// is itself handed to the arena's thread. <paramref name="initialDelay"/> is measured from when the
        /// arena's thread picks it up, so a timer set from elsewhere is not late, just started late.
        /// <para>
        /// Returning <see langword="false"/> from <paramref name="callback"/> stops the timer, as with
        /// <see cref="IMainloopTimer"/>.
        /// </para>
        /// <para>
        /// Timers for an arena are dropped when that arena is destroyed, so a module that sets one per arena
        /// is not required to clear it to stay correct. Clearing it in <see cref="ArenaAction.Destroy"/> is
        /// still worth doing, since it stops the timer at a defined point rather than an incidental one.
        /// </para>
        /// </remarks>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="arena">The arena whose thread should run the timer.</param>
        /// <param name="callback">The delegate that the timer should invoke.</param>
        /// <param name="initialDelay">How long to wait for the first call (in milliseconds).</param>
        /// <param name="interval">How long to wait between calls (in milliseconds).</param>
        /// <param name="state">A closure argument that will get passed to the timer <paramref name="callback"/>.</param>
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
        /// The timer is stopped on the arena's own thread, so this does not block and the timer is not
        /// guaranteed to have stopped by the time it returns. It will not run again after the call that may
        /// already be in progress, and a callback that is already running is allowed to finish.
        /// </remarks>
        /// <typeparam name="TState">The type of parameter the <paramref name="callback"/> accepts.</typeparam>
        /// <param name="arena">The arena to clear timer(s) for.</param>
        /// <param name="callback">The delegate to clear timer(s) for.</param>
        /// <param name="key">
        /// Timers that match this key will be removed.
        /// Use <see langword="null"/> to clear all timers associated with <paramref name="callback"/>, regardless of key.
        /// </param>
        void ClearTimer<TState>(
            Arena arena,
            TimerDelegate<TState> callback,
            object? key);
    }
}
