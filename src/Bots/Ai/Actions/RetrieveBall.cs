namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Go get a loose ball — just fly to it (routing around walls); the sim auto-picks it up on proximity
    /// (using the ship's SoccerBallProximity setting). Scores under the <see cref="Objective.Retrieve"/>
    /// objective, which the <see cref="BallStrategy"/> sets when no one is carrying the ball.
    /// </summary>
    /// <remarks>
    /// TODO(iterate): only commit to the retrieve when this bot is the closest of its team (else get open);
    /// if an enemy is closer but we're next, kill the enemy first; if the ball is goal-side, cut it off.
    /// </remarks>
    public sealed class RetrieveBall : IBotAction
    {
        public string Name => "RetrieveBall";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => p.Objective == Objective.Retrieve && ctx.Ball.InPlay ? 1.0f : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
            => BotSkills.Navigate(in ctx, ctx.Ball.Position);
    }
}
