using SS.Core;
using SS.Core.ComponentInterfaces;
using System.Reflection;
using System.Text;

namespace SS.PowerBall.Threading
{
    /// <summary>
    /// Runs a PowerBall module's work for an arena on the thread that owns that arena.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every PowerBall module keeps per-arena game state, and every piece of it has a single writer: the arena's
    /// own thread (see <see cref="IArenaLoop"/>). Work reaches a module from three places, and this is how each one
    /// gets there:
    /// </para>
    /// <list type="bullet">
    /// <item>Timers are set with <see cref="ArenaTimerSlot{TState}"/>, which runs them on the arena's thread.</item>
    /// <item>
    /// Callbacks can fire on either thread: the ball, kill and ship/freq ones arrive on the arena's thread when
    /// <c>Net:HandlePacketsOnArenaThread</c> is on and on the mainloop otherwise, and the player, arena and game
    /// timer ones always arrive on the mainloop. A handler that is not on the arena's thread hands itself over with
    /// one of the <c>Defer</c> overloads and returns.
    /// </item>
    /// <item>Commands always arrive on the mainloop. <see cref="AddCommand"/> registers them so that they run on the arena's thread.</item>
    /// </list>
    /// <para>
    /// Anything handed over runs after a delay, so it is re-checked when it arrives: a player argument that is no
    /// longer in the arena drops the work, because that player's state now belongs to another thread.
    /// </para>
    /// <para>
    /// A module that is detached from an arena calls <see cref="Drain"/> after unregistering everything, so nothing
    /// it handed over is still to run once the arena's data is reset.
    /// </para>
    /// </remarks>
    internal sealed class ArenaWork
    {
        private readonly IArenaLoop _arenaLoop;
        private readonly ICommandManager _commandManager;
        private readonly ILogManager _logManager;
        private readonly string _moduleName;

        // Command handlers wrapped by AddCommand, so that RemoveCommand can pass the same instance back. Only touched
        // on the mainloop, which is where modules are attached and detached.
        private readonly Dictionary<CommandDelegate, CommandDelegate> _commandWrappers = [];

        public ArenaWork(IArenaLoop arenaLoop, ICommandManager commandManager, ILogManager logManager, string moduleName)
        {
            _arenaLoop = arenaLoop ?? throw new ArgumentNullException(nameof(arenaLoop));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _moduleName = moduleName ?? throw new ArgumentNullException(nameof(moduleName));
        }

        /// <summary>
        /// Gets whether the calling thread owns <paramref name="arena"/>.
        /// </summary>
        public bool IsCurrent(Arena arena) => _arenaLoop.IsArenaThread(arena);

        /// <summary>
        /// Runs <paramref name="work"/> now if the calling thread owns <paramref name="arena"/>, or queues it to that
        /// thread otherwise.
        /// </summary>
        public void Run<TState>(Arena arena, Action<TState> work, TState state)
        {
            if (_arenaLoop.IsArenaThread(arena))
                work(state);
            else
                Queue(arena, work, state);
        }

        /// <summary>
        /// Queues <paramref name="work"/> to the thread that owns <paramref name="arena"/>, even when the caller is
        /// already on it.
        /// </summary>
        public void Queue<TState>(Arena arena, Action<TState> work, TState state)
        {
            if (!_arenaLoop.QueueArenaWorkItem(arena, work, state))
                _logManager.LogA(LogLevel.Warn, _moduleName, arena, "Unable to queue work to the arena's thread.");
        }

        /// <summary>
        /// Blocks until everything queued for <paramref name="arena"/> has run. Call from the mainloop when detaching.
        /// </summary>
        public void Drain(Arena arena) => _arenaLoop.WaitForArenaWorkItemDrain(arena);

        #region Defer

        // Each Defer returns false when the caller is already on the arena's thread and should carry on, and true when
        // the call has been queued to it and the caller should return.
        //
        // Defer drops the queued call if a player argument has left the arena by the time it runs, since that player's
        // state then belongs to another thread. DeferUnchecked runs it regardless, for handlers that only touch this
        // arena's own state, such as one reacting to the player leaving, which by then has always happened.
        //
        // Call sites check IsCurrent first (if (!IsCurrent(arena) && Defer(arena, Handler, ...))), so that the handler
        // delegate is only created when the call is actually handed over, not on every call already on the arena's thread.

        public bool Defer(Arena arena, Action<Arena> handler)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(arena, static s => s.Handler(s.Arena), (Handler: handler, Arena: arena));
            return true;
        }

        public bool Defer<T1>(Arena arena, Action<Arena, T1> handler, T1 arg1)
            => Defer(arena, handler, arg1, check: true);

