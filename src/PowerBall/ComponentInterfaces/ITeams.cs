using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>
    /// Interface exposed by the Teams module (port of ASSS <c>teams</c>).
    /// </summary>
    public interface ITeams : IComponentInterface
    {
        /// <summary>Resets and starts a fresh team-picking session for an arena.</summary>
        void InitiateNewTeams(Arena arena);

        /// <summary>Gets the sign-up event currently in use for drafting in an arena, or <see langword="null"/>.</summary>
        string? GetActiveEvent(Arena arena);

        /// <summary>
        /// Whether the arena is in the game-start stage (teams readied, game about to begin / underway). Used by the
        /// league to confirm a scheduled start is still valid before it fires.
        /// </summary>
        bool IsGameStarting(Arena arena);

        /// <summary>Prints the teams command help to a player (used by the <c>?pbhelp</c> aggregator).</summary>
        void PrintHelp(Player player);
    }
}
