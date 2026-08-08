namespace SS.Bots.Ai
{
    /// <summary>
    /// The slow "strategic bias" layer: decides the bot's current <see cref="Objective"/> — e.g. defend
    /// the base vs. chase the flag carrier vs. hunt — from the broad game situation. It runs infrequently
    /// (every few heartbeats), so it's cheap, and the objective it returns biases how the per-tick utility
    /// actions score. This is how goal-directedness is added without a planner's per-tick search cost.
    /// </summary>
    public interface IBotStrategy
    {
        Objective Evaluate(in BotContext ctx);
    }

    /// <summary>Trivial strategy for a pure fighter: always <see cref="Objective.Fight"/>.</summary>
    public sealed class AlwaysFightStrategy : IBotStrategy
    {
        public Objective Evaluate(in BotContext ctx) => Objective.Fight;
    }
}
