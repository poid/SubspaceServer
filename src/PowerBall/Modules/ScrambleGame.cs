using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// The "Scramble" side-game — a port of the ASSS <c>scramble</c> module.
    /// </summary>
    /// <remarks>
    /// A timed doubling-goal soccer mini-game played on a dedicated freq band inside the main pub arena. It auto-starts
    /// when the scramble freqs fill to 3v3 or 4v4 (in non-league arenas), plays a ~20 second countdown, runs a 15 minute
    /// clock, starts the goal value at 1 and doubles it every 12 seconds of continuous possession (capped at 32, resets on
    /// turnover), and the first team past 64 points (or the leader at time-up, else sudden death) wins.
    /// <para>
    /// Deviations from the C original (idiomatic): all state is touched only on the mainloop thread, so the recursive
    /// per-arena mutex is removed; goals are filtered by the scramble ball id (the C did not filter, which could cross-talk
    /// with other balls in a MultiPub arena); the dead <c>secsleft</c> computation in the pickup handler is dropped; and
    /// the "20 seconds" / "game stopped" announcements are sent once to the arena rather than once per matching player.
    /// </para>
    /// </remarks>
    [ModuleInfo("Scramble side-game (ASSS scramble port): timed doubling-goal soccer with auto-start and a countdown.")]
    public sealed class ScrambleGame : IModule, IArenaAttachableModule
    {
        // Audience rectangle (tiles); a spectator inside it counts as a scramble participant.
        private const int RectMinX = 388;
        private const int RectMaxX = 635;
        private const int RectMinY = 584;
        private const int RectMaxY = 771;

        private const int GoalValueCap = 32;
        private const int WinScore = 64; // strictly greater than this wins (i.e. >= 65)

        private readonly IArenaManager _arenaManager;
        private readonly IBalls _balls;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        public ScrambleGame(
            IArenaManager arenaManager,
            IBalls balls,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
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

            LoadConfig(arena, ad);

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            BallGoalCallback.Register(arena, Callback_BallGoal);
            BallPickupCallback.Register(arena, Callback_BallPickup);
            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);

            _commandManager.AddCommand("startgm", Command_startgm, arena);
            _commandManager.AddCommand("stopgm", Command_stopgm, arena);
            _commandManager.AddCommand("rules", Command_rules, arena);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _mainloopTimer.ClearTimer<Arena>(Timer_PreGame, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_DumpStats, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);

            _commandManager.RemoveCommand("startgm", Command_startgm, arena);
            _commandManager.RemoveCommand("stopgm", Command_stopgm, arena);
            _commandManager.RemoveCommand("rules", Command_rules, arena);

            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            BallGoalCallback.Unregister(arena, Callback_BallGoal);
            BallPickupCallback.Unregister(arena, Callback_BallPickup);
            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);

            return true;
        }

        #endregion

        #region Config

        [ConfigHelp<int>("MultiPub", "ScrambleFreq", ConfigScope.Arena, Default = 0, Description = "Base freq of the scramble game (uses this and +1).")]
        [ConfigHelp<int>("MultiPub", "ScrambleCenterX", ConfigScope.Arena, Default = 512, Description = "Center X (tiles) of the scramble field.")]
        [ConfigHelp<int>("MultiPub", "ScrambleCenterY", ConfigScope.Arena, Default = 512, Description = "Center Y (tiles) of the scramble field.")]
        [ConfigHelp<int>("MultiPub", "ScrambleBall", ConfigScope.Arena, Default = 0, Description = "The ball id used by the scramble game.")]
        [ConfigHelp<int>("Misc", "LeagueArena", ConfigScope.Arena, Default = 0, Description = "If set, an external bot controls the game (no auto-start/countdown/clock).")]
        private void LoadConfig(Arena arena, ArenaData ad)
        {
            ConfigHandle ch = arena.Cfg!;
            ad.StartFreq = _configManager.GetInt(ch, "MultiPub", "ScrambleFreq", 0);
            ad.MapCenterX = _configManager.GetInt(ch, "MultiPub", "ScrambleCenterX", 512);
            ad.MapCenterY = _configManager.GetInt(ch, "MultiPub", "ScrambleCenterY", 512);
            ad.BallId = _configManager.GetInt(ch, "MultiPub", "ScrambleBall", 0);
            ad.LeagueArena = _configManager.GetInt(ch, "Misc", "LeagueArena", 0) != 0;
        }

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create)
            {
                LoadConfig(arena, ad);
                ad.GameState = 0;
                ad.BallFreq = -1;
                ad.GoalValue = 0;
            }
            else if (action == ArenaAction.ConfChanged)
            {
                ad.LeagueArena = _configManager.GetInt(arena.Cfg!, "Misc", "LeagueArena", 0) != 0;
            }
            else if (action == ArenaAction.Destroy)
            {
                _mainloopTimer.ClearTimer<Arena>(Timer_PreGame, arena);
                _mainloopTimer.ClearTimer<Arena>(Timer_DumpStats, arena);
                _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);
            }
        }

        #endregion

        #region Commands

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Starts a Scramble game.")]
        private void Command_startgm(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is { } arena && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                StartGame(arena, ad);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Stops a Scramble game.")]
        private void Command_stopgm(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is { } arena && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                StopGame(arena, ad);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Describes the rules for the scramble game.")]
        private void Command_rules(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "Rules:");
            _chat.SendMessage(player, "The first team to score 65 points wins. Games are timed at 15 mins max.");
            _chat.SendMessage(player, "Goal value starts at 1 point and doubles every 12 seconds while your team possesses the ball.");
            _chat.SendMessage(player, "A turnover causes the goal value to be reset to 1 point.");
            _chat.SendMessage(player, "A goal can reach up to 32 points, then resets back to 1 point after 12 seconds.");
            _chat.SendMessage(player, "Wall-passing is allowed.");
        }

        #endregion

        #region Game control

        private void StartGame(Arena arena, ArenaData ad)
        {
            ad.Scores[0] = ad.Scores[1] = 0;

            if (!ad.LeagueArena)
                _chat.SendArenaMessage(arena, "Game will begin in 20 seconds.");

            ad.GameState = 1;
            ad.BallFreq = -1;
            ad.GoalValue = 1;

            _mainloopTimer.ClearTimer<Arena>(Timer_DumpStats, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_PreGame, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);

            ArmPreGame(arena, ad.LeagueArena ? 10 : 15000);
        }

        private void StopGame(Arena arena, ArenaData ad)
        {
            ad.GameState = 0;

            _mainloopTimer.ClearTimer<Arena>(Timer_DumpStats, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_PreGame, arena);
            _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);

            _chat.SendArenaMessage(arena, "Game stopped.");
        }

        private void ArmPreGame(Arena arena, int delayMs)
        {
            _mainloopTimer.SetTimer(Timer_PreGame, delayMs, Timeout.Infinite, arena, arena);
        }

        #endregion

        #region Timers

        private bool Timer_PreGame(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.LeagueArena)
            {
                // League: the external bot owns the game. Jump to active and do nothing else.
                ad.BallFreq = -1;
                ad.GameState = 10;
                return false;
            }

            HashSet<Player> audience = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetScrambleAudience(arena, ad, audience);

                switch (ad.GameState)
                {
                    case 1:
                        foreach (Player p in audience)
                        {
                            DepleteShip(p, ad);
                            _chat.SendMessage(p, "Game will begin in 5 seconds.");
                        }
                        ad.GameState = 2;
                        ArmPreGame(arena, 2000);
                        break;

                    case 2:
                        foreach (Player p in audience)
                            _chat.SendMessage(p, "SCORE: Warbirds:0  Javelins:0");
                        ad.GameState = 3;
                        ad.BallFreq = -1;
                        ArmPreGame(arena, 1000);
                        break;

                    case 3:
                        foreach (Player p in audience)
                            _chat.SendMessage(p, PbSound.Ready, "READY");
                        ad.GameState = 4;
                        ArmPreGame(arena, 1000);
                        break;

                    case 4:
                        ad.GameState = 5;
                        foreach (Player p in audience)
                        {
                            _chat.SendMessage(p, PbSound.Ready, "SET");
                            DepleteShip(p, ad);
                        }
                        PlaceScrambleBall(arena, ad);
                        ArmPreGame(arena, 1000);
                        break;

                    case 5:
                        foreach (Player p in audience)
                        {
                            _game.ShipReset(p);
                            _chat.SendMessage(p, PbSound.Go, "GO!!!");
                        }
                        ad.GameState = 10;
                        _mainloopTimer.SetTimer(Timer_DumpStats, 901000, Timeout.Infinite, arena, arena);
                        ArmPreGame(arena, 1000);
                        break;

                    case 10:
                    default:
                        // no-op; the chain ends here
                        break;
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(audience);
            }

            return false; // one-shot; each step re-arms the next
        }

        private bool Timer_DumpStats(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.GameState == 0)
                return false;

            HashSet<Player> audience = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetScrambleAudience(arena, ad, audience);

                if (ad.Scores[0] == ad.Scores[1])
                {
                    foreach (Player p in audience)
                        _chat.SendMessage(p, "Sudden Death Overtime. Next goal wins!");
                    ad.GameState = 11;
                }
                else
                {
                    foreach (Player p in audience)
                    {
                        _chat.SendMessage(p, $"SCORE: Warbirds:{ad.Scores[0]} Javelins:{ad.Scores[1]}");
                        _chat.SendMessage(p, "Soccer game over.");
                    }
                    ad.GameState = 0;
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(audience);
            }

            return false;
        }

        private bool Timer_UpGoalValue(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.GameState == 0)
                return false;

            bool overflow = ad.GoalValue == GoalValueCap;
            ad.GoalValue = overflow ? 1 : ad.GoalValue * 2;
            ChatSound sound = overflow ? ChatSound.Aww : ChatSound.None;

            HashSet<Player> audience = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                GetScrambleAudience(arena, ad, audience);
                foreach (Player p in audience)
                    _chat.SendMessage(p, sound, $"Goal: {ad.GoalValue}");
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(audience);
            }

            return true; // keep doubling
        }

        #endregion

        #region Gameplay callbacks

        private void Callback_BallGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ballId != ad.BallId || ad.GameState == 0)
                return;

            HashSet<Player> teamSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> enemySet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> audience = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Arena != arena)
                            continue;

                        if (p.Status == PlayerState.Playing && (p.Freq == ad.StartFreq || p.Freq == ad.StartFreq + 1))
                        {
                            if (p.Freq == player.Freq)
                                teamSet.Add(p);
                            else
                                enemySet.Add(p);
                        }

                        if (IsScramblePlayerOrSpec(p, ad))
                            audience.Add(p);
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                string plural = ad.GoalValue == 1 ? "" : "s";
                _chat.SendSetMessage(teamSet, ChatSound.Goal, $"Team Goal! by {player.Name}: {ad.GoalValue} point{plural}");
                _chat.SendSetMessage(enemySet, ChatSound.Goal, $"Enemy Goal! by {player.Name}: {ad.GoalValue} point{plural}");

                // Score by ship: Warbird => team 0, anything else => team 1.
                if (player.Ship == ShipType.Warbird)
                    ad.Scores[0] += ad.GoalValue;
                else
                    ad.Scores[1] += ad.GoalValue;

                _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);

                foreach (Player p in audience)
                    _chat.SendMessage(p, $"SCORE: Warbirds:{ad.Scores[0]}  Javelins:{ad.Scores[1]}");

                if (ad.Scores[0] > WinScore || ad.Scores[1] > WinScore || ad.GameState == 11)
                {
                    _mainloopTimer.ClearTimer<Arena>(Timer_DumpStats, arena);
                    ad.GameState = 0;

                    foreach (Player p in audience)
                        _chat.SendMessage(p, ChatSound.Ding, "Soccer game over.");
                }

                ad.BallFreq = -1;
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(teamSet);
                _objectPoolManager.PlayerSetPool.Return(enemySet);
                _objectPoolManager.PlayerSetPool.Return(audience);
            }
        }

        private void Callback_BallPickup(Arena arena, Player player, byte ballId)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            int team = player.Ship == ShipType.Warbird ? 0 : 1;

            if (ballId == ad.BallId && ad.GameState >= 10 && ad.BallFreq != team)
            {
                HashSet<Player> audience = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    GetScrambleAudience(arena, ad, audience);
                    foreach (Player p in audience)
                        _chat.SendMessage(p, "Goal: 1");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(audience);
                }

                ad.GoalValue = 1;
                ad.BallFreq = team;
                _mainloopTimer.ClearTimer<Arena>(Timer_UpGoalValue, arena);
                _mainloopTimer.SetTimer(Timer_UpGoalValue, 12000, 12000, arena, arena);
            }
        }

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            // Strip prizes on any ship/freq change onto a scramble freq before the game is active.
            if (ad.GameState < 10 && (player.Freq == ad.StartFreq || player.Freq == ad.StartFreq + 1))
                DepleteShip(player, ad);

            // Auto-start when the scramble freqs fill to exactly 3v3 or 4v4 (non-league only).
            if (ad.GameState != 0 || ad.LeagueArena)
                return;

            int birds = 0;
            int javs = 0;

            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena != arena)
                        continue;

                    if (p.Freq == ad.StartFreq)
                        birds++;
                    else if (p.Freq == ad.StartFreq + 1)
                        javs++;
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if ((birds == 4 && javs == 4) || (birds == 3 && javs == 3))
            {
                StartGame(arena, ad);

                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.Lock();
                    try
                    {
                        foreach (Player p in _playerData.Players)
                        {
                            if (p.Arena == arena && (p.Freq == ad.StartFreq || p.Freq == ad.StartFreq + 1))
                                set.Add(p);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    foreach (Player p in set)
                        DepleteShip(p, ad);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }
        }

        #endregion

        #region Helpers

        private void PlaceScrambleBall(Arena arena, ArenaData ad)
        {
            BallData bd = default;
            bd.State = BallState.OnMap;
            bd.X = (short)(16 * ad.MapCenterX);
            bd.Y = (short)(16 * ad.MapCenterY);
            bd.XSpeed = 0;
            bd.YSpeed = 0;
            bd.Freq = 0;
            bd.Carrier = null;
            bd.Time = ServerTick.Now;

            _balls.TryPlaceBall(arena, (byte)ad.BallId, ref bd);
        }

        private void DepleteShip(Player player, ArenaData ad)
        {
            for (short i = 1; i < 29; i++)
                _game.GivePrize(player, (Prize)(-i), 5);

            short x = (short)(player.Ship == ShipType.Warbird ? ad.MapCenterX - 108 : ad.MapCenterX + 107);
            _game.WarpTo(player, x, (short)ad.MapCenterY);
        }

        private void GetScrambleAudience(Arena arena, ArenaData ad, HashSet<Player> set)
        {
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena == arena && IsScramblePlayerOrSpec(p, ad))
                        set.Add(p);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private static bool IsScramblePlayerOrSpec(Player p, ArenaData ad)
        {
            if (p.Freq == ad.StartFreq || p.Freq == ad.StartFreq + 1)
                return true;

            // Spectators inside the scramble rectangle (rectangle in tiles, position is pixels = tile*16).
            return p.Position.X >= RectMinX * 16 && p.Position.X <= RectMaxX * 16
                && p.Position.Y >= RectMinY * 16 && p.Position.Y <= RectMaxY * 16;
        }

        #endregion

        #region Helper types

        private sealed class ArenaData : IResettable
        {
            /// <summary>0 = idle, 1..5 = countdown, 10 = active, 11 = sudden-death overtime.</summary>
            public int GameState;

            public readonly int[] Scores = new int[2];
            public int GoalValue;
            public int StartFreq;
            public int BallId;

            /// <summary>Team currently in possession: -1 = neutral, 0 = warbirds, 1 = javelins.</summary>
            public int BallFreq;

            public int MapCenterX;
            public int MapCenterY;
            public bool LeagueArena;

            public bool TryReset()
            {
                GameState = 0;
                Scores[0] = Scores[1] = 0;
                GoalValue = 0;
                StartFreq = 0;
                BallId = 0;
                BallFreq = -1;
                MapCenterX = 512;
                MapCenterY = 512;
                LeagueArena = false;
                return true;
            }
        }

        #endregion
    }
}
