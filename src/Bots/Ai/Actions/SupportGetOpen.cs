namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// A teammate has the ball: get open toward the enemy goal so they can pass forward. For now this simply
    /// advances toward the goal we score in (ahead of the play); scores under <see cref="Objective.Support"/>.
    /// </summary>
    /// <remarks>
    /// TODO(iterate): get open in the carrier's direction of travel and clear of enemies (a real receiving
    /// lane), and fall back to hunting enemies near the ball when there's no good passing lane.
    /// </remarks>
    public sealed class SupportGetOpen : IBotAction
    {
        public string Name => "SupportGetOpen";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => p.Objective == Objective.Support && ctx.Ball.HasGoal ? 0.7f : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
            => BotSkills.Navigate(in ctx, ctx.Ball.Goal);
    }
}
