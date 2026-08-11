using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>
    /// The kind of public game currently configured in a MultiPub arena.
    /// </summary>
    /// <remarks>
    /// Mirrors the ASSS <c>PB_gameType</c> enum. The numeric values are preserved so that any config that referenced
    /// them still lines up.
    /// </remarks>
    public enum PbGameType
    {
        /// <summary>No game type selected.</summary>
        Any = 0,

        /// <summary>Small Pub (the default full-size public game).</summary>
        Pub = 1,

        /// <summary>Proball (uses the same physics as <see cref="Pub"/>).</summary>
        Pro = 2,

        /// <summary>Small 3H.</summary>
        ThreeH = 3,

        /// <summary>Scramble side-game arena.</summary>
        Scramble = 4,

        /// <summary>Mini PowerBall.</summary>
        Mini = 5,
    }

    /// <summary>
    /// Interface exposed by the MultiPub controller module (the port of ASSS <c>pbpub</c>).
    /// </summary>
    public interface IMultiPub : IComponentInterface
    {
        /// <summary>
        /// Gets the game type currently configured for an arena.
        /// </summary>
        PbGameType GetGameType(Arena arena);

        /// <summary>
        /// Sets the stored game type for an arena.
        /// </summary>
        /// <remarks>
        /// This is the port of ASSS <c>SetTypeAndLock</c>. Despite that name, it only records the game type; it does not
        /// rewrite settings, warp, reset the game, or lock anything. Used by side-game modules (e.g. scramble) to mark
        /// the arena's type.
        /// </remarks>
        void SetGameType(Arena arena, PbGameType gameType);

        /// <summary>
        /// Prints the MultiPub help/command list to a player. Used by the <c>?pbhelp</c> aggregator.
        /// </summary>
        void PrintHelp(Player player);
    }
}