        public bool DeferUnchecked<T1>(Arena arena, Action<Arena, T1> handler, T1 arg1)
            => Defer(arena, handler, arg1, check: false);

        public bool Defer<T1, T2>(Arena arena, Action<Arena, T1, T2> handler, T1 arg1, T2 arg2)
            => Defer(arena, handler, arg1, arg2, check: true);

        public bool DeferUnchecked<T1, T2>(Arena arena, Action<Arena, T1, T2> handler, T1 arg1, T2 arg2)
            => Defer(arena, handler, arg1, arg2, check: false);

        public bool Defer<T1, T2, T3>(Arena arena, Action<Arena, T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3)
            => Defer(arena, handler, arg1, arg2, arg3, check: true);

        public bool DeferUnchecked<T1, T2, T3>(Arena arena, Action<Arena, T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3)
            => Defer(arena, handler, arg1, arg2, arg3, check: false);

        public bool Defer<T1, T2, T3, T4>(Arena arena, Action<Arena, T1, T2, T3, T4> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
            => Defer(arena, handler, arg1, arg2, arg3, arg4, check: true);

        public bool DeferUnchecked<T1, T2, T3, T4>(Arena arena, Action<Arena, T1, T2, T3, T4> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
            => Defer(arena, handler, arg1, arg2, arg3, arg4, check: false);

        public bool Defer<T1, T2, T3, T4, T5>(Arena arena, Action<Arena, T1, T2, T3, T4, T5> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
            => Defer(arena, handler, arg1, arg2, arg3, arg4, arg5, check: true);

        public bool DeferUnchecked<T1, T2, T3, T4, T5>(Arena arena, Action<Arena, T1, T2, T3, T4, T5> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
            => Defer(arena, handler, arg1, arg2, arg3, arg4, arg5, check: false);

        private bool Defer<T1>(Arena arena, Action<Arena, T1> handler, T1 arg1, bool check)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(
                arena,
                static s =>
                {
                    if (!s.Check || IsStillIn(s.Arena, s.Arg1))
                        s.Handler(s.Arena, s.Arg1);
                },
                (Handler: handler, Arena: arena, Arg1: arg1, Check: check));
            return true;
        }

        private bool Defer<T1, T2>(Arena arena, Action<Arena, T1, T2> handler, T1 arg1, T2 arg2, bool check)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(
                arena,
                static s =>
                {
                    if (!s.Check || (IsStillIn(s.Arena, s.Arg1) && IsStillIn(s.Arena, s.Arg2)))
                        s.Handler(s.Arena, s.Arg1, s.Arg2);
                },
                (Handler: handler, Arena: arena, Arg1: arg1, Arg2: arg2, Check: check));
            return true;
        }

        private bool Defer<T1, T2, T3>(Arena arena, Action<Arena, T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3, bool check)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(
                arena,
                static s =>
                {
                    if (!s.Check || (IsStillIn(s.Arena, s.Arg1) && IsStillIn(s.Arena, s.Arg2) && IsStillIn(s.Arena, s.Arg3)))
                        s.Handler(s.Arena, s.Arg1, s.Arg2, s.Arg3);
                },
                (Handler: handler, Arena: arena, Arg1: arg1, Arg2: arg2, Arg3: arg3, Check: check));
            return true;
        }

        private bool Defer<T1, T2, T3, T4>(Arena arena, Action<Arena, T1, T2, T3, T4> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4, bool check)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(
                arena,
                static s =>
                {
                    if (!s.Check || (IsStillIn(s.Arena, s.Arg1) && IsStillIn(s.Arena, s.Arg2) && IsStillIn(s.Arena, s.Arg3) && IsStillIn(s.Arena, s.Arg4)))
                        s.Handler(s.Arena, s.Arg1, s.Arg2, s.Arg3, s.Arg4);
                },
                (Handler: handler, Arena: arena, Arg1: arg1, Arg2: arg2, Arg3: arg3, Arg4: arg4, Check: check));
            return true;
        }

        private bool Defer<T1, T2, T3, T4, T5>(Arena arena, Action<Arena, T1, T2, T3, T4, T5> handler, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, bool check)
        {
            if (_arenaLoop.IsArenaThread(arena))
                return false;

            Queue(
                arena,
                static s =>
                {
                    if (!s.Check || (IsStillIn(s.Arena, s.Arg1) && IsStillIn(s.Arena, s.Arg2) && IsStillIn(s.Arena, s.Arg3) && IsStillIn(s.Arena, s.Arg4) && IsStillIn(s.Arena, s.Arg5)))
                        s.Handler(s.Arena, s.Arg1, s.Arg2, s.Arg3, s.Arg4, s.Arg5);
                },
                (Handler: handler, Arena: arena, Arg1: arg1, Arg2: arg2, Arg3: arg3, Arg4: arg4, Arg5: arg5, Check: check));
            return true;
        }

