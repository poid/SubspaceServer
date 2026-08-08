using System;                   // Math
using QS.Physics.Legacy.Structs; // ShipState
using SS.Packets.Game;          // WeaponCodes

namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Fight the nearest enemy, modelled on how strong SVS players actually duel (from replay analysis of
    /// ~1M frames). A skilled ship is TWO decoupled controllers, not a pursuit:
    /// <list type="bullet">
    ///   <item><b>AIM</b> — the hull is kept continuously on the enemy's lead point. Aiming is the decider:
    ///     in the reconstructed duels the loser dies the instant their aim drifts off. So we point the gun at
    ///     the enemy every tick, independent of where we're moving.</item>
    ///   <item><b>DRIVE</b> — instead of charging, hold a RANGE band: forward-thrust when too far,
    ///     REVERSE-thrust when too close OR closing too fast (decelerate INTO the fight instead of ramming
    ///     through and orbiting forever), coast at range. Good players spend ~a third of their ticks on
    ///     reverse-thrust; that range control is what our old pure-pursuit lacked.</item>
    /// </list>
    /// Bullets are the primary weapon (the engine promotes to bouncing if the ship has it), with an occasional
    /// bomb lobbed at range. Only eligible within the engage band; longer chases are <see cref="PursueEnemy"/>.
    /// </summary>
    public sealed class EngageEnemy : IBotAction
    {
        private const long EngageRange1000 = 24L * 16 * 1000;    // eligible to fight within this
        private const long EngageRangeSq = EngageRange1000 * EngageRange1000;
        private const long PreferredRange1000 = 12L * 16 * 1000; // hold ~12 tiles from the enemy
        private const long RangeMargin1000 = 3L * 16 * 1000;     // dead-band around the preferred range (coast here)
        private const long BombMinRange1000 = 9L * 16 * 1000;    // don't bomb closer than this (own splash)
        private const long BombMinRangeSq = BombMinRange1000 * BombMinRange1000;
        private const int ApproachSpeedCap = 800;   // stop thrusting IN once closing this fast (so we can stop at range)
        private const int BrakeSpeedCap = 1000;     // reverse-thrust to brake when closing faster than this
        private const int ThrustAlignTolerance = 7; // only thrust when roughly facing where we intend to push

        public string Name => "EngageEnemy";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
        {
            if (!p.HasEnemy || p.Enemy.DistanceSquared > EngageRangeSq)
                return 0f;

            float score = 0.75f * profile.Aggression;

            // Under a ball objective the ball comes first — fighting defers unless there's nothing else to do.
            if (p.Objective is Objective.Deliver or Objective.Retrieve or Objective.Support)
                score *= 0.4f;

            return score;
        }

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            ref readonly ShipState self = ref BotSkills.Self(in ctx);
            var selfPos = BotSkills.SelfPosition(in ctx);

            // Weapon choice: bullets are primary; a bomb is an OCCASIONAL ranged lob (past our own splash where
            // it's predictive). Deterministic ~1-in-8 roll (varies by tick+slot) so the bots don't bomb in lockstep.
            uint roll = ctx.CurrentTick * 2654435761u + (uint)ctx.ShipSlot * 40503u;
            bool useBomb = (roll % 8u) == 0u && self.BombsLevelCurrent > 0 && p.Enemy.DistanceSquared > BombMinRangeSq;
            int projectileSpeed = useBomb ? self.ShipSettings.BombSpeed : self.ShipSettings.BulletSpeed;
            WeaponCodes weapon = useBomb ? WeaponCodes.Bomb : WeaponCodes.Bullet;
            byte level = useBomb
                ? (byte)Math.Max(0, self.BombsLevelCurrent - 1)
                : (byte)Math.Max(0, self.GunsLevelCurrent - 1);

            // No clear line of fire (a wall between us): reposition to get one (route around it), don't sit
            // or drive the gun into the wall. Once we have a shot, the aim+drive below takes over.
            if (!BotSkills.HasShot(in ctx, p.Enemy.Id))
            {
                byte moveRot = BotSkills.AvoidWalls(in ctx, BotSkills.FaceRotation(selfPos, p.Enemy.Position));
                bool onMove = BotSkills.IsFacing(self.Rotation, moveRot, ThrustAlignTolerance);
                return BotSkills.Steer(moveRot, thrust: onMove);
            }

            // AIM controller: keep the hull on the weapon's lead solution EVERY tick (fall back to a direct
            // shot if the lead can't be solved). This is decoupled from movement — we point where we SHOOT,
            // not where we move.
            if (!BotSkills.TryAimWeapon(in ctx, p.Enemy.Id, projectileSpeed, out byte aim, out _))
                aim = BotSkills.FaceRotation(selfPos, p.Enemy.Position);
            aim = BotSkills.Jitter(aim, profile.AimErrorPositions, ctx.CurrentTick, ctx.ShipSlot);

            bool linedUp = BotSkills.IsFacing(self.Rotation, aim, profile.FireAlignTolerance);
            WeaponCodes fire = linedUp && BotSkills.WeaponReady(in ctx) ? weapon : WeaponCodes.Null;

            // DRIVE controller: hold the preferred RANGE. `closing` is the ship's speed along the line to the
            // enemy (>0 = closing). Thrust IN when too far and not already closing fast; REVERSE-thrust (which
            // pushes opposite our facing = away from the enemy, hull stays aimed) when too close or closing too
            // fast so we decelerate into the range band instead of ramming through it; otherwise coast and fire.
            double dist = Math.Sqrt(p.Enemy.DistanceSquared);
            double dx = p.Enemy.Position.X - selfPos.X, dy = p.Enemy.Position.Y - selfPos.Y;
            double closing = dist > 0 ? (self.Velocity.X * dx + self.Velocity.Y * dy) / dist : 0;
            bool facingEnemy = BotSkills.IsFacing(self.Rotation, aim, ThrustAlignTolerance);

            bool thrust = false, reverse = false;
            if (dist > PreferredRange1000 + RangeMargin1000 && closing < ApproachSpeedCap && facingEnemy)
                thrust = true;
            else if (dist < PreferredRange1000 - RangeMargin1000 || closing > BrakeSpeedCap)
                reverse = true;
            // else: inside the range band and not overshooting — coast, aim, fire.

            return BotSkills.Steer(aim, thrust, fire, level) with { ThrustReverse = reverse };
        }
    }
}
