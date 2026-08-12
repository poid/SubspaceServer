using SS.Core;
using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SS.PowerBall.ComponentInterfaces
{
    /// <summary>Whether an event exists and, if so, whether it is currently accepting sign-ups.</summary>
    public enum EventState
    {
        NotFound,
        Inactive,
        Active,
    }

    /// <summary>A row from the events table.</summary>
    public readonly record struct EventInfo(string Name, bool Active, string Description);

    /// <summary>A row from the saved-teams table.</summary>
    public readonly record struct SavedTeamInfo(int Id, string Name, string Captain);

    /// <summary>
    /// Async data-access for the PowerBall league database (PostgreSQL) — the events/signups and saved-teams tables.
    /// Implemented by the PowerBallDatabase module. All names are matched case-insensitively (stored upper-cased).
    /// </summary>
    /// <remarks>
    /// All methods run off the mainloop thread; awaiting them from a module resumes on the mainloop (the mainloop's
    /// synchronization context). Re-validate Player/Arena state after awaiting. When the database is unavailable
    /// (<see cref="IsAvailable"/> is <see langword="false"/>), the methods throw <see cref="System.InvalidOperationException"/>.
    /// </remarks>
    public interface ILeagueDatabase : IComponentInterface
    {
        /// <summary>Whether a database connection is configured and available.</summary>
        bool IsAvailable { get; }

        #region Events

        /// <summary>Adds an event. Returns <see langword="true"/> if a row was inserted.</summary>
        Task<bool> AddEventAsync(string name, string description);

        /// <summary>Deletes an event (and its sign-ups, via cascade).</summary>
        Task<bool> DeleteEventAsync(string name);

        /// <summary>Changes an event's description.</summary>
        Task<bool> ChangeEventDescriptionAsync(string name, string description);

        /// <summary>Gets all events.</summary>
        Task<IReadOnlyList<EventInfo>> GetEventsAsync();

        /// <summary>Sets an event's active (accepting sign-ups) flag.</summary>
        Task<bool> SetEventActiveAsync(string name, bool active);

        /// <summary>Gets whether an event exists and, if so, whether it is active.</summary>
        Task<EventState> GetEventStateAsync(string name);

        #endregion

        #region Sign-ups

        /// <summary>Finds sign-up names for an event that start with <paramref name="playerNamePrefix"/>.</summary>
        Task<IReadOnlyList<string>> FindSignUpsAsync(string eventName, string playerNamePrefix);

        /// <summary>Adds a sign-up (only if the event exists). Returns <see langword="true"/> if a row was inserted.</summary>
        Task<bool> AddSignUpAsync(string eventName, string playerName);

        /// <summary>Removes a sign-up.</summary>
        Task<bool> RemoveSignUpAsync(string eventName, string playerName);

        /// <summary>Gets all sign-up names for an event.</summary>
        Task<IReadOnlyList<string>> GetSignUpsAsync(string eventName);

        /// <summary>Clears all sign-ups for an event.</summary>
        Task<bool> ClearSignUpsAsync(string eventName);

        #endregion

        #region Teams

        /// <summary>Adds a saved team.</summary>
        Task<bool> AddTeamAsync(string name, string captain);

        /// <summary>Deletes a saved team (and its players, via cascade).</summary>
        Task<bool> DeleteTeamAsync(string name);

        /// <summary>Gets all saved teams.</summary>
        Task<IReadOnlyList<SavedTeamInfo>> GetTeamsAsync();

        /// <summary>Finds saved teams whose name starts with <paramref name="namePrefix"/>.</summary>
        Task<IReadOnlyList<SavedTeamInfo>> FindTeamsAsync(string namePrefix);

        /// <summary>From <paramref name="names"/>, returns those that already exist as saved teams.</summary>
        Task<IReadOnlyList<string>> CheckExistingTeamsAsync(IReadOnlyList<string> names);

        /// <summary>Sets a saved team's captain.</summary>
        Task<bool> ChangeTeamCaptainAsync(string name, string captain);

        /// <summary>Adds a player to a saved team (only if the team exists).</summary>
        Task<bool> AddTeamPlayerAsync(string teamName, string playerName);

        /// <summary>Removes a player from a saved team.</summary>
        Task<bool> RemoveTeamPlayerAsync(string teamName, string playerName);

        /// <summary>Gets the player names on a saved team.</summary>
        Task<IReadOnlyList<string>> GetTeamPlayersAsync(string teamName);

        #endregion
    }
}
