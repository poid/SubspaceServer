using QS.Physics.Legacy;    // Point
using SS.Bots.Navigation;   // NavGeometry, TileCoord

namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Carry the ball to the enemy goal and shoot it in. The goal tiles sit inside a walled box with only
    /// 1-2 tile gaps, and our hull can't fit a 1-tile gap — but the BALL can. So we don't drive at the goal
    /// centre (that just rams the box wall); we navigate to a FIRING POSITION with a clear line through a gap
    /// to the goal and shoot the ball through it. Scores under <see cref="Objective.Deliver"/>.
    /// </summary>
    /// <remarks>
    /// TODO(iterate): don't drive into a group of enemies — reroute or stall until teammates thin them out;
    /// pass to an open teammate when the pass is safe or the carry timer is low; a rocket for a long shot.
    /// </remarks>
    public sealed class CarryToGoal : IBotAction
    {
        private const long ShootRange1000 = 10L * 16 * 1000; // ~10 tiles: close enough to shoot it in
        private const long ShootRangeSq = ShootRange1000 * ShootRange1000;
        private const int ShootRangeTiles = 10;              // how far out to look for a clear firing position
        private const int CarryTimerShootTicks = 30;         // shoot with < ~3 heartbeats of carry timer left

        // When true, the bot shoots the ball at the goal from a clear firing position, or when the carry timer
        // is about to expire (a forward shot beats the sim's backward auto-throw).
        private const bool AllowKick = true;

        public string Name => "CarryToGoal";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => p.Objective == Objective.Deliver && ctx.Ball.HasGoal ? 1.0f : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            Point self = BotSkills.SelfPosition(in ctx);
            Point goal = ctx.Ball.Goal;

            // Shoot when we have a clear line to the goal (through a gap) within range — the ball fits a gap our
            // hull can't, so we score from OUTSIDE the box. Also shoot if the carry timer is about to expire (a
            // forward shot beats letting the sim auto-throw the ball backward).
            bool inRange = BotSkills.DistanceSquared(self, goal) <= ShootRangeSq
                        && ctx.World.HasLineOfSight(ctx.SelfId, goal);
            bool timerLow = ctx.Ball.HoldTicksLeft > 0 && ctx.Ball.HoldTicksLeft < CarryTimerShootTicks;

            if (AllowKick && (inRange || timerLow))
            {
                return BotSkills.Steer(BotSkills.FaceRotation(self, goal), thrust: false)
                    with { BallAction = BallIntent.Fire, BallTarget = goal };
            }

            // Otherwise line up a shot: go to a firing position — a walkable spot within range that has a clear
            // line (through a gap) to the goal — rather than driving at the goal centre behind the box wall.
            TileCoord selfTile = NavGeometry.ToTile(self.X / 1000, self.Y / 1000);
            TileCoord goalTile = NavGeometry.ToTile(goal.X / 1000, goal.Y / 1000);
            if (ctx.Navigation.TryFindFiringPosition(selfTile, goalTile, ShootRangeTiles, ctx.CurrentTick, out TileCoord firingTile))
            {
                Point firingPoint = new(NavGeometry.TileCenterWorldX(firingTile) * 1000, NavGeometry.TileCenterWorldY(firingTile) * 1000);
                return BotSkills.Navigate(in ctx, firingPoint);
            }

            // No clear line found — approach the goal and keep looking.
            return BotSkills.Navigate(in ctx, goal);
        }
    }
}
