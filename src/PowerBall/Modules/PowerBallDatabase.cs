using Npgsql;
using NpgsqlTypes;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// PostgreSQL data-access for the PowerBall league (events/signups and saved teams). Port of the MySQL-backed DB
    /// access in the ASSS <c>signups</c> and <c>teams</c> modules, consolidated into one repository.
    /// </summary>
    /// <remarks>
    /// Reads its connection string from <c>global.conf</c> (<c>[SS.PowerBall] DatabaseConnectionString</c>) and applies
    /// the schema (see sql/schema.sql) on load. If no connection string is configured (or the database is unreachable),
    /// the module still loads but <see cref="ILeagueDatabase.IsAvailable"/> reports <see langword="false"/> and the
    /// league sign-up/saved-team features are disabled.
    /// </remarks>
    [ModuleInfo("""
        PowerBall league database (PostgreSQL) for events/signups and saved teams.
        Requires global.conf: [SS.PowerBall] DatabaseConnectionString.
        """)]
    public sealed class PowerBallDatabase : IAsyncModule, ILeagueDatabase, IDisposable
    {
        // Names are stored twice: 'name' is exactly as the player typed it (for display, e.g. on a stats website),
        // and 'name_key' is the upper-cased form used for case-insensitive matching / uniqueness / lookups.
        private const string Schema = """
            CREATE SCHEMA IF NOT EXISTS pb;
            CREATE TABLE IF NOT EXISTS pb.event (
                id          serial       PRIMARY KEY,
                name        varchar(32)  NOT NULL,
                name_key    varchar(32)  NOT NULL UNIQUE,
                description varchar(250) NOT NULL DEFAULT '',
                active      boolean      NOT NULL DEFAULT false
            );
            CREATE TABLE IF NOT EXISTS pb.signup (
                event_id int         NOT NULL REFERENCES pb.event(id) ON DELETE CASCADE,
                name     varchar(32) NOT NULL,
                name_key varchar(32) NOT NULL,
                PRIMARY KEY (event_id, name_key)
            );
            CREATE TABLE IF NOT EXISTS pb.team (
                id       serial      PRIMARY KEY,
                name     varchar(64) NOT NULL,
                name_key varchar(64) NOT NULL UNIQUE,
                captain  varchar(32) NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS pb.team_player (
                team_id  int         NOT NULL REFERENCES pb.team(id) ON DELETE CASCADE,
                name     varchar(32) NOT NULL,
                name_key varchar(32) NOT NULL,
                PRIMARY KEY (team_id, name_key)
            );
            """;

        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;

        private NpgsqlDataSource? _dataSource;
        private InterfaceRegistrationToken<ILeagueDatabase>? _token;
        private bool _isDisposed;

        public PowerBallDatabase(IConfigManager configManager, ILogManager logManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        #region Module members

        [ConfigHelp("SS.PowerBall", "DatabaseConnectionString", ConfigScope.Global,
            Description = "The Npgsql connection string for the PowerBall league database (events/signups/teams).")]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            string? connectionString = _configManager.GetStr(_configManager.Global, "SS.PowerBall", "DatabaseConnectionString");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logManager.LogM(LogLevel.Warn, nameof(PowerBallDatabase),
                    "No connection string (global.conf: [SS.PowerBall] DatabaseConnectionString). League sign-up/saved-team features are disabled.");
            }
            else
            {
                try
                {
                    NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
                    await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
                    await using (NpgsqlCommand command = new(Schema, connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    _dataSource = dataSource;
                    _logManager.LogM(LogLevel.Info, nameof(PowerBallDatabase), "Connected to the league database.");
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(PowerBallDatabase), $"Failed to connect to the league database. League sign-up/saved-team features are disabled. {ex.Message}");
                    if (_dataSource is not null)
                    {
                        await _dataSource.DisposeAsync().ConfigureAwait(false);
                        _dataSource = null;
                    }
                }
            }

            _token = broker.RegisterInterface<ILeagueDatabase>(this);
            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _token) != 0)
                return false;

            if (_dataSource is not null)
            {
                await _dataSource.DisposeAsync().ConfigureAwait(false);
                _dataSource = null;
            }

            return true;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _dataSource?.Dispose();
            _dataSource = null;
            _isDisposed = true;
        }

        #endregion

        bool ILeagueDatabase.IsAvailable => _dataSource is not null;

        #region Events

        Task<bool> ILeagueDatabase.AddEventAsync(string name, string description) =>
            ExecuteReturningAnyAsync(
                "INSERT INTO pb.event (name, name_key, description) VALUES ($1, $2, $3) ON CONFLICT (name_key) DO NOTHING",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, name);
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(name));
                    p.AddWithValue(NpgsqlDbType.Varchar, description);
                });

        Task<bool> ILeagueDatabase.DeleteEventAsync(string name) =>
            ExecuteReturningAnyAsync("DELETE FROM pb.event WHERE name_key = $1", p => p.AddWithValue(NpgsqlDbType.Varchar, Up(name)));

        Task<bool> ILeagueDatabase.ChangeEventDescriptionAsync(string name, string description) =>
            ExecuteReturningAnyAsync(
                "UPDATE pb.event SET description = $2 WHERE name_key = $1",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(name));
                    p.AddWithValue(NpgsqlDbType.Varchar, description);
                });

        async Task<IReadOnlyList<EventInfo>> ILeagueDatabase.GetEventsAsync() =>
            await QueryListAsync(
                "SELECT name, active, description FROM pb.event ORDER BY name",
                static _ => { },
                static reader => new EventInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetString(2))).ConfigureAwait(false);

        Task<bool> ILeagueDatabase.SetEventActiveAsync(string name, bool active) =>
            ExecuteReturningAnyAsync(
                "UPDATE pb.event SET active = $2 WHERE name_key = $1",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(name));
                    p.AddWithValue(NpgsqlDbType.Boolean, active);
                });

        async Task<EventState> ILeagueDatabase.GetEventStateAsync(string name)
        {
            EnsureAvailable();
            await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync().ConfigureAwait(false);
            await using NpgsqlCommand command = new("SELECT active FROM pb.event WHERE name_key = $1", connection);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, Up(name));

            object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            if (result is null)
                return EventState.NotFound;

            return (bool)result ? EventState.Active : EventState.Inactive;
        }

        #endregion

        #region Sign-ups

        async Task<IReadOnlyList<string>> ILeagueDatabase.FindSignUpsAsync(string eventName, string playerNamePrefix) =>
            await QueryStringListAsync(
                "SELECT s.name FROM pb.event e JOIN pb.signup s ON s.event_id = e.id WHERE e.name_key = $1 AND s.name_key LIKE $2 || '%'",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(eventName));
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(playerNamePrefix));
                }).ConfigureAwait(false);

        Task<bool> ILeagueDatabase.AddSignUpAsync(string eventName, string playerName) =>
            ExecuteReturningAnyAsync(
                "INSERT INTO pb.signup (event_id, name, name_key) SELECT e.id, $2, $3 FROM pb.event e WHERE e.name_key = $1 ON CONFLICT DO NOTHING",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(eventName));
                    p.AddWithValue(NpgsqlDbType.Varchar, playerName);
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(playerName));
                });

        Task<bool> ILeagueDatabase.RemoveSignUpAsync(string eventName, string playerName) =>
            ExecuteReturningAnyAsync(
                "DELETE FROM pb.signup s USING pb.event e WHERE s.event_id = e.id AND e.name_key = $1 AND s.name_key = $2",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(eventName));
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(playerName));
                });

        async Task<IReadOnlyList<string>> ILeagueDatabase.GetSignUpsAsync(string eventName) =>
            await QueryStringListAsync(
                "SELECT s.name FROM pb.event e JOIN pb.signup s ON s.event_id = e.id WHERE e.name_key = $1 ORDER BY s.name",
                p => p.AddWithValue(NpgsqlDbType.Varchar, Up(eventName))).ConfigureAwait(false);

        Task<bool> ILeagueDatabase.ClearSignUpsAsync(string eventName) =>
            ExecuteReturningAnyAsync(
                "DELETE FROM pb.signup s USING pb.event e WHERE s.event_id = e.id AND e.name_key = $1",
                p => p.AddWithValue(NpgsqlDbType.Varchar, Up(eventName)));

        #endregion

        #region Teams

        Task<bool> ILeagueDatabase.AddTeamAsync(string name, string captain) =>
            ExecuteReturningAnyAsync(
                "INSERT INTO pb.team (name, name_key, captain) VALUES ($1, $2, $3) ON CONFLICT (name_key) DO NOTHING",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, name);
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(name));
                    p.AddWithValue(NpgsqlDbType.Varchar, captain);
                });

        Task<bool> ILeagueDatabase.DeleteTeamAsync(string name) =>
            ExecuteReturningAnyAsync("DELETE FROM pb.team WHERE name_key = $1", p => p.AddWithValue(NpgsqlDbType.Varchar, Up(name)));

        async Task<IReadOnlyList<SavedTeamInfo>> ILeagueDatabase.GetTeamsAsync() =>
            await QueryListAsync(
                "SELECT id, name, captain FROM pb.team ORDER BY name",
                static _ => { },
                static reader => new SavedTeamInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))).ConfigureAwait(false);

        async Task<IReadOnlyList<SavedTeamInfo>> ILeagueDatabase.FindTeamsAsync(string namePrefix) =>
            await QueryListAsync(
                "SELECT id, name, captain FROM pb.team WHERE name_key LIKE $1 || '%' ORDER BY name",
                p => p.AddWithValue(NpgsqlDbType.Varchar, Up(namePrefix)),
                static reader => new SavedTeamInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))).ConfigureAwait(false);

        async Task<IReadOnlyList<string>> ILeagueDatabase.CheckExistingTeamsAsync(IReadOnlyList<string> names)
        {
            string[] upper = new string[names.Count];
            for (int i = 0; i < names.Count; i++)
                upper[i] = Up(names[i]);

            return await QueryStringListAsync(
                "SELECT name FROM pb.team WHERE name_key = ANY($1)",
                p => p.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Varchar, upper)).ConfigureAwait(false);
        }

        Task<bool> ILeagueDatabase.ChangeTeamCaptainAsync(string name, string captain) =>
            ExecuteReturningAnyAsync(
                "UPDATE pb.team SET captain = $2 WHERE name_key = $1",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(name));
                    p.AddWithValue(NpgsqlDbType.Varchar, captain);
                });

        Task<bool> ILeagueDatabase.AddTeamPlayerAsync(string teamName, string playerName) =>
            ExecuteReturningAnyAsync(
                "INSERT INTO pb.team_player (team_id, name, name_key) SELECT t.id, $2, $3 FROM pb.team t WHERE t.name_key = $1 ON CONFLICT DO NOTHING",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(teamName));
                    p.AddWithValue(NpgsqlDbType.Varchar, playerName);
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(playerName));
                });

        Task<bool> ILeagueDatabase.RemoveTeamPlayerAsync(string teamName, string playerName) =>
            ExecuteReturningAnyAsync(
                "DELETE FROM pb.team_player tp USING pb.team t WHERE tp.team_id = t.id AND t.name_key = $1 AND tp.name_key = $2",
                p =>
                {
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(teamName));
                    p.AddWithValue(NpgsqlDbType.Varchar, Up(playerName));
                });

        async Task<IReadOnlyList<string>> ILeagueDatabase.GetTeamPlayersAsync(string teamName) =>
            await QueryStringListAsync(
                "SELECT tp.name FROM pb.team t JOIN pb.team_player tp ON tp.team_id = t.id WHERE t.name_key = $1 ORDER BY tp.name",
                p => p.AddWithValue(NpgsqlDbType.Varchar, Up(teamName))).ConfigureAwait(false);

        #endregion

        #region Helpers

        private static string Up(string s) => s.ToUpperInvariant();

        private void EnsureAvailable()
        {
            if (_dataSource is null)
                throw new InvalidOperationException("The PowerBall league database is not available.");
        }

        private async Task<bool> ExecuteReturningAnyAsync(string sql, Action<NpgsqlParameterCollection> bind)
        {
            EnsureAvailable();
            await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync().ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            bind(command.Parameters);
            int rows = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return rows > 0;
        }

        private async Task<IReadOnlyList<string>> QueryStringListAsync(string sql, Action<NpgsqlParameterCollection> bind) =>
            await QueryListAsync(sql, bind, static reader => reader.GetString(0)).ConfigureAwait(false);

        private async Task<List<T>> QueryListAsync<T>(string sql, Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> read)
        {
            EnsureAvailable();
            await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync().ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            bind(command.Parameters);

            List<T> list = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                list.Add(read(reader));

            return list;
        }

        #endregion
    }
}
