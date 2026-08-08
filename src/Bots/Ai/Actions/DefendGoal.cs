using System;            // Math
using QS.Physics.Legacy; // Point

namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// An enemy has the ball: take up a blocking position just goal-side of the BALL — on the line from the
    /// ball toward the goal we defend, a few tiles off the ball — so we're between the carrier and where it
    /// wants to score AND close enough to attack it. Scored below the combat actions so that once the carrier
    /// is in range, <see cref="EngageEnemy"/> takes over and kills it; this just gets us into position when
    /// the carrier is too far to fight. Scores under <see cref="Objective.Defend"/>.
    /// </summary>
    /// <remarks>
    /// TODO(iterate): only hold the block when teammates aren't already covering it — otherwise push up and
    /// hunt the carrier directly.
    /// </remarks>
    public sealed class DefendGoal : IBotAction
    {
        private const long OnStation1000 = 3L * 16 * 1000;   // ~3 tiles: coast when we're basically on station
        private const long OnStationSq = OnStation1000 * OnStation1000;
        private const long GoalSideOffset1000 = 5L * 16 * 1000; // sit ~5 tiles goal-side of the ball (near the carrier)

        public string Name => "DefendGoal";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            // Below EngageEnemy's ~0.75 so attacking the carrier wins once it's in range; this positions
            // us while it's still too far to fight.
            => p.Objective == Objective.Defend && ctx.Ball.HasDefendGoal ? 0.7f : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            Point goal = ctx.Ball.DefendGoal;
            Point ball = ctx.Ball.Position;

            // A point just goal-side of the ball: from the ball, step GoalSideOffset toward the defended goal.
            long dx = goal.X - ball.X, dy = goal.Y - ball.Y;
            double len = Math.Sqrt((double)dx * dx + (double)dy * dy);
            Point block = len > GoalSideOffset1000
                ? new Point(ball.X + (int)(dx / len * GoalSideOffset1000), ball.Y + (int)(dy / len * GoalSideOffset1000))
                : goal; // ball is essentially on the goal — sit on it

            var self = BotSkills.SelfPosition(in ctx);
            bool onStation = BotSkills.DistanceSquared(self, block) <= OnStationSq;
            return onStation ? BotSkills.Coast(in ctx) : BotSkills.Navigate(in ctx, block);
        }
    }
}
