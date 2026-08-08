using QS.Networking.Protocols.Common;
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
        byte FireLevel)
    {
        /// <summary>Whether to thrust in REVERSE this tick — brake by thrusting OPPOSITE the facing (used to
        /// bleed speed before a turn/wall without spinning around). If both this and <see cref="ThrustOn"/>
        /// are set, the engine gives reverse precedence.</summary>
        public bool ThrustReverse { get; init; }

        /// <summary>Ball interaction to perform this tick (Mode 3). Movement fields above still apply.</summary>
        public BallIntent BallAction { get; init; }

        /// <summary>Target for a <see cref="BallIntent.Fire"/> (Coordinates1000): the goal, a teammate lead, or open space.</summary>
        public Point BallTarget { get; init; }
    }

    /// <summary>What a bot wants to do with the soccer ball this tick. Executed by the module via <c>IBalls</c>.</summary>
    public enum BallIntent : byte
    {
        None,
        Grab,  // pick up a loose ball we're adjacent to
        Fire,  // kick/pass/shoot the carried ball toward BallTarget
    }

    /// <summary>
    /// Everything a brain needs to decide for one bot on one tick. Passed by <c>in</c> to avoid copies.
    /// </summary>
    public readonly struct BotContext
    {
        /// <summary>The arena's authoritative simulation. Read all ship state via <c>World.Canonical.Ships</c>.</summary>
        public required ReplayController World { get; init; }

        /// <summary>The arena's navigation service (global path planner, line-of-sight, regions).</summary>
        public required INavigation Navigation { get; init; }

        /// <summary>This bot's private path-follower (cached A* waypoints) for goal-directed movement.</summary>
        public NavAgent Agent { get; init; }

        /// <summary>This bot's stable ship slot within <see cref="World"/>.</summary>
        public required int ShipSlot { get; init; }

        /// <summary>This bot's network ID — pass to the ID-based query/aim methods on <see cref="World"/>.</summary>
        public required ID SelfId { get; init; }

        /// <summary>The simulation tick this decision is for.</summary>
        public required uint CurrentTick { get; init; }

        /// <summary>The soccer-ball situation for this bot (Mode 3). Default = not in play; ball brains read it.</summary>
        public Ai.BallView Ball { get; init; }
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
