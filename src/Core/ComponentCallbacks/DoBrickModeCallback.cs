using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using static SS.Core.Modules.Bricks;

namespace SS.Core.ComponentCallbacks
{
    // Fired from the Brick packet handler, which runs on the player's arena thread when
    // Net:HandlePacketsOnArenaThread is on. Bricks is the only zone-wide handler and it reads map tiles,
    // which are immutable once loaded, and writes only the list it is handed.
    [CallbackHelper]
    [ShardSafe]
    public static partial class DoBrickModeCallback
    {
        /// <summary>
        /// Delegate for deciding brick locations when a player requests a brick drop.
        /// </summary>
        /// <param name="player">The player that made the request.</param>
        /// <param name="brickMode">The mode. An implementation should only add bricks to <paramref name="bricks"/> if the mode matches.</param>
        /// <param name="x">The x-coordinate from the request.</param>
        /// <param name="y">The y-coordinate from the request.</param>
        /// <param name="length">The maximum # of tiles a brick should span.</param>
        /// <param name="bricks">The collection to add brick locations to.</param>
        public delegate void DoBrickModeDelegate(Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks);
    }
}
