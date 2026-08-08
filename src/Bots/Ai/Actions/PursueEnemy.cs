namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Close on the nearest enemy (radar, no line of sight) when it's too far to fight, bringing it into
    /// engage range. Scored below <see cref="EngageEnemy"/> and only eligible when out of engage range,
    /// so the bot moves toward the fight rather than drifting when it can't shoot.
    /// </summary>
    public sealed class PursueEnemy : IBotAction
    {
        private const long EngageRange1000 = 24L * 16 * 1000; // must match EngageEnemy's range
        private const long EngageRangeSq = EngageRange1000 * EngageRange1000;

        public string Name => "PursueEnemy";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => p.HasEnemy && p.Enemy.DistanceSquared > EngageRangeSq ? 0.5f * profile.Aggression : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
            // Head toward the enemy, routing around walls via the path-follower (pivot-then-thrust so a slow
            // turner doesn't pin itself on the wall in between; radar sees through walls, a straight charge wouldn't).
            => BotSkills.Navigate(in ctx, p.Enemy.Position);
    }
}
