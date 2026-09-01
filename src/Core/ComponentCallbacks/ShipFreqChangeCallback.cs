namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BeforeShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked right before a player's ship or freq (team) is changed.
        /// </summary>
        /// <remarks>
        /// This is executed synchronously before a player's ship or freq is changed.
        /// In other words, before <see cref="Player.Ship"/> and <see cref="Player.Freq"/> are changed, 
        /// and before the <see cref="Packets.Game.S2CPacketType.ShipChange"/> packet is sent to anyone.
        /// Handlers must not perform another ship or freq change as that would be recursive and will lead to unexpected, undefined behavior.
        /// </remarks>
        /// <param name="player">The player whose ship is changing.</param>
        /// <param name="newShip">The type of ship that the player changing to.</param>
        /// <param name="oldShip">The type of ship that the player changing from.</param>
        /// <param name="newFreq">The player's new team.</param>
        /// <param name="oldFreq">The player's old team.</param>
        public delegate void BeforeShipFreqChangeDelegate(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);
    }

    /// <summary>
    /// Helper class for the <see cref="PreShipFreqChangeCallback"/> callback.
    /// </summary>
    // Fired from the ship, freq and position packet handlers, which run on the player's arena thread when
    // Net:HandlePacketsOnArenaThread is on. FreqManager is the only zone-wide handler, and it takes the
    // global player lock for the whole of the work it does.
    [CallbackHelper]
    [ShardSafe]
    public static partial class PreShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship or freq (team) is changed.
        /// </summary>
        /// <remarks>
        /// This is executed synchronously after a player's ship or freq is changed.
        /// Handlers must not perform another ship or freq change as that would be recursive and will lead to unexpected, undefined behavior.
        /// </remarks>
        /// <param name="player">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The player's new team.</param>
        /// <param name="oldFreq">The player's old team.</param>
        public delegate void PreShipFreqChangeDelegate(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);
    }

    /// <summary>
    /// Helper class for the <see cref="ShipFreqChangeDelegate"/> callback.
    /// </summary>
    // As for PreShipFreqChangeCallback. The zone-wide handlers are FreqManager, which takes the global
    // player lock, and Balls and CarryFlags, which drop whatever the player was carrying in that player's
    // own arena, under that arena's lock.
    [CallbackHelper]
    [ShardSafe]
    public static partial class ShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship or freq (team) is changed.
        /// </summary>
        /// <remarks>
        /// This is executed asynchronously, after a player's ship or freq is changed.
        /// </remarks>
        /// <param name="player">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The player's new team.</param>
        /// <param name="oldFreq">The player's old team.</param>
        public delegate void ShipFreqChangeDelegate(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);
    }
}
