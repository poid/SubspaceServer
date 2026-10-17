using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.Threading
{
    /// <summary>
    /// One restartable timer for one arena, run on the arena's own thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin layer over <see cref="IArenaTimer"/> that makes <see cref="Stop"/> take effect at once. Clearing an
    /// <see cref="IArenaTimer"/> timer is itself queued to the arena's thread, so a timer that is already due can
    /// still run once after it has been cleared, even when it is cleared from the arena's own thread. Each
    /// <see cref="Start"/> here is a separate run with its own cancelled flag, which the callback checks first, so
    /// a stopped or replaced run never reaches the module's callback.
    /// </para>
    /// <para>
    /// Start and stop it from the arena's thread. The exceptions are attaching and detaching, on the mainloop: a
    /// timer may be started while attaching, before anything else can reach the arena's state, and stopped while
    /// detaching, which the module follows with <see cref="ArenaWork.Drain"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="TState">The type of state passed to the callback.</typeparam>
    internal sealed class ArenaTimerSlot<TState>
    {
        private readonly IArenaTimer _arenaTimer;
        private readonly Arena _arena;
        private readonly TimerDelegate<TState> _callback;
        private readonly TimerDelegate<Run> _invoke;
        private Run? _current;

        public ArenaTimerSlot(IArenaTimer arenaTimer, Arena arena, TimerDelegate<TState> callback)
        {
            _arenaTimer = arenaTimer ?? throw new ArgumentNullException(nameof(arenaTimer));
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _invoke = Invoke;
        }

        /// <summary>
        /// Gets whether the timer has been started and has neither been stopped nor finished.
        /// </summary>
        public bool IsRunning => _current is not null;

        /// <summary>
        /// Starts the timer, replacing any run already in progress.
        /// </summary>
        /// <param name="initialDelay">Milliseconds until the first call.</param>
        /// <param name="interval">Milliseconds between calls, or <see cref="Timeout.Infinite"/> for a single call.</param>
        /// <param name="state">The state to pass to the callback.</param>
        public void Start(int initialDelay, int interval, TState state)
        {
            Stop();

            Run run = new(state, interval);
            _current = run;
            _arenaTimer.SetTimer(_arena, _invoke, initialDelay, interval, run, run);
        }

        /// <summary>
        /// Stops the timer. The callback will not be called again, even if the timer was already due.
        /// </summary>
        public void Stop()
        {
            Run? run = _current;
            if (run is null)
                return;

            _current = null;
            Volatile.Write(ref run.IsCancelled, true);
            _arenaTimer.ClearTimer(_arena, _invoke, run);
        }

        private bool Invoke(Run run)
        {
            if (Volatile.Read(ref run.IsCancelled))
                return false;

            bool isContinuing = _callback(run.State);

            // The callback may have stopped or restarted the timer itself, in which case this run is already over.
            if (run.IsCancelled)
                return false;

            // A single-shot timer is over after its one call, whatever the callback returned.
            if ((!isContinuing || run.Interval == Timeout.Infinite) && _current == run)
                _current = null;

            return isContinuing;
        }

        private sealed class Run(TState state, int interval)
        {
            public readonly TState State = state;
            public readonly int Interval = interval;
            public bool IsCancelled;
        }
    }
}
