namespace SS.Core.ComponentCallbacks
{
    // As for BallGoalCallback: BasicStats alone, under the per-player lock that Stats takes.
    [CallbackHelper]
    [ShardSafe]
    public static partial class BallPickupCallback
    {
        public delegate void BallPickupDelegate(Arena arena, Player player, byte ballId);
    }
}
