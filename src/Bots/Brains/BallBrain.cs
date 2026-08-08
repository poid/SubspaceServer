using SS.Bots.Ai;
using SS.Bots.Ai.Actions;

namespace SS.Bots.Brains
{
    /// <summary>
    /// Mode 3 — soccer (ball game), built on the utility-AI framework. A <see cref="BallStrategy"/> maps ball
    /// possession to an objective, and the action set covers the four situations: retrieve a loose ball, carry
    /// it to goal and score, get open when a teammate carries, and defend goal-side when an enemy carries — with
    /// the combat actions (dodge, engage) reused so the bot also survives and kills the carrier. Same template
    /// as <see cref="CombatBrain"/>: this class is just the mode's configuration.
    /// </summary>
    public sealed class BallBrain : IBotBrain
    {
        private readonly UtilityBrain _brain;

        public BallBrain(BotProfile? profile = null)
        {
            _brain = new UtilityBrain(
                profile ?? BotProfile.Medium,
                new BallStrategy(),
                new LeaveSafeZone(),
                new DodgeThreat(),
                new RepelDefensive(),
                new RetrieveBall(),
                new CarryToGoal(),
                new DefendGoal(),
                new SupportGetOpen(),
                new EngageEnemy(),
                new PursueEnemy());
        }

        public BotDecision Think(in BotContext ctx) => _brain.Think(in ctx);
    }
}
