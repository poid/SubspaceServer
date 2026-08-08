using System;
using System.Threading;
using QS.Physics.Legacy; // EntityLocation

namespace SS.Bots.Ai
{
    /// <summary>
    /// A utility-AI brain. Each decision it: (1) runs the slow <see cref="IBotStrategy"/> to set the
    /// current objective, (2) builds one shared <see cref="Perception"/>, (3) scores every action, and
    /// (4) executes the highest scorer. Difficulty comes entirely from the <see cref="BotProfile"/>
    /// (aim error, reaction rate, awareness range, aggression / caution, score noise).
    /// </summary>
    /// <remarks>
    /// All four gameplay modes are built by handing this the right action set + strategy — the brain
    /// logic here never changes. It's deliberately cheap and search-free (O(actions) per decision), and
    /// the reaction throttle means weaker bots also run less often, so many bots across many arenas stay
    /// affordable. Exposes <see cref="LastActionName"/> / <see cref="LastObjective"/> for debugging.
    /// </remarks>
    public sealed class UtilityBrain : IBotBrain
    {
        private readonly IBotAction[] _actions;
        private readonly IBotStrategy _strategy;
        private readonly BotProfile _profile;
        private readonly Random _rng;

        private Objective _objective = Objective.Idle;
        private int _decideCountdown;    // heartbeats until the next full re-decision
        private int _strategyCountdown;  // heartbeats until the next strategy evaluation
        private BotDecision _lastDecision;
        private int _lastActionIndex = -1;

        private const int StrategyIntervalHeartbeats = 5; // ~0.5s; strategy is cheap and slow-changing
        private const float HysteresisBonus = 0.1f;       // stick with the current action to avoid thrashing

        private static int _seed;

        public UtilityBrain(BotProfile profile, IBotStrategy strategy, params IBotAction[] actions)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _rng = new Random(Interlocked.Increment(ref _seed)); // AI only — not the deterministic sim
        }

        public string? LastActionName { get; private set; }
        public Objective LastObjective => _objective;

        public BotDecision Think(in BotContext ctx)
        {
            // Reaction throttle: between re-decisions keep executing the last choice. Weaker profiles
            // decide less often — slower to react AND cheaper to run.
            if (_lastActionIndex >= 0 && _decideCountdown > 0)
            {
                _decideCountdown--;
                return _lastDecision;
            }
            _decideCountdown = Math.Max(1, _profile.ReactionHeartbeats) - 1;

            // Strategic bias (slow): set the current objective.
            if (_strategyCountdown <= 0)
            {
                _objective = _strategy.Evaluate(in ctx);
                _strategyCountdown = StrategyIntervalHeartbeats;
            }
            _strategyCountdown--;

            Perception p = BuildPerception(in ctx);

            // Score every eligible action; the highest wins (ties broken by order). Hysteresis and a
            // little noise are added only to eligible actions so an ineligible one never sneaks in.
            int best = -1;
            float bestScore = 0f;
            for (int i = 0; i < _actions.Length; i++)
            {
                float s = _actions[i].Score(in ctx, in p, _profile);
                if (s <= 0f)
                    continue;

                if (i == _lastActionIndex)
                    s += HysteresisBonus;
                if (_profile.DecisionNoise > 0f)
                    s += (float)(_rng.NextDouble() * _profile.DecisionNoise);

                if (s > bestScore)
                {
                    bestScore = s;
                    best = i;
                }
            }

            _lastActionIndex = best;
            if (best < 0)
            {
                LastActionName = null;
                _lastDecision = BotSkills.Coast(in ctx);
            }
            else
            {
                LastActionName = _actions[best].Name;
                _lastDecision = _actions[best].Execute(in ctx, in p, _profile);
            }
            return _lastDecision;
        }

        private Perception BuildPerception(in BotContext ctx)
        {
            // Radar targeting (no line of sight): the nearest enemy anywhere within awareness range. Ignore
            // an enemy sitting in a safe zone — it can't be damaged there, and chasing it in only traps this
            // bot too. (TODO: with a crowd, fall through to the closest enemy that ISN'T safe, not just drop.)
            bool hasEnemy = ctx.World.TryGetClosestEnemy(ctx.SelfId, out EntityLocation enemy, _profile.AwarenessRange1000);
            if (hasEnemy && BotSkills.IsSafeZone(in ctx, enemy.Position))
                hasEnemy = false;

            bool hasThreat = BotSkills.TryGetImminentThreat(in ctx, 60, 25, out EntityLocation threat);
            return new Perception(_objective, hasEnemy, enemy, hasThreat, threat);
        }
    }
}
