using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.PowerBall.ComponentInterfaces;
using System;
using System.Threading;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// PowerBall LVZ scoreboard, indicators, banners, sounds, and game clock — a port of the ASSS <c>pblvzs</c> module.
    /// </summary>
    /// <remarks>
    /// Drives the on-screen LVZ objects and "bong" sounds for a PowerBall game: who has the ball, who is leading, the
    /// game-point indicators, the animated score digits, the win / game-over / new-game banners, and (for league play)
    /// a MM:SS game clock rendered from LVZ digit objects.
    /// <para>
    /// The game is designed for two teams: freq 0 = Warbirds, any other freq = Javelins.
    /// </para>
    /// <para>
    /// Deviations from the C original (idiomatic): the per-enter LVZ resend is dropped because the C# server records
    /// arena-target LVZ state and re-sends it automatically when a player enters; the external game-timer mirroring
    /// (CB_TIMEPAUSE/RESUME/SET) is deferred to the league slice since MultiPub does not use a game clock; and two C bugs
    /// are fixed (the ResetLVZs Javelin-ones-digit clear, and the branch-C game-point re-fire guard).
    /// </para>
    /// </remarks>
    [ModuleInfo("PowerBall LVZ scoreboard/indicators/banners/sounds and game clock (ASSS pblvzs port).")]
    public sealed class PowerBallLvz : IModule, IArenaAttachableModule, IPowerBallLvz
    {
        private const int MaxFreqs = 8;

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly ILvzObjects _lvzObjects;
        private readonly IMainloopTimer _mainloopTimer;

        private ArenaDataKey<ArenaData> _adKey;

        public PowerBallLvz(
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILvzObjects lvzObjects,
            IMainloopTimer mainloopTimer)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
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

            BallPickupCallback.Register(arena, Callback_BallPickup);
            BallShootCallback.Register(arena, Callback_BallShoot);
            BallGameGoalCallback.Register(arena, Callback_BallGameGoal);
            BallGameStartCallback.Register(arena, Callback_BallGameStart);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);

            _commandManager.AddCommand("pblvzsversion", Command_pblvzsversion, arena);

            ad.InterfaceToken = arena.RegisterInterface<IPowerBallLvz>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.InterfaceToken is not null)
                arena.UnregisterInterface(ref ad.InterfaceToken);

            _mainloopTimer.ClearTimer<Arena>(Timer_GameDisplay, arena);
            _mainloopTimer.ClearTimer<GameOverState>(Timer_AnnounceGameOver, arena);
            _mainloopTimer.ClearTimer<GameOverState>(Timer_AnnounceWinner, arena);
            _mainloopTimer.ClearTimer<DelayedSoundState>(Timer_PlayDelayed, arena);

            _commandManager.RemoveCommand("pblvzsversion", Command_pblvzsversion, arena);

            BallPickupCallback.Unregister(arena, Callback_BallPickup);
            BallShootCallback.Unregister(arena, Callback_BallShoot);
            BallGameGoalCallback.Unregister(arena, Callback_BallGameGoal);
            BallGameStartCallback.Unregister(arena, Callback_BallGameStart);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);

            return true;
        }

        #endregion

        #region IPowerBallLvz

        void IPowerBallLvz.StartGameTimer(Arena arena, int seconds) => StartGameTimer(arena, seconds);

        void IPowerBallLvz.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region Setup

        [ConfigHelp<int>("Soccer", "CapturePoints", ConfigScope.Arena, Default = 0, Description = "See the scoring module; used here (negated) to compute the game-point threshold.")]
        [ConfigHelp<int>("Soccer", "WinBy", ConfigScope.Arena, Default = 0, Description = "See the scoring module; used here to compute the game-point threshold.")]
        private void InitializeArena(Arena arena, ArenaData ad)
        {
            ConfigHandle ch = arena.Cfg!;
            ad.CapturePoints = _configManager.GetInt(ch, "Soccer", "CapturePoints", 0) * -1;
            ad.WinBy = _configManager.GetInt(ch, "Soccer", "WinBy", 0);
            ad.LastScoringFreq = -1;
        }

        private int GetCustomGameMask(Arena arena) => _configManager.GetInt(arena.Cfg!, "Soccer", "CustomGame", 0);

        #endregion

        #region Ball possession

        private void Callback_BallPickup(Arena arena, Player player, byte ballId)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            for (int freq = 0; freq < MaxFreqs; freq++)
            {
                if (freq == player.Freq)
                {
                    ad.HasBall[freq] = true;
                    SetBall(arena, freq, true, ad);
                }
                else if (ad.HasBall[freq])
                {
                    ad.HasBall[freq] = false;
                    SetBall(arena, freq, false, ad);
                }
            }
        }

        private void Callback_BallShoot(Arena arena, Player player, byte ballId)
        {
            // Covers the ASSS CB_BALLFIRE and CB_BALLKILL cases (any loss of possession). Turns off the team's ball
            // indicator. (Faithfully does not clear HasBall[]; the next pickup reconciles it.)
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            SetBall(arena, player.Freq, false, ad);
        }

        #endregion

        #region Goal scored

        private void Callback_BallGameGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            Span<int> scores = stackalloc int[MaxFreqs];
            ReadScores(arena, scores);

            short freq = player.Freq;
            if (freq < 0 || freq >= MaxFreqs)
                return;

            bool isGamePointGoal = ad.HasGamePoint[freq];
            SetScoreDigits(arena, freq, scores[freq], isGamePointGoal, ad);

            int otherFreq = (freq + 1) % 2;

            if (scores[freq] > scores[otherFreq])
            {
                // Scoring freq now leads: clear the other teams' indicators.
                for (int i = 0; i < MaxFreqs; i++)
                {
                    if (i == freq)
                        continue;

                    if (ad.HasGamePoint[i])
                    {
                        SetGamePoint(arena, i, false, ad);
                        ad.HasGamePoint[i] = false;
                    }

                    if (ad.HasLead[i])
                    {
                        SetLead(arena, i, false, ad);
                        ad.HasLead[i] = false;
                    }
                }

                if ((scores[freq] - scores[otherFreq]) >= (ad.WinBy - 1) && scores[freq] > (ad.CapturePoints - 2))
                {
                    // Game point for the scoring freq.
                    if (ad.HasLead[freq])
                    {
                        ad.HasLead[freq] = false;
                        SetLead(arena, freq, false, ad);
                    }

                    if (!ad.HasGamePoint[freq])
                    {
                        ad.HasGamePoint[freq] = true;
                        SetGamePoint(arena, freq, true, ad);
                        ScheduleGamePointBong(arena, freq);
                    }
                }
                else if (!ad.HasLead[freq])
                {
                    ad.HasLead[freq] = true;
                    SetLead(arena, freq, true, ad);
                }
            }
            else if (scores[freq] == scores[otherFreq])
            {
                // Tie: clear all lead/game-point indicators.
                for (int i = 0; i < MaxFreqs; i++)
                {
                    if (ad.HasGamePoint[i])
                    {
                        SetGamePoint(arena, i, false, ad);
                        ad.HasGamePoint[i] = false;
                    }

                    if (ad.HasLead[i])
                    {
                        SetLead(arena, i, false, ad);
                        ad.HasLead[i] = false;
                    }
                }
            }
            else
            {
                // Scoring freq is losing (e.g. an own goal): the other team may now be on game point.
                if ((scores[otherFreq] - scores[freq]) >= (ad.WinBy - 1) && scores[otherFreq] > (ad.CapturePoints - 2)
                    && !ad.HasGamePoint[otherFreq])
                {
                    ad.HasGamePoint[otherFreq] = true;
                    SetGamePoint(arena, otherFreq, true, ad);
                    ScheduleGamePointBong(arena, otherFreq);
                }
            }

            scores.CopyTo(ad.Scores);
            ad.LastScoringFreq = freq;
        }

        private void SetScoreDigits(Arena arena, int freq, int score, bool isGamePointGoal, ArenaData ad)
        {
            int tensValue = score / 10;
            int onesValue = score % 10;
            EnabledLvz lvz = ad.Lvzs;

            if (freq == 0)
            {
                if (lvz.WbTens != -1)
                    _lvzObjects.Toggle(arena, (short)lvz.WbTens, false);
                if (lvz.WbOnes != -1)
                    _lvzObjects.Toggle(arena, (short)lvz.WbOnes, false);

                if (!isGamePointGoal)
                    PlayDelayedSound(arena, 4000, PbSound.WarbirdScore);

                _lvzObjects.Toggle(arena, PbLvz.WarbirdScoreSparkle, true);

                short tens = (short)(tensValue + PbLvz.WarbirdScoreTens);
                short ones = (short)(onesValue + PbLvz.WarbirdScoreOnes);
                _lvzObjects.Toggle(arena, tens, true);
                _lvzObjects.Toggle(arena, ones, true);
                lvz.WbTens = tens;
                lvz.WbOnes = ones;
            }
            else
            {
                if (lvz.JavTens != -1)
                    _lvzObjects.Toggle(arena, (short)lvz.JavTens, false);
                if (lvz.JavOnes != -1)
                    _lvzObjects.Toggle(arena, (short)lvz.JavOnes, false);

                if (!isGamePointGoal)
                    PlayDelayedSound(arena, 4000, PbSound.JavelinScore);

                _lvzObjects.Toggle(arena, PbLvz.JavelinScoreSparkle, true);

                short tens = (short)(tensValue + PbLvz.JavelinScoreTens);
                short ones = (short)(onesValue + PbLvz.JavelinScoreOnes);
                _lvzObjects.Toggle(arena, tens, true);
                _lvzObjects.Toggle(arena, ones, true);
                lvz.JavTens = tens;
                lvz.JavOnes = ones;
            }
        }

        private void ScheduleGamePointBong(Arena arena, int freq)
        {
            // ANNOUNCE_GAMEPOINT = 700 cs = 7000 ms.
            PlayDelayedSound(arena, 7000, freq == 0 ? PbSound.WarbirdGamePoint : PbSound.JavelinGamePoint);
        }

        #endregion

        #region Game over / new game

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            _chat.SendArenaMessage(arena, PbSound.Lockdown, "");
            _lvzObjects.Toggle(arena, PbLvz.GameOver, true);

            // Timer frame off + clear the clock.
            _lvzObjects.Toggle(arena, PbLvz.Timer, false);
            ad.Lvzs.TimerDisplay = false;
            _mainloopTimer.ClearTimer<Arena>(Timer_GameDisplay, arena);
            ResetGameTimerLvzs(arena, false, ad);
            ad.TimerPaused = false;

            int winner = ad.LastScoringFreq;
            _mainloopTimer.SetTimer(Timer_AnnounceGameOver, 4000, Timeout.Infinite, new GameOverState(arena, winner), arena);

            ad.RoundStart = false;
            ad.LastScoringFreq = -1;
        }

        private bool Timer_AnnounceGameOver(GameOverState state)
        {
            _chat.SendArenaMessage(state.Arena, PbSound.GameOver, "");

            if (state.Winner != -1)
                _mainloopTimer.SetTimer(Timer_AnnounceWinner, 3000, Timeout.Infinite, state, state.Arena);

            return false;
        }

        private bool Timer_AnnounceWinner(GameOverState state)
        {
            _chat.SendArenaMessage(state.Arena, PbSound.Lockdown, "");

            if (state.Winner == 0)
                _lvzObjects.Toggle(state.Arena, PbLvz.WarbirdWin, true);
            else
                _lvzObjects.Toggle(state.Arena, PbLvz.JavelinWin, true);

            // ANNOUNCE_WIN = 50 cs = 500 ms.
            PlayDelayedSound(state.Arena, 500, state.Winner == 0 ? PbSound.WarbirdWin : PbSound.JavelinWin);

            return false;
        }

        private void Callback_BallGameStart(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.IsLeagueGame)
                _lvzObjects.Toggle(arena, PbLvz.NewGame, true);

            if (!ad.RoundStart)
                RoundStart(arena, ad);
        }

        private void RoundStart(Arena arena, ArenaData ad)
        {
            ResetLvzs(arena, ad);
            ad.RoundStart = true;
            ad.CapturePoints = _configManager.GetInt(arena.Cfg!, "Soccer", "CapturePoints", 0) * -1;
            ad.WinBy = _configManager.GetInt(arena.Cfg!, "Soccer", "WinBy", 0);
            Array.Clear(ad.HasGamePoint);
        }

        private void ResetLvzs(Arena arena, ArenaData ad)
        {
            EnabledLvz lvz = ad.Lvzs;

            if (lvz.WbBall) { _lvzObjects.Toggle(arena, PbLvz.WarbirdBall, false); lvz.WbBall = false; }
            if (lvz.JavBall) { _lvzObjects.Toggle(arena, PbLvz.JavelinBall, false); lvz.JavBall = false; }
            if (lvz.WbLeading) { _lvzObjects.Toggle(arena, PbLvz.WarbirdLeading, false); lvz.WbLeading = false; }
            if (lvz.JavLeading) { _lvzObjects.Toggle(arena, PbLvz.JavelinLeading, false); lvz.JavLeading = false; }
            if (lvz.WbGamePoint) { _lvzObjects.Toggle(arena, PbLvz.WarbirdGamePoint, false); lvz.WbGamePoint = false; }
            if (lvz.JavGamePoint) { _lvzObjects.Toggle(arena, PbLvz.JavelinGamePoint, false); lvz.JavGamePoint = false; }

            if (lvz.WbTens != -1) { _lvzObjects.Toggle(arena, (short)lvz.WbTens, false); lvz.WbTens = -1; }
            if (lvz.WbOnes != -1) { _lvzObjects.Toggle(arena, (short)lvz.WbOnes, false); lvz.WbOnes = -1; }
            if (lvz.JavTens != -1) { _lvzObjects.Toggle(arena, (short)lvz.JavTens, false); lvz.JavTens = -1; }
            if (lvz.JavOnes != -1) { _lvzObjects.Toggle(arena, (short)lvz.JavOnes, false); lvz.JavOnes = -1; }

            Array.Clear(ad.Scores);

            if (lvz.GameTimeMinutes10s != -1) { _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes10s + PbLvz.TenMinutesZero), false); lvz.GameTimeMinutes10s = -1; }
            if (lvz.GameTimeMinutes != -1) { _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes + PbLvz.MinutesZero), false); lvz.GameTimeMinutes = -1; }
            if (lvz.GameTimeSeconds10s != -1) { _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), false); lvz.GameTimeSeconds10s = -1; }
        }

        #endregion

        #region Game clock

        private void StartGameTimer(Arena arena, int seconds)
        {
            if (seconds == 0 || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (!ad.RoundStart)
                RoundStart(arena, ad);

            ad.IsLeagueGame = true;
            _lvzObjects.Toggle(arena, PbLvz.Timer, true);
            ad.Lvzs.TimerDisplay = true;

            int startDelayMs = CalculateLvzTimerValues(arena, ad, seconds);
            // Clear any existing clock timer first so a restart (e.g. golden-goal extra time) doesn't stack a
            // second Timer_GameDisplay and run the clock at double speed.
            _mainloopTimer.ClearTimer<Arena>(Timer_GameDisplay, arena);
            _mainloopTimer.SetTimer(Timer_GameDisplay, startDelayMs, 10000, arena, arena);
        }

        private int CalculateLvzTimerValues(Arena arena, ArenaData ad, int seconds)
        {
            ad.TimerSeconds = seconds;
            int minutes = seconds / 60;
            int secs = seconds % 60;
            ad.Lvzs.GameTimeMinutes10s = minutes / 10;
            ad.Lvzs.GameTimeMinutes = minutes % 10;
            ad.Lvzs.GameTimeSeconds10s = secs / 10;
            ad.TimerSeconds -= secs % 10; // round down to a multiple of 10
            ResetGameTimerLvzs(arena, true, ad);
            return (secs % 10) * 1000; // align first tick to the 10s boundary
        }

        private void ResetGameTimerLvzs(Arena arena, bool activate, ArenaData ad)
        {
            EnabledLvz lvz = ad.Lvzs;

            if (!activate)
                _lvzObjects.Toggle(arena, PbLvz.SecondsCountdown, false);

            if (lvz.GameTimeMinutes10s != -1)
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes10s + PbLvz.TenMinutesZero), activate);
            if (lvz.GameTimeMinutes != -1)
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes + PbLvz.MinutesZero), activate);
            if (lvz.GameTimeSeconds10s != -1)
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), activate);
        }

        private bool Timer_GameDisplay(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.TimerPaused)
                return true;

            AdvanceClock(arena, ad);

            if (ad.TimerSeconds >= 0)
            {
                ad.TimerSeconds -= 10;
                return true;
            }

            return false;
        }

        private void AdvanceClock(Arena arena, ArenaData ad)
        {
            EnabledLvz lvz = ad.Lvzs;

            _lvzObjects.Toggle(arena, PbLvz.SecondsCountdown, false);
            _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), false);
            lvz.GameTimeSeconds10s--;

            if (lvz.GameTimeSeconds10s >= 0)
            {
                _lvzObjects.Toggle(arena, PbLvz.SecondsCountdown, true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), true);
                return;
            }

            // Borrow a minute.
            _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes + PbLvz.MinutesZero), false);
            lvz.GameTimeMinutes--;

            if (lvz.GameTimeMinutes >= 0)
            {
                lvz.GameTimeSeconds10s = 5;
                _lvzObjects.Toggle(arena, PbLvz.SecondsCountdown, true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes + PbLvz.MinutesZero), true);
                return;
            }

            // Borrow a ten-minute.
            _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes10s + PbLvz.TenMinutesZero), false);
            lvz.GameTimeMinutes10s--;

            if (lvz.GameTimeMinutes10s >= 0)
            {
                lvz.GameTimeSeconds10s = 5;
                lvz.GameTimeMinutes = 9;
                _lvzObjects.Toggle(arena, PbLvz.SecondsCountdown, true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeSeconds10s + PbLvz.TenSecondsZero), true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes + PbLvz.MinutesZero), true);
                _lvzObjects.Toggle(arena, (short)(lvz.GameTimeMinutes10s + PbLvz.TenMinutesZero), true);
                return;
            }

            // Fully expired; leave all digits off.
            lvz.GameTimeSeconds10s = 0;
            lvz.GameTimeMinutes = 0;
            lvz.GameTimeMinutes10s = 0;
        }

        #endregion

        #region Delayed sound

        private void PlayDelayedSound(Arena arena, int delayMs, ChatSound sound)
        {
            _mainloopTimer.SetTimer(Timer_PlayDelayed, delayMs, Timeout.Infinite, new DelayedSoundState(arena, sound), arena);
        }

        private bool Timer_PlayDelayed(DelayedSoundState state)
        {
            _chat.SendArenaMessage(state.Arena, state.Sound, "");
            return false;
        }

        #endregion

        #region Indicator helpers

        private void SetBall(Arena arena, int freq, bool on, ArenaData ad)
        {
            if (freq == 0)
            {
                _lvzObjects.Toggle(arena, PbLvz.WarbirdBall, on);
                ad.Lvzs.WbBall = on;
            }
            else
            {
                _lvzObjects.Toggle(arena, PbLvz.JavelinBall, on);
                ad.Lvzs.JavBall = on;
            }
        }

        private void SetLead(Arena arena, int freq, bool on, ArenaData ad)
        {
            if (freq == 0)
            {
                _lvzObjects.Toggle(arena, PbLvz.WarbirdLeading, on);
                ad.Lvzs.WbLeading = on;
            }
            else
            {
                _lvzObjects.Toggle(arena, PbLvz.JavelinLeading, on);
                ad.Lvzs.JavLeading = on;
            }
        }

        private void SetGamePoint(Arena arena, int freq, bool on, ArenaData ad)
        {
            if (freq == 0)
            {
                _lvzObjects.Toggle(arena, PbLvz.WarbirdGamePoint, on);
                ad.Lvzs.WbGamePoint = on;
            }
            else
            {
                _lvzObjects.Toggle(arena, PbLvz.JavelinGamePoint, on);
                ad.Lvzs.JavGamePoint = on;
            }
        }

        private void ReadScores(Arena arena, Span<int> scores)
        {
            IBallGamePoints? pg = arena.GetInterface<IBallGamePoints>();
            if (pg is null)
            {
                scores.Clear();
                return;
            }

            try
            {
                ReadOnlySpan<int> teamScores = pg.GetScores(arena);
                for (int i = 0; i < scores.Length; i++)
                    scores[i] = i < teamScores.Length ? teamScores[i] : 0;
            }
            finally
            {
                arena.ReleaseInterface(ref pg);
            }
        }

        #endregion

        #region Help / version

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the PowerBall LVZ module version information.")]
        private void Command_pblvzsversion(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "PowerBall LVZ module (C# port of ASSS PB LVZs Module v1.0 by POiD)");
        }

        private void PrintHelp(Player player)
        {
            _chat.SendMessage(player, "----------------------------------------------------");
            _chat.SendMessage(player, "The following PB LVZs Module commands are available:");
            _chat.SendMessage(player, "----------------------------------------------------");
            _chat.SendMessage(player, $"{"?pblvzsversion",-35} - Display the PB LVZs Module version information");
        }

        #endregion

        #region Helper types

        private sealed class EnabledLvz
        {
            public bool WbBall, WbLeading, WbGamePoint;
            public bool JavBall, JavLeading, JavGamePoint;

            // Currently-lit score digit object ids, or -1 when none.
            public int WbTens = -1, WbOnes = -1, JavTens = -1, JavOnes = -1;

            public bool TimerDisplay;

            // Current clock digit values (0-9 / 0-5), or -1 when the clock is off.
            public int GameTimeMinutes10s = -1, GameTimeMinutes = -1, GameTimeSeconds10s = -1;

            public void Reset()
            {
                WbBall = WbLeading = WbGamePoint = false;
                JavBall = JavLeading = JavGamePoint = false;
                WbTens = WbOnes = JavTens = JavOnes = -1;
                TimerDisplay = false;
                GameTimeMinutes10s = GameTimeMinutes = GameTimeSeconds10s = -1;
            }
        }

        private readonly record struct GameOverState(Arena Arena, int Winner);

        private readonly record struct DelayedSoundState(Arena Arena, ChatSound Sound);

        private sealed class ArenaData : IResettable
        {
            public InterfaceRegistrationToken<IPowerBallLvz>? InterfaceToken;

            public bool RoundStart;
            public int LastScoringFreq;
            public readonly bool[] HasLead = new bool[MaxFreqs];
            public readonly bool[] HasGamePoint = new bool[MaxFreqs];
            public readonly bool[] HasBall = new bool[MaxFreqs];
            public readonly int[] Scores = new int[MaxFreqs];
            public readonly EnabledLvz Lvzs = new();
            public bool IsLeagueGame;
            public bool TimerPaused;
            public int TimerSeconds;
            public int CapturePoints;
            public int WinBy;

            public bool TryReset()
            {
                InterfaceToken = null;
                RoundStart = false;
                LastScoringFreq = -1;
                Array.Clear(HasLead);
                Array.Clear(HasGamePoint);
                Array.Clear(HasBall);
                Array.Clear(Scores);
                Lvzs.Reset();
                IsLeagueGame = false;
                TimerPaused = false;
                TimerSeconds = 0;
                CapturePoints = 0;
                WinBy = 0;
                return true;
            }
        }

        #endregion
    }
}
