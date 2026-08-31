using SS.Core.ComponentInterfaces;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// Assertions that state where a piece of code is allowed to run, and what it must be holding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost all game logic runs on the mainloop thread, and touching a <see cref="Player"/> requires
    /// the global player lock. Both are conventions that nothing currently enforces, so a caller that
    /// gets it wrong produces a rare, hard to attribute failure rather than an error.
    /// </para>
    /// <para>
    /// These are written before the server is made concurrent, deliberately. Added now, every one of
    /// them is either immediately true, in which case it documents and then guards the contract, or
    /// immediately false, in which case it has found something already broken. Added afterwards, every
    /// failure is ambiguous: was it always wrong, or did the change to threading break it?
    /// </para>
    /// <para>
    /// Everything here compiles away entirely outside debug builds, so an assertion costs nothing in
    /// release and can be placed wherever it documents something worth stating.
    /// </para>
    /// </remarks>
    public static class ThreadingAssert
    {
        /// <summary>
        /// Asserts that the caller is on the mainloop thread.
        /// </summary>
        /// <param name="mainloop">The mainloop service.</param>
        /// <param name="caller">Filled in by the compiler, to name what was called off the mainloop.</param>
        [Conditional("DEBUG")]
        public static void OnMainloop(IMainloop? mainloop, [CallerMemberName] string? caller = null)
        {
            // Nothing to check before the mainloop is running: module loading legitimately runs on the
            // thread that later becomes it, and IsMainloop is false until RunLoop is entered.
            if (mainloop is null || !HasMainloopStarted(mainloop))
                return;

            Debug.Assert(
                mainloop.IsMainloop,
                $"{caller} must run on the mainloop thread, but ran on '{Thread.CurrentThread.Name ?? "unnamed"}'.");
        }

        /// <summary>
        /// Asserts that the caller holds the global player lock, in any of its forms.
        /// </summary>
        /// <param name="playerData">The player data service.</param>
        /// <param name="caller">Filled in by the compiler, to name what was missing the lock.</param>
        [Conditional("DEBUG")]
        public static void PlayerLockHeld(IPlayerData? playerData, [CallerMemberName] string? caller = null)
        {
            if (playerData is null)
                return;

            Debug.Assert(
                playerData.IsReadLockHeld || playerData.IsWriteLockHeld || playerData.IsUpgradeableReadLockHeld,
                $"{caller} requires the global player lock, and the calling thread does not hold it.");
        }

        /// <summary>
        /// Asserts that the caller holds the global player lock for writing.
        /// </summary>
        /// <param name="playerData">The player data service.</param>
        /// <param name="caller">Filled in by the compiler, to name what was missing the lock.</param>
        [Conditional("DEBUG")]
        public static void PlayerWriteLockHeld(IPlayerData? playerData, [CallerMemberName] string? caller = null)
        {
            if (playerData is null)
                return;

            Debug.Assert(
                playerData.IsWriteLockHeld,
                $"{caller} changes shared player state and requires the global player write lock, and the calling thread does not hold it.");
        }

        /// <summary>
        /// Asserts that a callback about to reach handlers registered above an arena is being fired from a
        /// thread those handlers can tolerate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called from the generated <c>Fire</c> methods, at the point where firing on an arena walks up to
        /// the root broker. Handlers registered there are zone-wide infrastructure -- logging, stats,
        /// replay, security -- and were written when the mainloop was the only thread that could reach
        /// them. An arena thread reaching them is a silent hazard, which this turns into a failed
        /// assertion naming the callback.
        /// </para>
        /// <para>
        /// Deliberately not an assertion about arena threads in general. An arena firing a callback that
        /// nothing registered zone-wide is fine, and so is one whose zone-wide handlers are thread-safe;
        /// the first is why this takes <paramref name="hasZoneWideHandlers"/> rather than checking the
        /// thread alone, and the second is what <see cref="ShardSafeAttribute"/> is for.
        /// </para>
        /// </remarks>
        /// <param name="hasZoneWideHandlers">Whether any handler is actually registered above the arena.</param>
        /// <param name="callbackName">The callback being fired, to name it in the failure.</param>
        [Conditional("DEBUG")]
        public static void CallbackReachesZoneWide(bool hasZoneWideHandlers, string callbackName)
        {
            if (!hasZoneWideHandlers)
                return;

            Debug.Assert(
                !Modules.ArenaLoop.IsArenaWorkerThread,
                $"{callbackName} was fired from an arena thread and reaches handlers registered zone-wide, which are not written to run there. " +
                $"Fire it on the mainloop, or mark the callback [ShardSafe] if every handler it can reach really is thread-safe.");
        }

        /// <summary>
        /// Whether the mainloop has started running, so that there is a thread to compare against.
        /// </summary>
        /// <remarks>
        /// <see cref="IMainloop.IsMainloop"/> is false both when called off the mainloop and before the
        /// mainloop exists, and only the first of those is a problem worth reporting.
        /// </remarks>
        private static bool HasMainloopStarted(IMainloop mainloop)
        {
            // Only the concrete module can say; another implementation is assumed to be running, since
            // the alternative is silently skipping every assertion against it.
            return mainloop is not Modules.Mainloop concrete || concrete.HasStarted;
        }
    }
}
