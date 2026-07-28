using QS.Physics.Legacy;
using SS.Packets.Game;

namespace SS.Bots
{
    /// <summary>
    /// A single tick's worth of intent produced by a bot's AI.
    /// </summary>
    /// <param name="ThrustOn">Whether the engine is firing this tick.</param>
    /// <param name="AfterburnerOn">Whether the afterburner is engaged this tick.</param>
    /// <param name="Rotation">Target facing on Continuum's 40-position ring (0 = 12:00, 10 = 3:00, ...).</param>
    /// <param name="Fire"><see cref="WeaponCodes.Null"/> to hold fire; otherwise the weapon to fire this tick.</param>
    /// <param name="FireLevel">Weapon level [0-3] when <paramref name="Fire"/> is set.</param>
    public readonly record struct BotDecision(
        bool ThrustOn,
        bool AfterburnerOn,
        byte Rotation,
        WeaponCodes Fire,
        byte FireLevel);

    /// <summary>
    /// A pluggable bot brain. One instance per bot.
    /// </summary>
    /// <remarks>
    /// <see cref="Think"/> is called once per bot tick, on the mainloop thread, with the
    /// arena's authoritative simulation. The brain has full knowledge of every ship in the
    /// world — real players and other bots alike — by reading <c>world.Canonical.Ships</c>.
    /// It returns an intent; <see cref="BotsModule"/> translates that into physics commands.
    /// The brain must not mutate the world or block the thread.
    /// </remarks>
    public interface IBotBrain
    {
        /// <param name="world">The per-arena physics simulation. Read state via <c>world.Canonical</c>.</param>
        /// <param name="myShipSlot">This bot's stable ship slot within <paramref name="world"/>.</param>
        BotDecision Think(ReplayController world, int myShipSlot);
    }
}
