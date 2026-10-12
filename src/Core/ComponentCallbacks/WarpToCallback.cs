namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Fired when a server-initiated warp (<see cref="ComponentInterfaces.IGame.WarpTo(ITarget, short, short)"/>)
    /// is issued to a player, for EVERY targeted player and BEFORE the client-feature filter that decides who
    /// receives the <c>0x32</c> warp packet. This lets a module that drives a server-side player with no real
    /// client — e.g. a bot, whose position is simulation-driven — apply the warp to its own model, since the
    /// network packet goes nowhere for such a player.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="WarpCallback"/>, which fires when the server DETECTS a real client's position
    /// jump in an incoming position packet. The coordinates are TILE coordinates (0-1023), matching the warp
    /// packet.
    /// </remarks>
    [CallbackHelper]
    public static partial class WarpToCallback
    {
        public delegate void WarpToDelegate(Player player, short x, short y);
    }
}
