using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.PowerBall.ComponentCallbacks;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// League team drafting / captains / rosters — a port of the ASSS <c>teams</c> module.
    /// </summary>
    /// <remarks>
    /// Defines teams by freq, assigns captains, drafts/picks players (from the arena or a sign-up list, including
    /// offline players), enforces team-max / in-game-max, supports free/normal/snake/random pick orders, ready-up,
    /// substitutions, borrows, and optional PostgreSQL persistence of team rosters. It arena-locks players to spec during
    /// setup and fires <see cref="TeamsReadyCallback"/> when all teams are ready (which the PowerBallLeague module uses
    /// to start the match).
    /// <para>
    /// This is split across several partial files: Teams.cs (core/data/lookups/callbacks), Teams.Picking.cs (the pick
    /// order state machine), Teams.Commands.cs (the ? commands), and Teams.Database.cs (saved-team persistence and
    /// sign-up integration).
    /// </para>
    /// </remarks>
    [ModuleInfo("League team drafting/captains/rosters (ASSS teams port). Requires PowerBallDatabase + SignUps.")]
    public sealed partial class Teams : IModule, IArenaAttachableModule, ITeams
    {
        private readonly IArenaManager _arenaManager;
        private readonly IBalls _balls;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILeagueDatabase _db;
        private readonly ILogManager _logManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;
        private readonly IScoreStats _scoreStats;
        private readonly ISignUps _signUps;

        private ArenaDataKey<ArenaData> _adKey;

        public Teams(
            IArenaManager arenaManager,
            IBalls balls,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILeagueDatabase db,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng,
            IScoreStats scoreStats,
            ISignUps signUps)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));
            _signUps = signUps ?? throw new ArgumentNullException(nameof(signUps));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _arenaManager.FreeArenaData(ref _adKey);
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);

            AddCommands(arena);

            ad.InterfaceToken = arena.RegisterInterface<ITeams>(this);

            InitializeArena(arena, ad);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.InterfaceToken is not null)
                arena.UnregisterInterface(ref ad.InterfaceToken);

            RemoveCommands(arena);

            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);

            // Release the arena lock and clear rosters.
            ReleaseTeams(arena, ad);
            _game.UnlockArena(arena, true, false);

            return true;
        }

        #endregion

        #region ITeams

        void ITeams.InitiateNewTeams(Arena arena)
        {
            if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
                ResetTeams(arena, ad);
        }

        string? ITeams.GetActiveEvent(Arena arena)
        {
            return arena.TryGetExtraData(_adKey, out ArenaData? ad) ? ad.ActiveEvent : null;
        }

        void ITeams.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region Setup / reset

        [ConfigHelp<int>("Teams", "TeamMax", ConfigScope.Arena, Default = int.MaxValue, Description = "Max picked players per team.")]
        [ConfigHelp<int>("Teams", "TeamInGameMax", ConfigScope.Arena, Default = int.MaxValue, Description = "Max in-game (non-spec) players per team.")]
        [ConfigHelp<int>("Teams", "PickingType", ConfigScope.Arena, Default = (int)PickingType.Normal, Description = "1=Free, 2=Normal, 3=Snake, 4=Random.")]
        [ConfigHelp<int>("Teams", "RepopulateSignups", ConfigScope.Arena, Default = 1, Description = "Put removed/released players back on the sign-up list.")]
        [ConfigHelp<int>("Teams", "IsDraft", ConfigScope.Arena, Default = 0, Description = "Draft (players go to spec) vs pick (players go in-game).")]
        private void InitializeArena(Arena arena, ArenaData ad)
        {
            ad.ActiveEvent = null;
            ResetTeams(arena, ad);
        }

        private void ResetTeams(Arena arena, ArenaData ad)
        {
            _logManager.LogA(LogLevel.Info, nameof(Teams), arena, "Reset Teams");

            ReleaseTeams(arena, ad);

            ad.NumberOfTeams = 0;
            ad.PickingStage = PickingStage.Setup;
            ad.PickingRound = 0;
            ad.CurrentPickFreq = -1;
            ad.CurrentPick = 1;
            ad.PickDirection = false;
            ad.SaveTeams = false;
            ad.OfflineDrafting = false;
            // Note: ActiveEvent is intentionally preserved across a reset.

            ConfigHandle ch = arena.Cfg!;
            ad.TeamMax = ReadMax(ch, "TeamMax");
            ad.TeamInGameMax = ReadMax(ch, "TeamInGameMax");
            ad.PickingType = (PickingType)_configManager.GetInt(ch, "Teams", "PickingType", (int)PickingType.Normal);
            ad.RepopulateSignups = _configManager.GetInt(ch, "Teams", "RepopulateSignups", 1) != 0;
            ad.IsDraft = _configManager.GetInt(ch, "Teams", "IsDraft", 0) != 0;

            // Lock the arena to spec and move everyone off a playing freq.
            _game.LockArena(arena, true, false, false, true);

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena && player.Freq != arena.SpecFreq)
                        _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private int ReadMax(ConfigHandle ch, string key)
        {
            int value = _configManager.GetInt(ch, "Teams", key, int.MaxValue);
            return value <= 0 ? int.MaxValue : value;
        }

        private void ReleaseTeams(Arena arena, ArenaData ad)
        {
            foreach (Team team in ad.Teams)
                ReleaseTeam(arena, ad, team);

            ad.Teams.Clear();
        }

        private void ReleaseTeam(Arena arena, ArenaData ad, Team team)
        {
            // Spec every rostered player found in the arena, and repopulate the sign-up list if configured.
            foreach (TeamPlayer teamPlayer in team.Players)
            {
                Player? player = _playerData.FindPlayer(teamPlayer.Name);
                if (player is not null && player.Arena == arena)
                    _game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);

                if (ad.RepopulateSignups && ad.ActiveEvent is not null && !teamPlayer.WasLoaded && !teamPlayer.WasBorrowed)
                    RepopulateSignup(ad.ActiveEvent, teamPlayer.Name);
            }

            team.Players.Clear();
            team.BorrowList.Clear();
        }

        #endregion

        #region Lookups

        private static Team? FindTeamFreq(ArenaData ad, int freq)
        {
            foreach (Team team in ad.Teams)
            {
                if (team.Frequency == freq)
                    return team;
            }

            return null;
        }

        private static Team? FindTeamFuzzy(ArenaData ad, ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
                return null;

            Team? match = null;
            int count = 0;

            foreach (Team team in ad.Teams)
            {
                if (team.TeamName.AsSpan().StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (team.TeamName.Length == name.Length)
                        return team; // exact

                    count++;
                    match ??= team;
                }
            }

            return count == 1 ? match : (count > 1 ? null : match);
        }

        /// <summary>Finds a team by a numeric freq, or by (fuzzy) name.</summary>
        private static Team? FindTeam(ArenaData ad, ReadOnlySpan<char> input)
        {
            if (int.TryParse(input, out int freq))
            {
                Team? byFreq = FindTeamFreq(ad, freq);
                if (byFreq is not null)
                    return byFreq;
            }

            return FindTeamFuzzy(ad, input);
        }

        private static TeamPlayer? FindPlayerInTeamExact(Team team, ReadOnlySpan<char> name)
        {
            foreach (TeamPlayer teamPlayer in team.Players)
            {
                if (name.Equals(teamPlayer.Name, StringComparison.OrdinalIgnoreCase))
                    return teamPlayer;
            }

            return null;
        }

        /// <summary>Fuzzy player lookup within a team (or all teams when <paramref name="team"/> is null).</summary>
        private (TeamPlayer? player, Team? team, int count) FindPlayerInTeamFuzzy(ArenaData ad, Team? team, ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
                return (null, null, 0);

            TeamPlayer? match = null;
            Team? matchTeam = null;
            int count = 0;

            foreach (Team t in ad.Teams)
            {
                if (team is not null && t != team)
                    continue;

                foreach (TeamPlayer teamPlayer in t.Players)
                {
                    if (teamPlayer.Name.AsSpan().StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (teamPlayer.Name.Length == name.Length)
                            return (teamPlayer, t, 1); // exact

                        count++;
                        if (match is null)
                        {
                            match = teamPlayer;
                            matchTeam = t;
                        }
                    }
                }
            }

            if (count > 1)
                return (null, null, count);

            return (match, matchTeam, count);
        }

        private static Team? FindTeamExactPlayer(ArenaData ad, ReadOnlySpan<char> name)
        {
            foreach (Team team in ad.Teams)
            {
                if (FindPlayerInTeamExact(team, name) is not null)
                    return team;
            }

            return null;
        }

        private static (BorrowedPlayer? borrow, Team? team) FindBorrowedPlayerInTeams(ArenaData ad, ReadOnlySpan<char> name)
        {
            foreach (Team team in ad.Teams)
            {
                foreach (BorrowedPlayer borrow in team.BorrowList)
                {
                    if (name.Equals(borrow.Name, StringComparison.OrdinalIgnoreCase))
                        return (borrow, team);
                }
            }

            return (null, null);
        }

        private bool IsCaptain(ArenaData ad, Player player)
        {
            foreach (Team team in ad.Teams)
            {
                if (team.Captain is not null && string.Equals(team.Captain, player.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static Team? FindCaptainTeam(ArenaData ad, Player player)
        {
            foreach (Team team in ad.Teams)
            {
                if (team.Captain is not null && string.Equals(team.Captain, player.Name, StringComparison.OrdinalIgnoreCase))
                    return team;
            }

            return null;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == PlayerAction.EnterArena)
            {
                SendWelcome(player, ad);
                ChangePlayerLagStatus(ad, player, leaving: false);
            }
            else if (action == PlayerAction.LeaveArena)
            {
                ChangePlayerLagStatus(ad, player, leaving: true);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            Team? team = FindTeamExactPlayer(ad, player.Name);
            if (team is null)
                return;

            TeamPlayer? teamPlayer = FindPlayerInTeamExact(team, player.Name);
            if (teamPlayer is not null)
                teamPlayer.LaggedOut = newShip == ShipType.Spec;
        }

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.PickingStage = PickingStage.GameOver;
            _balls.TrySetBallCount(arena, 0); // stop the ball game
        }

        private void ChangePlayerLagStatus(ArenaData ad, Player player, bool leaving)
        {
            Team? team = FindTeamExactPlayer(ad, player.Name);
            if (team is null)
                return;

            TeamPlayer? teamPlayer = FindPlayerInTeamExact(team, player.Name);
            if (teamPlayer is null)
                return;

            teamPlayer.LaggedOut = leaving || player.Ship == ShipType.Spec;
        }

        private void SendWelcome(Player player, ArenaData ad)
        {
            _chat.SendMessage(player, ad.PickingStage switch
            {
                PickingStage.Setup => "Picking of teams has yet to begin.",
                PickingStage.Picking => "Teams are currently being picked.",
                PickingStage.Paused => "Team picking is currently paused.",
                PickingStage.Completed => "Teams have completed picking. Waiting on captains to ready.",
                PickingStage.GameStart => "The game has already started.",
                PickingStage.GameOver => "The game has finished.",
                _ => "",
            });

            if (ad.ActiveEvent is not null)
                _chat.SendMessage(player, $"In order to be picked you must be on the signup list. If you haven't signed up already use ?signup {ad.ActiveEvent}");

            _chat.SendMessage(player, "Type ?pbhelp for available commands.");
        }

        #endregion

        #region Data types

        internal enum PickingStage
        {
            Setup = 1,
            Picking,
            Paused,
            Completed,
            GameStart,
            GameOver,
        }

        internal enum PickingType
        {
            Free = 1,
            Normal,
            Snake,
            Random,
        }

        private sealed class Team
        {
            public required int Frequency;
            public required string TeamName;
            public bool Ready;
            public string? Captain;
            public readonly List<TeamPlayer> Players = [];
            public int PickedCount;
            public bool WasLoaded;
            public int FreqShip = -1;
            public int PlayersInGame;
            public readonly List<BorrowedPlayer> BorrowList = [];
        }

        private sealed class TeamPlayer
        {
            public required string Name;
            public int Ship;
            public bool LaggedOut = true;
            public bool WasLoaded;
            public bool WasBorrowed;
        }

        private sealed class BorrowedPlayer
        {
            public required string Name;
            public bool Approved;
            public string? ApprovedBy;
        }

        private sealed class ArenaData : IResettable
        {
            public InterfaceRegistrationToken<ITeams>? InterfaceToken;

            public readonly List<Team> Teams = [];
            public int NumberOfTeams;
            public PickingStage PickingStage = PickingStage.Setup;
            public int PickingRound;
            public string? ActiveEvent;
            public int CurrentPickFreq = -1;
            public int CurrentPick = 1;
            public bool PickDirection;
            public bool SaveTeams;
            public bool OfflineDrafting;

            public int TeamMax = int.MaxValue;
            public int TeamInGameMax = int.MaxValue;
            public PickingType PickingType = PickingType.Normal;
            public bool RepopulateSignups = true;
            public bool IsDraft;

            public bool TryReset()
            {
                InterfaceToken = null;
                Teams.Clear();
                NumberOfTeams = 0;
                PickingStage = PickingStage.Setup;
                PickingRound = 0;
                ActiveEvent = null;
                CurrentPickFreq = -1;
                CurrentPick = 1;
                PickDirection = false;
                SaveTeams = false;
                OfflineDrafting = false;
                TeamMax = int.MaxValue;
                TeamInGameMax = int.MaxValue;
                PickingType = PickingType.Normal;
                RepopulateSignups = true;
                IsDraft = false;
                return true;
            }
        }

        #endregion
    }
}
