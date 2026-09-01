using SS.Packets;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player's banner is set.
    /// </summary>
    // Fired from the Banner packet handler, which runs on the player's arena thread when
    // Net:HandlePacketsOnArenaThread is on. BillingUdp is the only zone-wide handler and it does all of
    // its work, queuing the banner for upload to the biller, under its own lock.
    [CallbackHelper]
    [ShardSafe]
    public static partial class BannerSetCallback
    {
        /// <summary>
        /// Delegate for when a player's banner is set.
        /// </summary>
        /// <param name="player">The player whose banner was set.</param>
        /// <param name="banner">The banner.</param>
        /// <param name="isFromPlayer">Whether the change was initiated by the player themself.</param>
        public delegate void BannerSetDelegate(Player player, ref readonly Banner banner, bool isFromPlayer);
    }
}
