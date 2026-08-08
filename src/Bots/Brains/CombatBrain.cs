using SS.Bots.Ai;
using SS.Bots.Ai.Actions;

namespace SS.Bots.Brains
{
    /// <summary>
    /// Mode 4 — solo/team combat, built on the utility-AI framework. Wires the combat action set
    /// (leave-safe → dodge → engage → pursue) and an always-fight strategy into a <see cref="UtilityBrain"/> at the
    /// given difficulty. The decision logic lives in <see cref="UtilityBrain"/> and the actions, so this
    /// class is just the mode's configuration — the template every other mode's brain will follow.
    /// </summary>
    public sealed class CombatBrain : IBotBrain
    {
        private readonly UtilityBrain _brain;

        public CombatBrain(BotProfile? profile = null)
        {
            _brain = new UtilityBrain(
                profile ?? BotProfile.Medium,
                new AlwaysFightStrategy(),
                new LeaveSafeZone(),
                new DodgeThreat(),
                new RepelDefensive(),
                new EngageEnemy(),
                new PursueEnemy());
        }

        public BotDecision Think(in BotContext ctx) => _brain.Think(in ctx);
    }
}
