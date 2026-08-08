namespace SS.Bots.Ai
{
    /// <summary>
    /// One candidate behaviour a utility brain can choose each decision. <see cref="Score"/> rates its
    /// desirability from the current situation; the brain executes the highest scorer. Actions are small,
    /// reusable, and shared across modes — combat and flag brains both use DodgeThreat / EngageEnemy.
    /// </summary>
    public interface IBotAction
    {
        /// <summary>Short name for logging / debugging the decision.</summary>
        string Name { get; }

        /// <summary>
        /// Desirability right now, normally in 0..1 (before the brain adds hysteresis / noise). Return 0
        /// or less when the action is not applicable — such actions are never chosen.
        /// </summary>
        float Score(in BotContext ctx, in Perception p, BotProfile profile);

        /// <summary>Produce this tick's input when this action wins the vote.</summary>
        BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile);
    }
}
