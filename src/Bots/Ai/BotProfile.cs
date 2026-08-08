namespace SS.Bots.Ai
{
    /// <summary>
    /// Difficulty / personality knobs for a utility brain. Everything that makes a bot better or worse
    /// lives here, so the same brain logic scales from Easy to Hard without branching. Actions read it
    /// when scoring and executing; the brain reads it for its reaction rate.
    /// </summary>
    /// <remarks>
    /// This is the single lever for "easy / medium / hard" bots. Utility AI degrades gracefully: a weaker
    /// bot is one that aims worse, reacts slower, sees less far, and picks more erratically — not one with
    /// behaviours removed.
    /// </remarks>
    public sealed class BotProfile
    {
        public required string Name { get; init; }

        /// <summary>Multiplier on offensive action scores (engage / pursue). Higher = more aggressive.</summary>
        public float Aggression { get; init; } = 1f;

        /// <summary>Multiplier on defensive action scores (dodge / retreat). Higher = more cautious.</summary>
        public float Caution { get; init; } = 1f;

        /// <summary>Max random aim error added to a firing solution, in ring positions (0 = perfect aim).</summary>
        public int AimErrorPositions { get; init; }

        /// <summary>How aligned (ring positions) the ship must be before firing. Lower = more disciplined.</summary>
        public int FireAlignTolerance { get; init; } = 3;

        /// <summary>Heartbeats between re-decisions. Higher = slower to react (and cheaper to run).</summary>
        public int ReactionHeartbeats { get; init; } = 1;

        /// <summary>Radar range in 1000-scale; enemies beyond are not perceived (0 = whole arena).</summary>
        public long AwarenessRange1000 { get; init; }

        /// <summary>Random noise added to each eligible action's score (0..1). Higher = more erratic choices.</summary>
        public float DecisionNoise { get; init; }

        public static readonly BotProfile Easy = new()
        {
            Name = "easy",
            Aggression = 0.7f,
            Caution = 0.7f,
            AimErrorPositions = 3,
            FireAlignTolerance = 5,
            ReactionHeartbeats = 4,
            AwarenessRange1000 = 30L * 16 * 1000, // ~30 tiles of radar awareness
            DecisionNoise = 0.15f,
        };

        public static readonly BotProfile Medium = new()
        {
            Name = "medium",
            Aggression = 1f,
            Caution = 1f,
            AimErrorPositions = 1,
            FireAlignTolerance = 3,
            ReactionHeartbeats = 2,
            AwarenessRange1000 = 0,
            DecisionNoise = 0.05f,
        };

        public static readonly BotProfile Hard = new()
        {
            Name = "hard",
            Aggression = 1.2f,
            Caution = 1.1f,
            AimErrorPositions = 0,
            FireAlignTolerance = 2,
            ReactionHeartbeats = 1,
            AwarenessRange1000 = 0,
            DecisionNoise = 0f,
        };

        public static BotProfile FromName(string? name) => name switch
        {
            "easy" => Easy,
            "hard" => Hard,
            _ => Medium,
        };
    }
}
