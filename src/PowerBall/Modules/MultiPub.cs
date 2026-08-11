using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// The MultiPub public-arena controller — a port of the ASSS <c>pbpub</c> module.
    /// </summary>
    /// <remarks>
    /// Manages a public PowerBall arena: switching between game types (Small Pub / 3H / Mini PB / Pro) by overriding
    /// arena settings, randomizing teams, and routing players who join a side-game freq band (scramble/proball/small4tm)
    /// into per-player client-setting overrides plus spawn placement. It also enforces which freqs players may switch to.
    /// <para>
    /// This is arena-attachable; attach it (via <c>Modules:AttachModules</c>) only to the public PowerBall arena. In ASSS
    /// this module hard-coded an "attach only to the arena literally named 0" guard; that guard is dropped here in favor of
    /// config-driven attachment.
    /// </para>
    /// </remarks>
    [ModuleInfo("MultiPub controller (ASSS pbpub port): game-type voting/switching, team randomize, side-game routing.")]
    public sealed class MultiPub : IModule, IArenaAttachableModule, IMultiPub, IFreqManagerEnforcerAdvisor
    {
        private const string VersionString = "PowerBall MultiPub (C# port of ASSS PB Module v1.2 by POiD)";

        private readonly IArenaManager _arenaManager;
        private readonly IBalls _balls;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly IClientSettings _clientSettings;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly ILogManager _logManager;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        public MultiPub(
            IArenaManager arenaManager,
            IBalls balls,
            ICapabilityManager capabilityManager,
            IChat chat,
            IClientSettings clientSettings,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            InitializeArena(arena, ad);

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            BallPickupCallback.Register(arena, Callback_BallPickup);
            BallGameStartCallback.Register(arena, Callback_BallGameStart);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);
            BallGameGoalCallback.Register(arena, Callback_BallGameGoal);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);

            _commandManager.AddCommand("pbpubhelp", Command_pbpubhelp, arena);
            _commandManager.AddCommand("pbversion", Command_pbversion, arena);
            _commandManager.AddCommand("randomize", Command_randomize, arena);
            _commandManager.AddCommand("randomnow", Command_randomnow, arena);
            _commandManager.AddCommand("changegame", Command_changegame, arena);
            _commandManager.AddCommand("cg", Command_changegame, arena);
            _commandManager.AddCommand("changenow", Command_changenow, arena);
            _commandManager.AddCommand("changemap", Command_changemap, arena);
            _commandManager.AddCommand("changemapongoal", Command_changemapongoal, arena);
            _commandManager.AddCommand("togglerandom", Command_togglerandom, arena);
            _commandManager.AddCommand("pubmode", Command_pubmode, arena);

            ad.EnforcerAdvisorToken = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
            ad.InterfaceToken = arena.RegisterInterface<IMultiPub>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.InterfaceToken is not null)
                arena.UnregisterInterface(ref ad.InterfaceToken);

            arena.UnregisterAdvisor(ref ad.EnforcerAdvisorToken);

            _mainloopTimer.ClearTimer<Arena>(MainloopTimer_ChangeMapOnGoal, arena);

            _commandManager.RemoveCommand("pbpubhelp", Command_pbpubhelp, arena);
            _commandManager.RemoveCommand("pbversion", Command_pbversion, arena);
            _commandManager.RemoveCommand("randomize", Command_randomize, arena);
            _commandManager.RemoveCommand("randomnow", Command_randomnow, arena);
            _commandManager.RemoveCommand("changegame", Command_changegame, arena);
            _commandManager.RemoveCommand("cg", Command_changegame, arena);
            _commandManager.RemoveCommand("changenow", Command_changenow, arena);
            _commandManager.RemoveCommand("changemap", Command_changemap, arena);
            _commandManager.RemoveCommand("changemapongoal", Command_changemapongoal, arena);
            _commandManager.RemoveCommand("togglerandom", Command_togglerandom, arena);
            _commandManager.RemoveCommand("pubmode", Command_pubmode, arena);

            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            BallPickupCallback.Unregister(arena, Callback_BallPickup);
            BallGameStartCallback.Unregister(arena, Callback_BallGameStart);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);
            BallGameGoalCallback.Unregister(arena, Callback_BallGameGoal);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);

            return true;
        }

        #endregion

        #region IMultiPub

        PbGameType IMultiPub.GetGameType(Arena arena)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return PbGameType.Any;

            return ad.GameType;
        }

        void IMultiPub.SetGameType(Arena arena, PbGameType gameType)
        {
            // Port of ASSS SetTypeAndLock: only records the game type; no settings/warp/reset/lock.
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.GameType == gameType)
                return;

            ad.GameType = gameType;
        }

        void IMultiPub.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region IFreqManagerEnforcerAdvisor

        bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder? errorMessage)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return true;

            // Allow: the two pub teams (0..PrivFreqStart-2), and each side-game band. Deny everything else (silently).
            if (newFreq >= 0 && newFreq < ad.PrivFreqStart - 1)
                return true;

            if (newFreq == ad.ScrambleFreq || newFreq == ad.ScrambleFreq + 1)
                return true;

            if (newFreq == ad.ProballFreq || newFreq == ad.ProballFreq + 1)
                return true;

            if (newFreq >= ad.Small4TmFreq && newFreq <= ad.Small4TmFreq + 3)
                return true;

            return false;
        }

        #endregion

        #region Setup

        [ConfigHelp<int>("MultiPub", "MaxPubFreq", ConfigScope.Arena, Default = 1,
            Description = "The highest freq considered part of the public game (freqs above this are side games/priv).")]
        [ConfigHelp<int>("MultiPub", "ScrambleFreq", ConfigScope.Arena, Default = 10, Description = "Base freq of the scramble side-game (uses this and +1).")]
        [ConfigHelp<int>("MultiPub", "Small4TmFreq", ConfigScope.Arena, Default = 20, Description = "Base freq of the small-4-team side-game (uses this through +3).")]
        [ConfigHelp<int>("MultiPub", "ProballFreq", ConfigScope.Arena, Default = 30, Description = "Base freq of the proball side-game (uses this and +1).")]
        [ConfigHelp<int>("MultiPub", "ScrambleSoccerThrowTime", ConfigScope.Arena, Default = 1000, Description = "SoccerThrowTime override applied to scramble-band players.")]
        [ConfigHelp<int>("MultiPub", "ProballSoccerThrowTime", ConfigScope.Arena, Default = 790, Description = "SoccerThrowTime override applied to proball-band players.")]
        [ConfigHelp<int>("MultiPub", "Small4TmSoccerThrowTime", ConfigScope.Arena, Default = 1000, Description = "SoccerThrowTime override applied to small-4-team-band players.")]
        [ConfigHelp<int>("MultiPub", "Small4TmCenterX", ConfigScope.Arena, Default = 512, Description = "Spawn X (tiles) for small-4-team-band players.")]
        [ConfigHelp<int>("MultiPub", "Small4TmCenterY", ConfigScope.Arena, Default = 512, Description = "Spawn Y (tiles) for small-4-team-band players.")]
        private void InitializeArena(Arena arena, ArenaData ad)
        {
            // Runtime state (once per arena create / attach).
            ad.GameType = PbGameType.Pub;
            ad.RandomizeTeamsOnEnd = true;
            ad.RoundStart = false;
            ad.PubMode = true;
            ad.ResetScores = true;
            ad.ChangeMapOnGoal = "-";

            LoadConfig(arena, ad);
        }

        private void LoadConfig(Arena arena, ArenaData ad)
        {
            ConfigHandle ch = arena.Cfg!;
            ad.MaxPubFreq = _configManager.GetInt(ch, "MultiPub", "MaxPubFreq", 1);
            ad.ScrambleFreq = _configManager.GetInt(ch, "MultiPub", "ScrambleFreq", 10);
            ad.Small4TmFreq = _configManager.GetInt(ch, "MultiPub", "Small4TmFreq", 20);
            ad.ProballFreq = _configManager.GetInt(ch, "MultiPub", "ProballFreq", 30);
            ad.SpecFreq = _configManager.GetInt(ch, "Team", "SpectatorFrequency", 420);
            ad.PrivFreqStart = _configManager.GetInt(ch, "Team", "PrivFreqStart", 3);
            ad.ScrambleThrowTime = _configManager.GetInt(ch, "MultiPub", "ScrambleSoccerThrowTime", 1000);
            ad.ProballThrowTime = _configManager.GetInt(ch, "MultiPub", "ProballSoccerThrowTime", 790);
            ad.Small4TmThrowTime = _configManager.GetInt(ch, "MultiPub", "Small4TmSoccerThrowTime", 1000);
            ad.Small4TmCenterX = _configManager.GetInt(ch, "MultiPub", "Small4TmCenterX", 512);
            ad.Small4TmCenterY = _configManager.GetInt(ch, "MultiPub", "Small4TmCenterY", 512);
            ad.CustomGameMask = _configManager.GetInt(ch, "Soccer", "CustomGame", 0);
        }

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create)
                InitializeArena(arena, ad);
            else if (action == ArenaAction.ConfChanged)
                LoadConfig(arena, ad);
        }

        #endregion

        #region Game-type switching

        private void ChangeGameType(Arena arena, ArenaData ad, Player? player, PbGameType gameType)
        {
            if (ad.GameType == gameType)
                return;

            // Never switch away from a scramble arena.
            if (ad.GameType == PbGameType.Scramble)
                return;

            GameTypeSettings settings;
            switch (gameType)
            {
                case PbGameType.Pub:
                case PbGameType.Pro:
                    settings = PubSettings;
                    break;
                case PbGameType.ThreeH:
                    settings = ThreeHSettings;
                    break;
                case PbGameType.Mini:
                    settings = MiniSettings;
                    break;
                default:
                    return;
            }

            ApplyGameSettings(arena, in settings);
            ad.GameType = gameType;

            // The config change notification (which resends client settings) is fired asynchronously by ConfigManager.

            if (ad.ResetScores)
                ResetBallGame(arena, player);

            // Warp the pub-freq players so they respawn at the new spawn points.
            WarpPubPlayers(arena, ad);
        }

        private void ApplyGameSettings(Arena arena, in GameTypeSettings s)
        {
            ConfigHandle ch = arena.Cfg!;

            void Set(string section, string key, int value) => _configManager.SetInt(ch, section, key, value, null, false);

            Set("Soccer", "SpawnRadius", s.SpawnRadius);
            Set("Soccer", "SpawnX", s.SpawnX);
            Set("Soccer", "SpawnY", s.SpawnY);
            Set("Soccer", "PassDelay", s.PassDelay);
            Set("Soccer", "DisableWallPass", s.DisableWallPass);

            Set("Spawn", "Team0-Radius", s.Team0Radius);
            Set("Spawn", "Team0-X", s.Team0X);
            Set("Spawn", "Team0-Y", s.Team0Y);
            Set("Spawn", "Team1-Radius", s.Team1Radius);
            Set("Spawn", "Team1-X", s.Team1X);
            Set("Spawn", "Team1-Y", s.Team1Y);

            Set("Bomb", "BombAliveTime", s.BombAliveTime);
            Set("Bullet", "BulletDamageLevel", s.BulletDamageLevel);
            Set("Bullet", "BulletAliveTime", s.BulletAliveTime);
            Set("Mine", "MineAliveTime", s.MineAliveTime);
            Set("Burst", "BurstDamageLevel", s.BurstDamageLevel);
            Set("Misc", "WarpRadiusLimit", s.WarpRadiusLimit);
            Set("Kill", "EnterDelay", s.EnterDelay);

            Set("Warbird", "BurstMax", s.BurstMax);
            Set("Warbird", "BulletFireEnergy", s.WarbirdFireEnergy);
            Set("Warbird", "PortalMax", s.PortalMax);
            Set("Warbird", "SoccerThrowTime", s.SoccerThrowTime);
            Set("Warbird", "BrickMax", s.BrickMax);
            Set("Warbird", "RepelMax", s.RepelMax);
            Set("Warbird", "InitialEnergy", s.InitialEnergy);
            Set("Warbird", "MaximumEnergy", s.MaximumEnergy);

            Set("Javelin", "BurstMax", s.BurstMax);
            Set("Javelin", "BulletFireEnergy", s.JavelinFireEnergy);
            Set("Javelin", "PortalMax", s.PortalMax);
            Set("Javelin", "SoccerThrowTime", s.SoccerThrowTime);
            Set("Javelin", "BrickMax", s.BrickMax);
            Set("Javelin", "RepelMax", s.RepelMax);
            Set("Javelin", "InitialEnergy", s.InitialEnergy);
            Set("Javelin", "MaximumEnergy", s.MaximumEnergy);

            Set("PrizeWeight", "Repel", s.RepelPrize);
        }

        private void WarpPubPlayers(Arena arena, ArenaData ad)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Status == PlayerState.Playing
                            && player.Ship != ShipType.Spec
                            && player.Freq <= ad.MaxPubFreq)
                        {
                            set.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (set.Count > 0)
                    _game.GivePrize(Target.ListTarget(set), Prize.Warp, 1);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void ResetBallGame(Arena arena, Player? player)
        {
            IBallGamePoints? pg = arena.GetInterface<IBallGamePoints>();
            if (pg is null)
                return;

            try
            {
                pg.ResetGame(arena, player!);
            }
            finally
            {
                arena.ReleaseInterface(ref pg);
            }
        }

        #endregion

        #region Commands

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the list of PowerBall commands.")]
        private void Command_pbpubhelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            PrintHelp(player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the PowerBall module version information.")]
        private void Command_pbversion(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, VersionString);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Log your vote to randomize the teams.")]
        private void Command_randomize(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.PubMode)
            {
                _chat.SendMessage(player, "Pub Mode is disabled. Cannot randomize teams.");
                return;
            }

            if (player.Freq > ad.MaxPubFreq)
            {
                _chat.SendMessage(player, "Cannot vote from spec or side game.");
                return;
            }

            int total = 0;
            int yesVote = 0;

            _playerData.Lock();
            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Arena != arena || otherPlayer.Status != PlayerState.Playing)
                        continue;

                    if (otherPlayer.Ship == ShipType.Spec && otherPlayer.Freq <= ad.MaxPubFreq)
                        continue;

                    if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? opd))
                        continue;

                    total++;

                    if (otherPlayer == player)
                    {
                        opd.RandomizeVote = !opd.RandomizeVote;
                        _chat.SendMessage(player, $"Your vote to random teams has been set to {(opd.RandomizeVote ? "Yes" : "No")}");
                    }

                    if (opd.RandomizeVote)
                        yesVote++;
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (yesVote > total / 2)
            {
                RandomizeTeams(arena, ad);
                ResetBallGame(arena, player);
            }
            else
            {
                _chat.SendMessage(player, $"Current votes for randomizing:  For: {yesVote}  Against: {total - yesVote}");
            }
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Randomize the teams and restart the game. (staff)")]
        private void Command_randomnow(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.PubMode)
            {
                _chat.SendMessage(player, "Pub Mode is disabled. Cannot randomize teams.");
                return;
            }

            RandomizeTeams(arena, ad);
            ResetBallGame(arena, player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<2|pub|3|3h|4|mini>", Description = "Log your vote to change the game type.")]
        private void Command_changegame(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.PubMode)
            {
                _chat.SendMessage(player, "Pub Mode is disabled. Cannot randomize teams.");
                return;
            }

            if (player.Freq > ad.MaxPubFreq)
            {
                _chat.SendMessage(player, "Cannot vote for pub mode from spec or side game.");
                return;
            }

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                CountVotes(player, arena, ad, PbGameType.Any, isCheck: true);
                return;
            }

            if (!TryParseGameType(arg, out PbGameType vote, out string message))
            {
                _chat.SendMessage(player, message);
                return;
            }

            _chat.SendMessage(player, message);
            CountVotes(player, arena, ad, vote, isCheck: false);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<2|pub|3|3h|4|mini>", Description = "Change the game type and restart the game. (staff)")]
        private void Command_changenow(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ChangeMap(player, parameters);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<2|pub|3|3h|4|mini>", Description = "Change the game type but continue the game. (staff)")]
        private void Command_changemap(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.ResetScores = false;
            if (ChangeMap(player, parameters))
                _balls.TrySpawnBall(arena, 0);
            ad.ResetScores = true;
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "<2|pub|3|3h|4|mini> | -", Description = "Change the game type on the next goal but continue the game (- to cancel). (staff)")]
        private void Command_changemapongoal(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty || arg.SequenceEqual("-"))
            {
                ad.ChangeMapOnGoal = "-";
                ad.ResetScores = true;
                _chat.SendMessage(player, "Map change on next goal canceled.");
            }
            else
            {
                ad.ResetScores = false;
                ad.ChangeMapOnGoal = arg.ToString();
                _chat.SendMessage(player, $"Map will change to {arg} on next goal.");
            }
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "[ON|OFF]", Description = "Display or enable/disable whether teams are randomized at the end of a game. (staff)")]
        private void Command_togglerandom(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.PubMode)
            {
                _chat.SendMessage(player, "Pub Mode is disabled. Cannot randomize teams.");
                return;
            }

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, $"Randomize currently set as {(ad.RandomizeTeamsOnEnd ? "ON" : "OFF")}");
            }
            else if (arg.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (ad.RandomizeTeamsOnEnd)
                    _chat.SendMessage(player, "Randomize is already set to ON");
                else
                {
                    ad.RandomizeTeamsOnEnd = true;
                    _chat.SendMessage(player, "Randomize set to ON");
                }
            }
            else if (arg.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                if (!ad.RandomizeTeamsOnEnd)
                    _chat.SendMessage(player, "Randomize is already set to OFF");
                else
                {
                    ad.RandomizeTeamsOnEnd = false;
                    _chat.SendMessage(player, "Randomize set to OFF");
                }
            }
            else
            {
                _chat.SendMessage(player, "Please specify ON or OFF.");
            }
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "[ON|OFF]", Description = "Display or enable/disable Pub Mode. (staff)")]
        private void Command_pubmode(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, $"Pub Mode currently set as {(ad.PubMode ? "ON" : "OFF")}");
            }
            else if (arg.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (ad.PubMode)
                    _chat.SendMessage(player, "Pub Mode is already set to ON");
                else
                {
                    ad.PubMode = true;
                    _chat.SendMessage(player, "Pub mode set to ON");
                }
            }
            else if (arg.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                if (!ad.PubMode)
                    _chat.SendMessage(player, "Pub Mode is already set to OFF");
                else
                {
                    ad.PubMode = false;
                    _chat.SendMessage(player, "Pub Mode set to OFF");
                }
            }
            else
            {
                _chat.SendMessage(player, "Please specify ON or OFF.");
            }
        }

        private bool ChangeMap(Player player, ReadOnlySpan<char> parameters)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (!ad.PubMode)
            {
                _chat.SendMessage(player, "Pub Mode is disabled. Cannot randomize teams.");
                return false;
            }

            ReadOnlySpan<char> arg = parameters.Trim();
            if (arg.IsEmpty)
            {
                _chat.SendMessage(player, "No change option specified.");
                return false;
            }

            if (!TryParseGameTypeForMap(arg, out PbGameType vote))
            {
                _chat.SendMessage(player, "Invalid map.");
                return false;
            }

            if (ad.GameType == vote)
            {
                _chat.SendMessage(player, vote switch
                {
                    PbGameType.Pub => "No change needed. Already playing PUB!",
                    PbGameType.ThreeH => "No change needed. Already playing 3H!",
                    PbGameType.Mini => "No change needed. Already playing Mini PB!",
                    _ => "No change needed.",
                });
                return false;
            }

            _chat.SendMessage(player, vote switch
            {
                PbGameType.Pub => "Changing to Pub!!",
                PbGameType.ThreeH => "Changing to 3H!!",
                PbGameType.Mini => "Changing to Mini PB.",
                _ => "Changing game.",
            });

            ChangeGameType(arena, ad, player, vote);
            return true;
        }

        #endregion

        #region Voting

        private void CountVotes(Player player, Arena arena, ArenaData ad, PbGameType vote, bool isCheck)
        {
            if (isCheck)
                vote = ad.GameType;

            int total = 0;
            int yesVote = 0;

            _playerData.Lock();
            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Arena != arena || otherPlayer.Status != PlayerState.Playing || otherPlayer.Ship == ShipType.Spec)
                        continue;

                    if (!otherPlayer.TryGetExtraData(_pdKey, out PlayerData? opd))
                        continue;

                    if (!isCheck && otherPlayer == player)
                        opd.ChangeGameTypeVote = vote;

                    total++;

                    if (opd.ChangeGameTypeVote == vote || (isCheck && opd.ChangeGameTypeVote == PbGameType.Any))
                        yesVote++;
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (isCheck)
            {
                _chat.SendMessage(player, $"Votes To Change Game: {total - yesVote}  Against: {yesVote}");
                return;
            }

            if (yesVote > total / 2)
            {
                ChangeGameType(arena, ad, player, vote);
            }
            else
            {
                _chat.SendMessage(player, $"Votes For: {yesVote}  Against: {total - yesVote}");
                _chat.SendArenaMessage(arena, "New vote lodged for game change.");
            }
        }

        private static bool TryParseGameType(ReadOnlySpan<char> arg, out PbGameType gameType, out string message)
        {
            if (arg.Length == 1)
            {
                switch (arg[0])
                {
                    case '2':
                        gameType = PbGameType.Pub;
                        message = "Your vote has been counted for Small Pub play!";
                        return true;
                    case '3':
                        gameType = PbGameType.ThreeH;
                        message = "Your vote has been counted for 3H play!";
                        return true;
                    case '4':
                        gameType = PbGameType.Mini;
                        message = "Your vote has been counted for Mini PB play!";
                        return true;
                    default:
                        gameType = PbGameType.Any;
                        message = $"Invalid game option {arg[0]}.";
                        return false;
                }
            }

            if (arg.Equals("mini", StringComparison.OrdinalIgnoreCase))
            {
                gameType = PbGameType.Mini;
                message = "Your vote has been counted for Mini PB play!";
                return true;
            }
            if (arg.Equals("pub", StringComparison.OrdinalIgnoreCase))
            {
                gameType = PbGameType.Pub;
                message = "Your vote has been counted for Small Pub play!";
                return true;
            }
            if (arg.Equals("3h", StringComparison.OrdinalIgnoreCase))
            {
                gameType = PbGameType.ThreeH;
                message = "Your vote has been counted for 3H play!";
                return true;
            }

            gameType = PbGameType.Any;
            message = $"Invalid game option {arg}.";
            return false;
        }

        private static bool TryParseGameTypeForMap(ReadOnlySpan<char> arg, out PbGameType gameType)
        {
            if (arg.Length == 1)
            {
                switch (arg[0])
                {
                    case '2': gameType = PbGameType.Pub; return true;
                    case '3': gameType = PbGameType.ThreeH; return true;
                    case '4': gameType = PbGameType.Mini; return true;
                    default: gameType = PbGameType.Any; return false;
                }
            }

            if (arg.Equals("mini", StringComparison.OrdinalIgnoreCase)) { gameType = PbGameType.Mini; return true; }
            if (arg.Equals("pub", StringComparison.OrdinalIgnoreCase)) { gameType = PbGameType.Pub; return true; }
            if (arg.Equals("3h", StringComparison.OrdinalIgnoreCase)) { gameType = PbGameType.ThreeH; return true; }

            gameType = PbGameType.Any;
            return false;
        }

        #endregion

        #region Team randomize

        private void RandomizeTeams(Arena arena, ArenaData ad)
        {
            Span<int> freqCounts = stackalloc int[2];
            freqCounts.Clear();

            int total = 0;

            _playerData.Lock();
            try
            {
                // Clear every player's randomize vote; count placeable players.
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena)
                        continue;

                    if (player.TryGetExtraData(_pdKey, out PlayerData? pd))
                        pd.RandomizeVote = false;

                    if (player.Status == PlayerState.Playing && player.Ship != ShipType.Spec)
                        total++;
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            int extra = total % 2;
            total /= 2;

            if (total == 0)
                return;

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena
                        || player.Status != PlayerState.Playing
                        || player.Freq > ad.MaxPubFreq
                        || player.Ship == ShipType.Spec)
                    {
                        continue;
                    }

                    if (freqCounts[1] == total)
                    {
                        // Team 1 is full; assign to team 0.
                        if (player.Freq != 0)
                            _game.SetShipAndFreq(player, ShipType.Warbird, 0);
                        else
                            _game.GivePrize(player, Prize.Warp, 1);
                    }
                    else if (freqCounts[0] == total + extra)
                    {
                        // Team 0 is full (including the odd extra); assign to team 1.
                        if (player.Freq != 1)
                            _game.SetShipAndFreq(player, ShipType.Javelin, 1);
                        else
                            _game.GivePrize(player, Prize.Warp, 1);
                    }
                    else
                    {
                        short random = (short)_prng.Number(0, 1);
                        freqCounts[random]++;
                        if (player.Freq != random)
                            _game.SetShipAndFreq(player, (ShipType)random, random);
                        else
                            _game.GivePrize(player, Prize.Warp, 1);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            _chat.SendArenaMessage(arena, "Teams Randomized!");
        }

        #endregion

        #region Side-game routing

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (newFreq > ad.MaxPubFreq && newFreq != ad.SpecFreq)
            {
                if (newFreq == ad.ScrambleFreq || newFreq == ad.ScrambleFreq + 1)
                {
                    bool warp = !(oldFreq == ad.ScrambleFreq || oldFreq == ad.ScrambleFreq + 1);
                    ApplyScrambleSettings(player, pd, ad, newFreq, warp);
                }
                else if (newFreq == ad.ProballFreq || newFreq == ad.ProballFreq + 1)
                {
                    bool warp = !(oldFreq == ad.ProballFreq || oldFreq == ad.ProballFreq + 1);
                    ApplyProballSettings(player, pd, ad, newFreq, warp);
                }
                else if (newFreq >= ad.Small4TmFreq && newFreq <= ad.Small4TmFreq + 3)
                {
                    bool warp = !(oldFreq >= ad.Small4TmFreq && oldFreq <= ad.Small4TmFreq + 3);
                    ApplySmall4TmSettings(player, pd, ad, newFreq, warp);
                }
            }
            else
            {
                // Joining a pub team or spec: clear any side-game overrides.
                UnoverrideAll(player, pd);

                if (oldFreq != ad.SpecFreq && oldFreq != (short)(1 - newFreq) && oldFreq != newFreq)
                    _game.GivePrize(player, Prize.Warp, 1);
            }

            _logManager.LogP(LogLevel.Info, nameof(MultiPub), player, $"freq ship change {oldShip}:{newShip} {oldFreq}:{newFreq}");

            // The remaining pub-freq rebalance logic was commented out in ASSS; nothing to do.
        }

        private void ApplyScrambleSettings(Player player, PlayerData pd, ArenaData ad, short newFreq, bool warp)
        {
            string ship = (newFreq % 2 == 0) ? "Warbird" : "Javelin";

            Override(player, pd, ship, "SoccerThrowTime", ad.ScrambleThrowTime);
            Override(player, pd, ship, "InitialEnergy", 1299);
            Override(player, pd, ship, "MaximumEnergy", 1300);
            Override(player, pd, "Kill", "EnterDelay", 300);
            Override(player, pd, "Soccer", "DisableWallPass", 0);
            Override(player, pd, "Spawn", "Team2-X", 512);
            Override(player, pd, "Spawn", "Team2-Y", 677);
            Override(player, pd, "Spawn", "Team2-Radius", 10);
            Override(player, pd, "Spawn", "Team3-X", 512);
            Override(player, pd, "Spawn", "Team3-Y", 677);
            Override(player, pd, "Spawn", "Team3-Radius", 10);

            _clientSettings.SendClientSettings(player);
            _game.SetShipAndFreq(player, player.Ship, newFreq);
            if (warp)
                _game.GivePrize(player, Prize.Warp, 1);

            _logManager.LogP(LogLevel.Info, nameof(MultiPub), player, "Sent to Scramble");
        }

        private void ApplyProballSettings(Player player, PlayerData pd, ArenaData ad, short newFreq, bool warp)
        {
            string ship = (newFreq % 2 == 0) ? "Warbird" : "Javelin";

            Override(player, pd, ship, "SoccerThrowTime", ad.ProballThrowTime);
            Override(player, pd, ship, "InitialEnergy", 1299);
            Override(player, pd, ship, "MaximumEnergy", 1300);
            Override(player, pd, "Kill", "EnterDelay", 200);
            Override(player, pd, "Soccer", "DisableWallPass", 0);
            Override(player, pd, "Spawn", "Team2-X", 512);
            Override(player, pd, "Spawn", "Team2-Y", 890);
            Override(player, pd, "Spawn", "Team2-Radius", 16);
            Override(player, pd, "Spawn", "Team3-X", 512);
            Override(player, pd, "Spawn", "Team3-Y", 890);
            Override(player, pd, "Spawn", "Team3-Radius", 16);

            _clientSettings.SendClientSettings(player);
            _game.SetShipAndFreq(player, player.Ship, newFreq);
            if (warp)
                _game.GivePrize(player, Prize.Warp, 1);

            _logManager.LogP(LogLevel.Info, nameof(MultiPub), player, "Sent to Proball");
        }

        private void ApplySmall4TmSettings(Player player, PlayerData pd, ArenaData ad, short newFreq, bool warp)
        {
            Override(player, pd, "Warbird", "SoccerThrowTime", ad.Small4TmThrowTime);
            Override(player, pd, "Javelin", "SoccerThrowTime", ad.Small4TmThrowTime);
            Override(player, pd, "Leviathan", "SoccerThrowTime", ad.Small4TmThrowTime);
            Override(player, pd, "Spider", "SoccerThrowTime", ad.Small4TmThrowTime);
            Override(player, pd, "Warbird", "InitialEnergy", 1299);
            Override(player, pd, "Warbird", "MaximumEnergy", 1300);
            Override(player, pd, "Kill", "EnterDelay", 200);
            Override(player, pd, "Soccer", "DisableWallPass", 0);

            for (int i = 0; i < 4; i++)
            {
                Override(player, pd, "Spawn", $"Team{i}-X", ad.Small4TmCenterX);
                Override(player, pd, "Spawn", $"Team{i}-Y", ad.Small4TmCenterY);
                Override(player, pd, "Spawn", $"Team{i}-Radius", 3);
            }

            _clientSettings.SendClientSettings(player);
            _game.SetShipAndFreq(player, player.Ship, newFreq);
            if (warp)
                _game.GivePrize(player, Prize.Warp, 1);

            _logManager.LogP(LogLevel.Info, nameof(MultiPub), player, "Sent to Small4Tm");
        }

        private void Override(Player player, PlayerData pd, string section, string key, int value)
        {
            if (!_clientSettings.TryGetSettingsIdentifier(section, key, out ClientSettingIdentifier id))
            {
                _logManager.LogM(LogLevel.Warn, nameof(MultiPub), $"Unknown client setting {section}:{key}.");
                return;
            }

            _clientSettings.OverrideSetting(player, id, value);
            pd.ClientOverrides.Add(id);
        }

        private void UnoverrideAll(Player player, PlayerData pd)
        {
            if (pd.ClientOverrides.Count == 0)
                return;

            foreach (ClientSettingIdentifier id in pd.ClientOverrides)
                _clientSettings.UnoverrideSetting(player, id);

            pd.ClientOverrides.Clear();
            _clientSettings.SendClientSettings(player);
        }

        #endregion

        #region Callbacks

        private void Callback_BallPickup(Arena arena, Player player, byte ballId)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((ad.CustomGameMask & (1 << ballId)) != 0)
                return;

            if (!ad.RoundStart)
                RoundStart(arena, ad);

            ad.RoundStart = true;
        }

        private void Callback_BallGameStart(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            RoundStart(arena, ad);
        }

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.RoundStart = false;

            if (ad.GameType == PbGameType.Pro || !ad.RandomizeTeamsOnEnd)
                return;

            if (!ad.PubMode)
                return;

            RandomizeTeams(arena, ad);
        }

        private void Callback_BallGameGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((ad.CustomGameMask & (1 << ballId)) != 0)
                return;

            if (ad.ChangeMapOnGoal != "-")
            {
                // Defer the map change slightly so it doesn't run inside the goal handling.
                _mainloopTimer.SetTimer(MainloopTimer_ChangeMapOnGoal, 100, Timeout.Infinite, arena, arena);
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action != PlayerAction.EnterArena || arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _chat.SendMessage(player, "Welcome to PowerBall! ");
            _chat.SendMessage(player, "This arena is powered by the PB Module!");
            _chat.SendMessage(player, "Type ?pbhelp for available commands.");
            _chat.SendMessage(player, ad.GameType switch
            {
                PbGameType.Pub => "Currently playing Small Pub!",
                PbGameType.ThreeH => "Currently playing Small 3H!",
                PbGameType.Pro => "Currently playing Proball!",
                PbGameType.Scramble => "Currently playing Scramble!",
                PbGameType.Mini => "Currently playing Mini PB!",
                _ => "Currently no game selected!",
            });
        }

        #endregion

        #region Round start / change-map-on-goal

        private void RoundStart(Arena arena, ArenaData ad)
        {
            if (!ad.PubMode)
                return;

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena
                        || player.Status != PlayerState.Playing
                        || player.Freq > ad.MaxPubFreq
                        || player.Ship == ShipType.Spec)
                    {
                        continue;
                    }

                    if (!IsValidGameFreq(ad.GameType, player.Freq))
                    {
                        short random = (short)_prng.Number(0, 1);
                        _game.SetShipAndFreq(player, (ShipType)random, random);
                    }
                    else if ((int)player.Ship != player.Freq)
                    {
                        _game.SetShipAndFreq(player, (ShipType)player.Freq, player.Freq);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private static bool IsValidGameFreq(PbGameType gameType, short freq)
        {
            // For all current game types, only freqs 0 and 1 are valid pub freqs.
            _ = gameType;
            return freq <= 1;
        }

        private bool MainloopTimer_ChangeMapOnGoal(Arena arena)
        {
            if (arena.TryGetExtraData(_adKey, out ArenaData? ad) && ChangeMapSilent(arena, ad))
                _balls.TrySpawnBall(arena, 0);

            if (ad is not null)
                ad.ResetScores = true;

            return false; // one-shot
        }

        private bool ChangeMapSilent(Arena arena, ArenaData ad)
        {
            if (!ad.PubMode)
            {
                ad.ChangeMapOnGoal = "-";
                return false;
            }

            // Need a player to act as the "requester" for ResetGame; use the first player in the arena.
            Player? requester = null;
            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena)
                    {
                        requester = player;
                        break;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (requester is null)
            {
                ad.ChangeMapOnGoal = "-";
                return false;
            }

            if (!TryParseGameTypeForMap(ad.ChangeMapOnGoal, out PbGameType vote))
            {
                ad.ChangeMapOnGoal = "-";
                return false;
            }

            if (ad.GameType == vote)
            {
                ad.ChangeMapOnGoal = "-";
                return false;
            }

            ChangeGameType(arena, ad, requester, vote);
            _logManager.LogA(LogLevel.Info, nameof(MultiPub), arena, $"Map changed on goal to {ad.ChangeMapOnGoal}");
            ad.ChangeMapOnGoal = "-";
            return true;
        }

        #endregion

        #region Help

        private void PrintHelp(Player player)
        {
            if (player.Arena is null || !player.Arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _chat.SendMessage(player, "----------------------------------------------------");
            _chat.SendMessage(player, "The following PB Module commands are available:");
            _chat.SendMessage(player, "----------------------------------------------------");
            DisplayCommand(player, "?pbhelp", "Display this help list");
            DisplayCommand(player, "?pbversion", "Display the PB Module version information");

            if (ad.GameType == PbGameType.Scramble)
            {
                _chat.SendMessage(player, "---------- SCRAMBLE ARENA Commands ------------------");
                DisplayCommand(player, "?rules", "Display the rules of scramble");
                DisplayCommand(player, "?startgm", "Start a scramble game!");
                DisplayCommand(player, "?stopgm", "Pre-maturely stop a scramble game");
            }
            else
            {
                DisplayCommand(player, "?randomize", "Log your vote for randomizing the teams");
                DisplayCommand(player, "?changegame <opt>", "Log your vote for changing the Game Type");
                DisplayCommand(player, "", "<opt> = 2 or 'pub' (Public)");
                DisplayCommand(player, "", "<opt> = 3 or '3h' (Smallpub3h)");
                DisplayCommand(player, "", "<opt> = 4 or 'mini' (MiniPB)");
                DisplayCommand(player, "?cg <opt>", "Alias for changegame. Log your vote for changing the Game Type");
            }

            bool displayedMod = false;
            DisplayModCommand(player, ref displayedMod, "randomnow", "?randomnow", "Randomize the teams and restart the game");
            DisplayModCommand(player, ref displayedMod, "changenow", "?changenow", "Change Game Type and restart the game");
            DisplayModCommand(player, ref displayedMod, "changemap", "?changemap", "Change Game Type but continue game");
            DisplayModCommand(player, ref displayedMod, "changemapongoal", "?changemapongoal", "Change Game Type on next goal but continue game");
            DisplayModCommand(player, ref displayedMod, "togglerandom", "?togglerandom [ON/OFF]", "Display or Enable/Disable if teams are randomized at the end of a game.");
            DisplayModCommand(player, ref displayedMod, "pubmode", "?pubmode [ON/OFF]", "Display or Enable/Disable Pub Mode.");
        }

        private void DisplayCommand(Player player, string command, string description)
        {
            _chat.SendMessage(player, $"{command,-35} - {description}");
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

        #region Helper types

        private readonly record struct GameTypeSettings(
            int SpawnRadius, int SpawnX, int SpawnY,
            int Team0Radius, int Team0X, int Team0Y,
            int Team1Radius, int Team1X, int Team1Y,
            int PassDelay, int DisableWallPass,
            int BombAliveTime, int BulletDamageLevel, int BulletAliveTime, int MineAliveTime, int BurstDamageLevel,
            int WarpRadiusLimit, int EnterDelay,
            int BurstMax, int WarbirdFireEnergy, int JavelinFireEnergy, int PortalMax, int SoccerThrowTime,
            int BrickMax, int RepelMax, int InitialEnergy, int MaximumEnergy, int RepelPrize);

        // Small Pub (also used for Pro).
        private static readonly GameTypeSettings PubSettings = new(
            SpawnRadius: 20, SpawnX: 512, SpawnY: 399,
            Team0Radius: 20, Team0X: 512, Team0Y: 399,
            Team1Radius: 20, Team1X: 512, Team1Y: 399,
            PassDelay: 10, DisableWallPass: 1,
            BombAliveTime: 6000, BulletDamageLevel: 250, BulletAliveTime: 250, MineAliveTime: 4500, BurstDamageLevel: 300,
            WarpRadiusLimit: 125, EnterDelay: 500,
            BurstMax: 1, WarbirdFireEnergy: 20, JavelinFireEnergy: 30, PortalMax: 1, SoccerThrowTime: 700,
            BrickMax: 1, RepelMax: 0, InitialEnergy: 1500, MaximumEnergy: 1500, RepelPrize: 0);

        // Small 3H.
        private static readonly GameTypeSettings ThreeHSettings = new(
            SpawnRadius: 3, SpawnX: 512, SpawnY: 53,
            Team0Radius: 3, Team0X: 512, Team0Y: 53,
            Team1Radius: 3, Team1X: 512, Team1Y: 53,
            PassDelay: 20, DisableWallPass: 0,
            BombAliveTime: 3000, BulletDamageLevel: 266, BulletAliveTime: 550, MineAliveTime: 4500, BurstDamageLevel: 266,
            WarpRadiusLimit: 8, EnterDelay: 200,
            BurstMax: 0, WarbirdFireEnergy: 20, JavelinFireEnergy: 30, PortalMax: 2, SoccerThrowTime: 775,
            BrickMax: 0, RepelMax: 0, InitialEnergy: 1300, MaximumEnergy: 1300, RepelPrize: 0);

        // Mini PowerBall (distinct Team0/Team1 spawn points).
        private static readonly GameTypeSettings MiniSettings = new(
            SpawnRadius: 10, SpawnX: 512, SpawnY: 161,
            Team0Radius: 15, Team0X: 449, Team0Y: 161,
            Team1Radius: 15, Team1X: 574, Team1Y: 161,
            PassDelay: 20, DisableWallPass: 1,
            BombAliveTime: 3000, BulletDamageLevel: 266, BulletAliveTime: 250, MineAliveTime: 4500, BurstDamageLevel: 266,
            WarpRadiusLimit: 8, EnterDelay: 400,
            BurstMax: 0, WarbirdFireEnergy: 20, JavelinFireEnergy: 30, PortalMax: 2, SoccerThrowTime: 775,
            BrickMax: 1, RepelMax: 0, InitialEnergy: 1500, MaximumEnergy: 1500, RepelPrize: 0);

        private sealed class ArenaData : IResettable
        {
            public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? EnforcerAdvisorToken;
            public InterfaceRegistrationToken<IMultiPub>? InterfaceToken;

            // runtime state
            public PbGameType GameType;
            public bool RandomizeTeamsOnEnd;
            public bool RoundStart;
            public bool PubMode;
            public bool ResetScores;
            public string ChangeMapOnGoal = "-";

            // config-derived
            public int MaxPubFreq;
            public int ScrambleFreq;
            public int Small4TmFreq;
            public int ProballFreq;
            public int SpecFreq;
            public int PrivFreqStart;
            public int ScrambleThrowTime;
            public int ProballThrowTime;
            public int Small4TmThrowTime;
            public int Small4TmCenterX;
            public int Small4TmCenterY;
            public int CustomGameMask;

            public bool TryReset()
            {
                EnforcerAdvisorToken = null;
                InterfaceToken = null;
                GameType = PbGameType.Any;
                RandomizeTeamsOnEnd = true;
                RoundStart = false;
                PubMode = true;
                ResetScores = true;
                ChangeMapOnGoal = "-";
                MaxPubFreq = 1;
                ScrambleFreq = 10;
                Small4TmFreq = 20;
                ProballFreq = 30;
                SpecFreq = 420;
                PrivFreqStart = 3;
                ScrambleThrowTime = 1000;
                ProballThrowTime = 790;
                Small4TmThrowTime = 1000;
                Small4TmCenterX = 512;
                Small4TmCenterY = 512;
                CustomGameMask = 0;
                return true;
            }
        }

        private sealed class PlayerData : IResettable
        {
            public bool RandomizeVote;
            public PbGameType ChangeGameTypeVote;
            public readonly List<ClientSettingIdentifier> ClientOverrides = [];

            public bool TryReset()
            {
                RandomizeVote = false;
                ChangeGameTypeVote = PbGameType.Any;
                ClientOverrides.Clear();
                return true;
            }
        }

        #endregion
    }
}
