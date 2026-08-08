using QS.Physics.Legacy.Structs; // ShipState
using SS.Packets.Game;          // WeaponCodes

namespace SS.Bots.Ai.Actions
{
    /// <summary>
    /// Evade an imminent incoming weapon. Scores high (scaled by Caution) whenever a threat is about to
    /// connect, so survival tends to outrank offence. If the bot carries a repel and the threat is close, it
    /// pops a repel to push the incoming fire away while dodging. Reusable by every mode.
    /// </summary>
    public sealed class DodgeThreat : IBotAction
    {
        private const long RepelRange1000 = 7L * 16 * 1000; // ~7 tiles: only worth a repel when the shot is close
        private const long RepelRangeSq = RepelRange1000 * RepelRange1000;

        public string Name => "DodgeThreat";

        public float Score(in BotContext ctx, in Perception p, BotProfile profile)
            => p.HasThreat ? 0.9f * profile.Caution : 0f;

        public BotDecision Execute(in BotContext ctx, in Perception p, BotProfile profile)
        {
            BotDecision evade = BotSkills.Evade(in ctx, in p.Threat);

            // Pop a repel to push the incoming weapon away, if we have one and the shot is close. Repels are
            // limited inventory (the sim decrements per use); this is self-limiting because the repel clears
            // the threat, so next heartbeat there's nothing to react to.
            ref readonly ShipState self = ref BotSkills.Self(in ctx);
            if (self.RepelsCurrent > 0 && p.Threat.DistanceSquared < RepelRangeSq)
                return evade with { Fire = WeaponCodes.Repel };

            return evade;
        }
    }
}
