using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.PowerBall.ComponentCallbacks;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Threading;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// League match orchestration — a port of the ASSS <c>pbleague</c> module.
    /// </summary>
    /// <remarks>
    /// On the <see cref="TeamsReadyCallback"/> (fired by the Teams module) it runs a Ready/Set/Go LVZ countdown,
    /// ship-resets and warps each team to its start region, starts the ball game, and starts the in-game clock. On
    /// time-up it handles a tied game as either extra time or golden goal (per <c>Soccer:GoldenGoalRule</c>); otherwise
    /// it ends the game.
    /// </remarks>
    [ModuleInfo("PowerBall league match orchestration (ASSS pbleague port): countdown, clock, golden goal.")]
    public sealed class PowerBallLeague : IModule, IArenaAttachableModule, IPowerBallLeague
    {
        private const int MaxTeams = 8;

        private readonly IArenaManager _arenaManager;
        private readonly IBalls _balls;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly IGameTimer _gameTimer;
        private readonly ILogManager _logManager;
        private readonly ILvzObjects _lvzObjects;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapData _mapData;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IScoreStats _scoreStats;

        private ArenaDataKey<ArenaData> _adKey;

        public PowerBallLeague(
            IArenaManager arenaManager,
            IBalls balls,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            IGameTimer gameTimer,
            ILogManager logManager,
            ILvzObjects lvzObjects,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IScoreStats scoreStats)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _gameTimer = gameTimer ?? throw new ArgumentNullException(nameof(gameTimer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));
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

            InitializeArena(arena, ad);

            TeamsReadyCallback.Register(arena, Callback_TeamsReady);
            BallGameStartCallback.Register(arena, Callback_BallGameStart);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);
            BallGameGoalCallback.Register(arena, Callback_BallGameGoal);
            GameTimerEndedCallback.Register(arena, Callback_GameTimerEnded);

            _commandManager.AddCommand("settime", Command_settime, arena);
            _commandManager.AddCommand("resettime", Command_resettime, arena);

            ad.InterfaceToken = arena.RegisterInterface<IPowerBallLeague>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.InterfaceToken is not null)
                arena.UnregisterInterface(ref ad.InterfaceToken);

            _mainloopTimer.ClearTimer<CountdownState>(Timer_Countdown, arena);

            _commandManager.RemoveCommand("settime", Command_settime, arena);
            _commandManager.RemoveCommand("resettime", Command_resettime, arena);

            TeamsReadyCallback.Unregister(arena, Callback_TeamsReady);
            BallGameStartCallback.Unregister(arena, Callback_BallGameStart);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);
            BallGameGoalCallback.Unregister(arena, Callback_BallGameGoal);
            GameTimerEndedCallback.Unregister(arena, Callback_GameTimerEnded);

            return true;
        }

        #endregion

        #region IPowerBallLeague

        void IPowerBallLeague.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region Setup

        [ConfigHelp<int>("Teams", "GameTime", ConfigScope.Arena, Default = 0, Description = "League match length in seconds (0 = untimed).")]
        [ConfigHelp<int>("Soccer", "GoldenGoalRule", ConfigScope.Arena, Default = 0,
            Description = "On a tie at time-up: negative = add that many seconds of extra time; positive = golden goal (next goal wins); 0 = end the game.")]
        private void InitializeArena(Arena arena, ArenaData ad)
        {
            ad.TimerSeconds = _configManager.GetInt(arena.Cfg!, "Teams", "GameTime", 0);
            ad.Goal0 = _mapData.FindRegionByName(arena, "Goal0");
            ad.Goal1 = _mapData.FindRegionByName(arena, "Goal1");
            ad.Start0 = _mapData.FindRegionByName(arena, "Start0");
            ad.Start1 = _mapData.FindRegionByName(arena, "Start1");
        }

        #endregion

        #region Teams-ready countdown

        private void Callback_TeamsReady(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _logManager.LogA(LogLevel.Info, nameof(PowerBallLeague), arena, "Handle Teams Ready");
            _chat.SendArenaMessage(arena, "Game Starts in 30 seconds ...");

            // Initial delay 30s, then every 3s: READY (30s) -> SET (33s) -> GO + start (36s) -> GO off (39s).
            _mainloopTimer.SetTimer(Timer_Countdown, 30000, 3000, new CountdownState(arena) { Countdown = 3 }, arena);
        }

        private bool Timer_Countdown(CountdownState state)
        {
            Arena arena = state.Arena;
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            switch (state.Countdown)
            {
                case 3: // ready
                    ShipResetAndWarpAll(arena, ad);
                    _lvzObjects.Toggle(arena, PbLvz.LeagueReady, true);
                    break;

                case 2: // set
                    _lvzObjects.Toggle(arena, PbLvz.LeagueReady, false);
                    _lvzObjects.Toggle(arena, PbLvz.LeagueSet, true);
                    break;

                case 1: // go
                    _lvzObjects.Toggle(arena, PbLvz.LeagueSet, false);
                    _lvzObjects.Toggle(arena, PbLvz.LeagueGo, true);
                    ShipResetAndWarpAll(arena, ad);
                    _chat.SendArenaMessage(arena, ChatSound.Goal, "Go Go Go !!!");
                    _balls.TrySetBallCount(arena, null); // start the ball game (restore configured ball count)
                    break;

                default: // 0 - clear the GO banner and stop
                    _lvzObjects.Toggle(arena, PbLvz.LeagueGo, false);
                    break;
            }

            if (state.Countdown > 0)
            {
                state.Countdown--;
                return true;
            }

            return false;
        }

        private void ShipResetAndWarpAll(Arena arena, ArenaData ad)
        {
            _logManager.LogA(LogLevel.Info, nameof(PowerBallLeague), arena, "Ship Reset And Warp All");

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena || player.Ship == ShipType.Spec)
                        continue;

                    bool odd = (player.Freq % 2) != 0;
                    bool found = odd
                        ? TryGetWarpPoint(ad.Start1, ad.Goal1, out short x, out short y)
                        : TryGetWarpPoint(ad.Start0, ad.Goal0, out x, out y);

                    _game.ShipReset(player);
                    _scoreStats.ScoreReset(player, PersistInterval.Reset);

                    if (found)
                        _game.WarpTo(player, x, y);
                    else
                        _game.GivePrize(player, Prize.Warp, 1);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            _scoreStats.SendUpdates(arena, null);
        }

        private static bool TryGetWarpPoint(MapRegion? region, MapRegion? fallback, out short x, out short y)
        {
            if (region is not null && region.TileCount > 0)
            {
                region.FindRandomPoint(out x, out y);
                return true;
            }

            if (fallback is not null && fallback.TileCount > 0)
            {
                fallback.FindRandomPoint(out x, out y);
                return true;
            }

            x = -1;
            y = -1;
            return false;
        }

        #endregion

        #region Game timer

        private void Callback_BallGameStart(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _logManager.LogA(LogLevel.Info, nameof(PowerBallLeague), arena, "Game Start");

            ad.IsGoldenGoal = false;
            ad.GoldenGoalRule = _configManager.GetInt(arena.Cfg!, "Soccer", "GoldenGoalRule", 0);

            if (ad.TimerSeconds != 0)
            {
                StartLvzTimer(arena, ad.TimerSeconds);
                _gameTimer.SetTimer(arena, TimeSpan.FromSeconds(ad.TimerSeconds));
            }

            ResetScores(arena);
        }

        private void Callback_GameTimerEnded(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _logManager.LogA(LogLevel.Info, nameof(PowerBallLeague), arena, "Timer Finish");

            Span<int> scores = stackalloc int[MaxTeams];
            GetScores(arena, scores);

            if (ad.GoldenGoalRule != 0 && scores[0] == scores[1])
            {
                if (ad.GoldenGoalRule < 0)
                {
                    // Extra time.
                    int extra = -ad.GoldenGoalRule;
                    _chat.SendArenaMessage(arena, ChatSound.Beep1, $"Scores tied. {extra / 60}:{extra % 60:D2} additional time added");
                    _gameTimer.SetTimer(arena, TimeSpan.FromSeconds(extra));
                    StartLvzTimer(arena, extra); // restart the LVZ clock (replaces the ASSS CB_TIMESET fire)
                }
                else
                {
                    // Golden goal.
                    _chat.SendArenaMessage(arena, ChatSound.Beep1, "Scores tied. Golden Goal in effect!");
                    ad.IsGoldenGoal = true;
                }
            }
            else
            {
                _balls.EndGame(arena);
            }
        }

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _logManager.LogA(LogLevel.Info, nameof(PowerBallLeague), arena, "Game Over");

            if (ad.TimerSeconds != 0)
                _gameTimer.SetTimer(arena, TimeSpan.Zero); // stop the in-game timer
        }

        private void Callback_BallGameGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.IsGoldenGoal)
            {
                ad.IsGoldenGoal = false;
                _balls.EndGame(arena);
            }
        }

        #endregion

        #region Commands

        [CommandHelp(Targets = CommandTarget.None, Args = "[minutes][:seconds]", Description = "Display or set the game time to minutes:seconds. (staff)")]
        private void Command_settime(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, $"Timer currently set to {ad.TimerSeconds / 60}:{ad.TimerSeconds % 60:D2}");
                return;
            }

            int seconds;
            int colon = arg.IndexOf(':');
            if (colon >= 0)
            {
                int minutes = ParseIntOrZero(arg[..colon]);
                int secs = ParseIntOrZero(arg[(colon + 1)..]);
                seconds = secs + minutes * 60;
            }
            else
            {
                seconds = ParseIntOrZero(arg);
            }

            if (seconds == 0 && arg[0] != '0')
            {
                _chat.SendMessage(player, "Please specify a new time in the form of <seconds> or <min>:<seconds>");
                return;
            }

            ad.TimerSeconds = seconds;
            _chat.SendMessage(player, $"Timer now set to {ad.TimerSeconds / 60}:{ad.TimerSeconds % 60:D2}");
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Reset the game time to the arena default (Teams:GameTime). (staff)")]
        private void Command_resettime(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.TimerSeconds = _configManager.GetInt(arena.Cfg!, "Teams", "GameTime", 0);
            _chat.SendMessage(player, $"Timer now reset to {ad.TimerSeconds / 60}:{ad.TimerSeconds % 60:D2}");
        }

        private static int ParseIntOrZero(ReadOnlySpan<char> s)
        {
            s = s.Trim();
            return int.TryParse(s, out int value) ? value : 0;
        }

        private void PrintHelp(Player player)
        {
            _chat.SendMessage(player, "-------------------------------------------------------");
            _chat.SendMessage(player, "The following PB Leagues Module commands are available:");
            _chat.SendMessage(player, "-------------------------------------------------------");

            bool displayedMod = false;
            DisplayModCommand(player, ref displayedMod, "settime", "?settime [minutes][:seconds]", "Display or set the game time to minutes:seconds");
            DisplayModCommand(player, ref displayedMod, "resettime", "?resettime", "Reset the game time to the arena default (Teams:GameTime)");
        }

        private void DisplayModCommand(Player player, ref bool displayedMod, string capability, string command, string description)
        {
            if (!_capabilityManager.HasCapability(player, $"cmd_{capability}"))
                return;

            if (!displayedMod)
            {
                displayedMod = true;
                _chat.SendMessage(player, "-=-=-= Moderator Commands =-=-=-");
            }

            _chat.SendMessage(player, $"{command,-35} - {description}");
        }

        #endregion

        #region Helpers

        private void StartLvzTimer(Arena arena, int seconds)
        {
            IPowerBallLvz? lvz = arena.GetInterface<IPowerBallLvz>();
            if (lvz is null)
                return;

            try { lvz.StartGameTimer(arena, seconds); }
            finally { arena.ReleaseInterface(ref lvz); }
        }

        private void ResetScores(Arena arena)
        {
            IBallGamePoints? pg = arena.GetInterface<IBallGamePoints>();
            if (pg is null)
                return;

            try { pg.ResetScores(arena); }
            finally { arena.ReleaseInterface(ref pg); }
        }

        private void GetScores(Arena arena, Span<int> scores)
        {
            scores.Clear();
            IBallGamePoints? pg = arena.GetInterface<IBallGamePoints>();
            if (pg is null)
                return;

            try
            {
                ReadOnlySpan<int> teamScores = pg.GetScores(arena);
                for (int i = 0; i < scores.Length && i < teamScores.Length; i++)
                    scores[i] = teamScores[i];
            }
            finally
            {
                arena.ReleaseInterface(ref pg);
            }
        }

        #endregion

        #region Helper types

        private sealed class CountdownState(Arena arena)
        {
            public readonly Arena Arena = arena;
            public int Countdown;
        }

        private sealed class ArenaData : IResettable
        {
            public InterfaceRegistrationToken<IPowerBallLeague>? InterfaceToken;

            public MapRegion? Goal0;
            public MapRegion? Goal1;
            public MapRegion? Start0;
            public MapRegion? Start1;
            public int TimerSeconds;
            public bool IsGoldenGoal;
            public int GoldenGoalRule;

            public bool TryReset()
            {
                InterfaceToken = null;
                Goal0 = Goal1 = Start0 = Start1 = null;
                TimerSeconds = 0;
                IsGoldenGoal = false;
                GoldenGoalRule = 0;
                return true;
            }
        }

        #endregion
    }
}
