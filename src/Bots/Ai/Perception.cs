using QS.Physics.Legacy; // EntityLocation

namespace SS.Bots.Ai
{
    /// <summary>
    /// A per-decision snapshot of what a bot perceives, built ONCE per decision and shared (by <c>in</c>)
    /// with every one of its utility actions — so the closest-enemy / threat queries run once, not per
    /// action. That's the main "share processing" lever for running many bots cheaply.
    /// </summary>
    /// <remarks>
    /// Targeting is radar-style (no line of sight): <see cref="Enemy"/> is the nearest enemy anywhere in
    /// awareness range, as in real Subspace. Line of fire only matters when deciding whether a specific
    /// shot connects. Modes 1-3 will extend this with flag / ball / carrier / goal / safe-zone fields
    /// (ideally hoisted to a once-per-arena perception shared across that arena's bots).
    /// </remarks>
    public readonly struct Perception
    {
        public readonly Objective Objective;
        public readonly bool HasEnemy;
        public readonly EntityLocation Enemy;   // nearest enemy on radar (no LOS required)
        public readonly bool HasThreat;
        public readonly EntityLocation Threat;  // soonest incoming weapon worth dodging

        public Perception(Objective objective, bool hasEnemy, EntityLocation enemy, bool hasThreat, EntityLocation threat)
        {
            Objective = objective;
            HasEnemy = hasEnemy;
            Enemy = enemy;
            HasThreat = hasThreat;
            Threat = threat;
        }
    }
}
