using SS.Core;
using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>Result of a sign-up lookup: a count and the matched names.</summary>
    /// <remarks><see cref="Count"/> is <c>-1</c> on error, <c>0</c> for no match, otherwise the number of prefix
    /// matches (or 1 when an exact match was found, in which case <see cref="Names"/> holds just that name).</remarks>
    public readonly record struct SignUpMatch(int Count, IReadOnlyList<string> Names);

    /// <summary>
    /// Interface exposed by the SignUps module (port of ASSS <c>signups</c>). Provides the async sign-up queries the
    /// Teams module uses to validate drafts and repopulate sign-up lists.
    /// </summary>
    public interface ISignUps : IComponentInterface
    {
        /// <summary>Whether the sign-up database is available.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Checks whether a player (by name prefix) is signed up for an event. If a name exactly matches the prefix,
        /// the result collapses to that single name with a count of 1.
        /// </summary>
        Task<SignUpMatch> IsPlayerSignedUpAsync(string eventName, string playerNamePrefix);

        /// <summary>Adds a player to an event's sign-up list. Returns whether a row was inserted.</summary>
        Task<bool> AddPlayerAsync(string eventName, string playerName);

        /// <summary>Removes a player from an event's sign-up list.</summary>
        Task<bool> RemovePlayerAsync(string eventName, string playerName);

        /// <summary>Returns whether an event exists.</summary>
        Task<bool> IsValidEventAsync(string eventName);

        /// <summary>Prints the sign-up command help to a player (used by the <c>?pbhelp</c> aggregator).</summary>
        void PrintHelp(Player player);
    }
}
