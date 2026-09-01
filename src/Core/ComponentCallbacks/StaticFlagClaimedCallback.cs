namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="StaticFlagClaimedDelegate"/> callback.
    /// </summary>
    // Fired from the turf flag TouchFlag handler, which runs on the player's arena thread when
    // Net:HandlePacketsOnArenaThread is on. BasicStats is the only zone-wide handler and it only
    // increments a stat, which Stats does under the per-player lock.
    [CallbackHelper]
    [ShardSafe]
    public static partial class StaticFlagClaimedCallback
    {
        /// <summary>
        /// Delegate for when a flag is claimed in a static flag game.
        /// </summary>
        /// <param name="arena">The arena the flag was claimed in.</param>
        /// <param name="player">The player that claimed the flag.</param>
        /// <param name="flagId">Id of the flag that was claimed.</param>
        /// <param name="oldFreq">The team that previously owned the flag.</param>
        /// <param name="newFreq">The team that took ownership of the flag.</param>
        public delegate void StaticFlagClaimedDelegate(Arena arena, Player player, short flagId, short oldFreq, short newFreq);
    }
}