        private static bool IsStillIn<T>(Arena arena, T arg) => arg is not Player player || player.Arena == arena;

        #endregion

        #region Commands

        /// <summary>
        /// Registers an arena command whose handler runs on the arena's thread. Use <see cref="RemoveCommand"/> to remove it.
        /// </summary>
        /// <remarks>
        /// The help text comes from the handler's <see cref="CommandHelpAttribute"/>, as it would if the handler were
        /// registered directly; the wrapper that is actually registered has no attribute of its own.
        /// </remarks>
        public void AddCommand(string commandName, CommandDelegate handler, Arena arena)
        {
            _commandManager.AddCommand(commandName, GetCommandWrapper(handler), arena, GetHelpText(commandName, handler));
        }

        public void RemoveCommand(string commandName, CommandDelegate handler, Arena arena)
        {
            _commandManager.RemoveCommand(commandName, GetCommandWrapper(handler), arena);
        }

        private CommandDelegate GetCommandWrapper(CommandDelegate handler)
        {
            if (!_commandWrappers.TryGetValue(handler, out CommandDelegate? wrapper))
            {
                wrapper = (commandName, parameters, player, target) => DispatchCommand(handler, commandName, parameters, player, target);
                _commandWrappers.Add(handler, wrapper);
            }

            return wrapper;
        }

        private void DispatchCommand(CommandDelegate handler, ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (_arenaLoop.IsArenaThread(arena))
            {
                handler(commandName, parameters, player, target);
                return;
            }

            Queue(arena, static work => work.Invoke(), new CommandWork(handler, arena, commandName.ToString(), parameters.ToString(), player, target));
        }

        /// <summary>
        /// Builds the help text the way <see cref="ICommandManager"/> does for a handler registered without any.
        /// </summary>
        private static string? GetHelpText(string commandName, CommandDelegate handler)
        {
            foreach (CommandHelpAttribute helpAttr in handler.GetMethodInfo().GetCustomAttributes<CommandHelpAttribute>())
            {
                if (helpAttr.Command is not null && !string.Equals(helpAttr.Command, commandName, StringComparison.OrdinalIgnoreCase))
                    continue;

                StringBuilder sb = new();
                sb.AppendLine($"Targets: {helpAttr.Targets:F}");
                sb.AppendLine($"Args: {helpAttr.Args ?? "None"}");

                if (!string.IsNullOrWhiteSpace(helpAttr.Description))
                    sb.Append(helpAttr.Description);

                return sb.ToString();
            }

            return null;
        }

        private sealed class CommandWork(CommandDelegate handler, Arena arena, string commandName, string parameters, Player player, ITarget target)
        {
            public void Invoke()
            {
                // The command was typed in this arena. If the player, or the player it was sent to, has since left, it
                // has nothing left to act on here, and that player's state now belongs to another arena's thread.
                if (player.Arena != arena)
                    return;

                if (target is IPlayerTarget playerTarget && playerTarget.Player.Arena != arena)
                    return;

                handler(commandName, parameters, player, target);
            }
        }

        #endregion

        #region Synchronization context

        /// <summary>
        /// Starts <paramref name="action"/> so that its <see langword="await"/> continuations come back to the thread
        /// that owns <paramref name="arena"/>.
        /// </summary>
        /// <remarks>
        /// Arena threads have no <see cref="SynchronizationContext"/> of their own, so an <see langword="await"/> on
        /// one would otherwise resume on the thread pool. Awaits that should not come back (database calls inside the
        /// repository) use <c>ConfigureAwait(false)</c> as usual.
        /// </remarks>
        public Task RunAsync(Arena arena, Func<Task> action)
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ArenaSynchronizationContext(this, arena));

            try
            {
                return action();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private sealed class ArenaSynchronizationContext(ArenaWork owner, Arena arena) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state)
            {
                owner.Queue(arena, static s => s.Context.Invoke(s.Callback, s.State), (Context: this, Callback: d, State: state));
            }

            public override void Send(SendOrPostCallback d, object? state)
            {
                throw new NotSupportedException("Synchronous calls to an arena's thread are not supported.");
            }

            public override SynchronizationContext CreateCopy() => this;

            private void Invoke(SendOrPostCallback callback, object? state)
            {
                // Installed for the duration, so that the next await in the same method comes back here too.
                SynchronizationContext? previous = Current;
                SetSynchronizationContext(this);

                try
                {
                    callback(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }

        #endregion
    }
}
