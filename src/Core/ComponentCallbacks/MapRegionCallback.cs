using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="MapRegionDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    [ShardSafe]
    public static partial class MapRegionCallback
    {
        // Fired from the position packet handler, which runs on the player's arena thread when
        // Net:HandlePacketsOnArenaThread is on. A region is a property of one arena's map and a handler
        // for one has no reason to touch anything zone-wide; AutoWarp, the only handler in the server,
        // hands the one thing that does -- sending the player to another arena -- to the mainloop itself.

        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> enters or exits a <see cref="MapRegion"/>.
        /// </summary>
        /// <param name="player">The player entering or exiting a region.</param>
        /// <param name="region">The region being entered or exited.</param>
        /// <param name="x">The x-coordinate of the player.</param>
        /// <param name="y">The y-coordinate of the player.</param>
        /// <param name="entering">True if the region is being entered.  False if being exited.</param>
        public delegate void MapRegionDelegate(Player player, MapRegion region, short x, short y, bool entering);
    }
}
