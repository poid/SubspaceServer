using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    // Fired from the ball handlers, which run on the player's arena thread when
    // Net:HandlePacketsOnArenaThread is on. BasicStats is the only zone-wide handler and it only
    // adjusts stats, which Stats does under the per-player lock.
    [CallbackHelper]
    [ShardSafe]
    public static partial class BallGoalCallback
    {
        public delegate void BallGoalDelegate(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates);
    }
}
