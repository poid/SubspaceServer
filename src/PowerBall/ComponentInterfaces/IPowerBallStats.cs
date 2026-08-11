using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>
    /// Interface exposed by the PowerBall statistics module (port of ASSS <c>pbstats</c>).
    /// </summary>
    public interface IPowerBallStats : IComponentInterface
    {
        /// <summary>
        /// Clears the recorded per-game stats for an arena.
        /// </summary>
        void ResetStats(Arena arena);

        /// <summary>
        /// Prints the stats command help to a player (used by the <c>?pbhelp</c> aggregator).
        /// </summary>
        void PrintHelp(Player player);
    }
}
