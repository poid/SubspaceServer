namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Get out of a safe zone. Inside one the engine suppresses fire and blocks damage, so a bot sitting
    /// there is useless — it can't shoot and can't be shot. This scores above every combat action whenever
    /// the bot is on a safe tile, so leaving always wins until it's back in open space. Applies to every
    /// mode, which is why it lives as a standalone action rather than inside the combat logic.
    /// </summary>
    public sealed class LeaveSafeZone : IBotAction
    {
        // Above any normal action score (engage/dodge peak ~1.0 + hysteresis/noise) so exiting dominates.
        private const float ExitScore = 2f;

        public string Name => "LeaveSafeZone";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => BotSkills.SelfInSafeZone(in ctx) ? ExitScore : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            // Head for the nearest open tile out of the zone; if boxed in, thrust ahead to drift out.
            if (BotSkills.TryFindSafeExit(in ctx, out byte exit))
                return BotSkills.Steer(exit, thrust: true);

            return BotSkills.Steer(BotSkills.Self(in ctx).Rotation, thrust: true);
        }
    }
}
