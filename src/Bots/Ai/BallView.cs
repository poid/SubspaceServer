using QS.Physics.Legacy; // Point

namespace SS.Bots.Ai
{
    /// <summary>Who holds the ball, relative to THIS bot.</summary>
    public enum BallPossession : byte
    {
        None,      // no ball in play (waiting to respawn, or no ball game)
        Loose,     // on the map / just fired — up for grabs
        Mine,      // this bot is carrying it
        Teammate,  // a same-freq player is carrying it
        Enemy,     // an enemy is carrying it
    }

    /// <summary>
    /// A per-bot snapshot of the soccer-ball situation, built once per heartbeat by the module from the
    /// server's authoritative ball state (<c>IBalls</c>) plus the sim's goal-tile lookup, and handed to the
    /// bot's brain via <see cref="BotContext.Ball"/>. Positions are Coordinates1000, matching sim ship state.
    /// </summary>
    /// <remarks>
    /// The server ball game stays authoritative (real clients see it); this is just the read model the bot
    /// decides from. <see cref="Goal"/> is the goal THIS bot scores in (offence); <see cref="DefendGoal"/> is
    /// the goal the current enemy carrier would score in (what we guard). Only the relevant one is set.
    /// </remarks>
    public readonly struct BallView
    {
        public readonly bool InPlay;            // a ball exists and is playable this heartbeat
        public readonly BallPossession Owner;   // relative to this bot
        public readonly Point Position;         // ball position (Coordinates1000)
        public readonly Point Velocity;         // ball velocity (Coordinates1000/tick; 0 when carried)
        public readonly int CarrierId;          // carrier's player id (0 when loose)

        public readonly bool HasGoal;           // a goal this bot can score in was found
        public readonly Point Goal;             // that goal's centre (Coordinates1000)

        public readonly bool HasDefendGoal;     // set when an enemy carries the ball
        public readonly Point DefendGoal;       // the goal the enemy carrier scores in — the one to guard

        public readonly int HoldTicksLeft;      // sim ticks before OUR carry timer auto-throws (when Owner==Mine); 0 otherwise

        public readonly int LastToucherId;      // player id that last carried/shot the ball (0 if none) — a bot
                                                // can't re-grab its OWN shot until it's stationary or another touches it

        public BallView(
            bool inPlay, BallPossession owner, Point position, Point velocity, int carrierId,
            bool hasGoal, Point goal, bool hasDefendGoal, Point defendGoal, int holdTicksLeft, int lastToucherId)
        {
            InPlay = inPlay;
            Owner = owner;
            Position = position;
            Velocity = velocity;
            CarrierId = carrierId;
            HasGoal = hasGoal;
            Goal = goal;
            HasDefendGoal = hasDefendGoal;
            DefendGoal = defendGoal;
            HoldTicksLeft = holdTicksLeft;
            LastToucherId = lastToucherId;
        }
    }
}
