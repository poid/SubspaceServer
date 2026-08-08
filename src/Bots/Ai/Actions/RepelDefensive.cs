using QS.Physics.Legacy.Structs; // ShipState
using SS.Packets.Game;          // WeaponCodes

namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Survival repel. Replay analysis of top SVS players (102 repel uses across league games) shows the
    /// repel is a DEFENSIVE move, not offence: they pop it when their energy is LOW (~36% median vs ~77%
    /// normally) AND an enemy is closer than usual (~10-15 tiles, often with the enemy's gun on them, while
    /// moving fast to escape) — to shove the enemy and their incoming fire away and buy time to recover. So
    /// this scores high (survival, above normal offence) under exactly that pressure and repels-and-flees.
    /// </summary>
    public sealed class RepelDefensive : IBotAction
    {
        private const long PressureRange1000 = 12L * 16 * 1000;   // enemy this close + low energy = pressure
        private const long PressureRangeSq = PressureRange1000 * PressureRange1000;
        private const long RepelFireRange1000 = 8L * 16 * 1000;   // only worth a repel when the enemy is in its push radius
        private const long RepelFireRangeSq = RepelFireRange1000 * RepelFireRange1000;
        private const float LowEnergyFraction = 0.45f;            // repel when below this share of max energy
        private const int ThrustAlignTolerance = 7;

        public string Name => "RepelDefensive";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
        {
            if (!p.HasEnemy || p.Enemy.DistanceSquared > PressureRangeSq)
                return 0f;

            ref readonly ShipState self = ref BotSkills.Self(in ctx);
            if (self.RepelsCurrent <= 0 || self.EnergyMax <= 0)
                return 0f;

            float energyFraction = self.Energy1000 / 1000f / self.EnergyMax;
            if (energyFraction > LowEnergyFraction)
                return 0f;

            // Survival: outrank normal engagement (~0.75) but stay under a point-blank incoming-weapon dodge.
            return 0.85f * profile.Caution;
        }

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            ref readonly ShipState self = ref BotSkills.Self(in ctx);
            var selfPos = BotSkills.SelfPosition(in ctx);

            // Flee: head AWAY from the enemy (routing around walls) to open distance — low energy means escape
            // beats trading shots. Thrust when roughly pointed away.
            byte away = BotSkills.AvoidWalls(in ctx, BotSkills.FaceRotation(p.Enemy.Position, selfPos));
            bool onHeading = BotSkills.IsFacing(self.Rotation, away, ThrustAlignTolerance);

            // Pop a repel only when the enemy is inside its push radius and the fire is off cooldown — the
            // repel is limited inventory and the shove + our flee change the situation, so it's self-limiting.
            WeaponCodes fire = p.Enemy.DistanceSquared < RepelFireRangeSq && BotSkills.WeaponReady(in ctx)
                ? WeaponCodes.Repel : WeaponCodes.Null;

            return BotSkills.Steer(away, thrust: onHeading, fire: fire);
        }
    }
}
