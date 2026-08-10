using SS.Packets.Game;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on player position activities.
    /// </summary>
    public interface IPlayerPositionAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Modifies a player's position packet before it is sent out.
        /// </summary>
        /// <remarks>
        /// The adviser may edit the packet, but should not rely on this function for notification, as other advisers may be consulted. 
        /// Instead the <see cref="ComponentCallbacks.PlayerPositionPacketCallback"/> should be used for notification.
        /// </remarks>
        /// <param name="player">The player that the position packet belongs to.</param>
        /// <param name="positionPacket">The position packet.</param>
        void EditPositionPacket(Player player, ref C2S_PositionPacket positionPacket) { }

        /// <summary>
        /// Modifies a player's position packet before it is sent to a specific player.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="EditPositionPacket"/>, this is not called on the mainloop thread. It is
        /// called once per recipient, which is the busiest loop in the server, so that loop runs on the
        /// thread that owns the arena rather than on the mainloop.
        /// <para>
        /// An implementation must therefore be safe to call from a thread other than the mainloop, and
        /// safe to call for two arenas at once. In practice that means reading player and arena state
        /// rather than changing it, and changing nothing beyond the packet it is given. The lock over
        /// player data is held for the duration, so players will not be freed underneath it, but nothing
        /// else is guaranteed.
        /// </para>
        /// <para>
        /// It is also called for every recipient of every position packet, tens of thousands of times a
        /// second on a busy zone, so it should do as little as possible and must not block.
        /// </para>
        /// </remarks>
        /// <param name="player">The player that the position packet belongs to.</param>
        /// <param name="toPlayer">The player that the position packet will be sent to.</param>
        /// <param name="positionPacket">The position packet.</param>
        /// <param name="extra">The extra position data.</param>
        /// <param name="extraLength">The length of the <paramref name="extra"/> position data (0 = none, 2 = energy only, 10 = all extra position data).</param>
        /// <returns><see langword="true"/> if this function modified the packet. Otherwise, <see langword="false"/>.</returns>
        bool EditIndividualPositionPacket(Player player, Player toPlayer, ref C2S_PositionPacket positionPacket, ref ExtraPositionData extra, ref int extraLength) => false;
    }
}
