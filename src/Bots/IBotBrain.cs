using QS.Physics.Legacy;
using SS.Bots.Navigation;
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
    /// Everything a brain needs to decide for one bot on one tick. Passed by <c>in</c> to avoid copies.
    /// </summary>
    public readonly struct BotContext
    {
        /// <summary>The arena's authoritative simulation. Read all ship state via <c>World.Canonical.Ships</c>.</summary>
        public required ReplayController World { get; init; }

        /// <summary>The arena's navigation service (global path planner, line-of-sight, regions).</summary>
        public required INavigation Navigation { get; init; }

        /// <summary>This bot's stable ship slot within <see cref="World"/>.</summary>
        public required int ShipSlot { get; init; }

        /// <summary>The simulation tick this decision is for.</summary>
        public required uint CurrentTick { get; init; }
    }

    /// <summary>
    /// A pluggable bot brain. One instance per bot.
    /// </summary>
    /// <remarks>
    /// <see cref="Think"/> is called once per bot tick, on the mainloop thread. The brain has full
    /// knowledge of every ship in the world — real players and other bots — and the navigation
    /// service for pathing/line-of-sight. It returns an intent; <see cref="BotsModule"/> translates
    /// that into physics commands. All four gameplay modes are brains that differ mainly in goal
    /// selection; they share the same navigation and steering machinery. The brain must not mutate
    /// the world or block the thread.
    /// </remarks>
    public interface IBotBrain
    {
        BotDecision Think(in BotContext context);
    }
}
