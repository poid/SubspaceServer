namespace SS.Bots.Ai
{
    /// <summary>
    /// Mode 3 (soccer) strategy: the objective follows ball possession. It carries when it has the ball,
    /// supports the carrier when a teammate has it, defends when an enemy has it, and retrieves a loose ball.
    /// With no ball in play it falls back to fighting. The per-tick ball actions and the combat actions then
    /// score against whichever objective this returns.
    /// </summary>
    public sealed class BallStrategy : IBotStrategy
    {
        public Objective Evaluate(in BotContext ctx)
        {
            BallView ball = ctx.Ball;
            if (!ball.InPlay)
                return Objective.Fight; // between points / no ball game — behave like a fighter

            return ball.Owner switch
            {
                BallPossession.Mine => Objective.Deliver,
                BallPossession.Teammate => Objective.Support,
                BallPossession.Enemy => Objective.Defend,
                // Loose: retrieve it — UNLESS it's our own still-moving shot, which we can't re-grab until it
                // stops or someone else touches it. Don't fixate on chasing it; get open / follow up instead.
                _ => IsMyLiveShot(in ctx, in ball) ? Objective.Support : Objective.Retrieve,
            };
        }

        // True when the loose ball is THIS bot's own shot that's still moving — last touched by us and not yet
        // stationary, so the pass-delay means we can't pick it up again yet.
        private static bool IsMyLiveShot(in BotContext ctx, in BallView ball)
        {
            const long MovingThreshold = 200L * 200; // Coordinates1000/tick²; below this the ball is ~stationary

            if (ball.LastToucherId != ctx.SelfId.Int)
                return false; // someone else touched it — it's fair game

            long vsq = (long)ball.Velocity.X * ball.Velocity.X + (long)ball.Velocity.Y * ball.Velocity.Y;
            return vsq > MovingThreshold;
        }
    }
}
