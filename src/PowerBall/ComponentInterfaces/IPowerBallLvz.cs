using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>
    /// Interface exposed by the PowerBall LVZ module (port of ASSS <c>pblvzs</c>).
    /// </summary>
    public interface IPowerBallLvz : IComponentInterface
    {
        /// <summary>
        /// Starts the on-screen LVZ game clock counting down from <paramref name="seconds"/>. Marks the game as a
        /// league game (which suppresses the "new game" banner). Called by the league module.
        /// </summary>
        void StartGameTimer(Arena arena, int seconds);

        /// <summary>
        /// Prints the LVZ command help to a player (used by the <c>?pbhelp</c> aggregator).
        /// </summary>
        void PrintHelp(Player player);
    }
}
