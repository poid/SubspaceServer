using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>
    /// Interface exposed by the PowerBall league module (port of ASSS <c>pbleague</c>).
    /// </summary>
    public interface IPowerBallLeague : IComponentInterface
    {
        /// <summary>
        /// Prints the league command help to a player (used by the <c>?pbhelp</c> aggregator).
        /// </summary>
        void PrintHelp(Player player);
    }
}
