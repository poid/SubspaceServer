using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.PowerBall.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// PowerBall per-game statistics engine and end-of-game chart — a port of the ASSS <c>pbstats</c> module.
    /// </summary>
    /// <remarks>
    /// Tracks detailed per-player stats for the current game (goals, assists, kills/ball-kills/far-kills/team-kills,
    /// deaths/ball-deaths, steals, turnovers, saves, spawns, carries + carry time, near-ball time), computes a rating and
    /// rating-per-minute, detects steals/saves/chokes/assists using the goal map regions and ball distance, and prints a
    /// two-column WARBIRDS/JAVS chart at <c>?chart</c> and on game over.
    /// <para>
    /// Deviations from the C original (idiomatic): the ball-carrier-killed case (ASSS <c>CB_BALLKILL</c>, which the C#
    /// server does not provide) is merged into the kill handler and detected from the tracked carrier; all carry-time and
    /// choke bookkeeping is done in the possession-lost handler (<see cref="BallShootCallback"/>), which covers shots,
    /// kills, and leaves uniformly.
    /// </para>
    /// </remarks>
    [ModuleInfo("PowerBall statistics engine and MVP chart (ASSS pbstats port).")]
    public sealed class PowerBallStats : IModule, IArenaAttachableModule, IPowerBallStats
    {
        private const int MaxTeams = 2;
        private const int NearBallSqrDistance = 250000; // 500 pixels, squared
        private const int StealTimeout = 400;           // centiseconds
        private const int AssistTimeout = 1000;         // centiseconds

        private readonly IArenaManager _arenaManager;
        private readonly IBalls _balls;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapData _mapData;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        public PowerBallStats(
            IArenaManager arenaManager,
            IBalls balls,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapData mapData,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
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

            InitializeArena(arena, ad);

            ShipFreqChangeCallback.Register(arena, Callback_ShipFreqChange);
            BallPickupCallback.Register(arena, Callback_BallPickup);
            BallShootCallback.Register(arena, Callback_BallShoot);
            BallGameGoalCallback.Register(arena, Callback_BallGameGoal);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);
            BallGameStartCallback.Register(arena, Callback_BallGameStart);
            PlayerActionCallback.Register(arena, Callback_PlayerAction);
            KillCallback.Register(arena, Callback_Kill);

            _commandManager.AddCommand("chart", Command_chart, arena);
            _commandManager.AddCommand("charthelp", Command_charthelp, arena);
            _commandManager.AddCommand("resetstats", Command_resetstats, arena);
            _commandManager.AddCommand("pbstatshelp", Command_pbstatshelp, arena);
            _commandManager.AddCommand("pbstatsversion", Command_pbstatsversion, arena);

            _mainloopTimer.SetTimer(Timer_Base, 1000, 1000, arena, arena);

            ad.InterfaceToken = arena.RegisterInterface<IPowerBallStats>(this);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.InterfaceToken is not null)
                arena.UnregisterInterface(ref ad.InterfaceToken);

            _mainloopTimer.ClearTimer<Arena>(Timer_Base, arena);

            _commandManager.RemoveCommand("chart", Command_chart, arena);
            _commandManager.RemoveCommand("charthelp", Command_charthelp, arena);
            _commandManager.RemoveCommand("resetstats", Command_resetstats, arena);
            _commandManager.RemoveCommand("pbstatshelp", Command_pbstatshelp, arena);
            _commandManager.RemoveCommand("pbstatsversion", Command_pbstatsversion, arena);

            ShipFreqChangeCallback.Unregister(arena, Callback_ShipFreqChange);
            BallPickupCallback.Unregister(arena, Callback_BallPickup);
            BallShootCallback.Unregister(arena, Callback_BallShoot);
            BallGameGoalCallback.Unregister(arena, Callback_BallGameGoal);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);
            BallGameStartCallback.Unregister(arena, Callback_BallGameStart);
            PlayerActionCallback.Unregister(arena, Callback_PlayerAction);
            KillCallback.Unregister(arena, Callback_Kill);

            return true;
        }

        #endregion

        #region IPowerBallStats

        void IPowerBallStats.ResetStats(Arena arena)
        {
            if (arena is not null && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                ClearGameStats(ad);
        }

        void IPowerBallStats.PrintHelp(Player player) => PrintHelp(player);

        #endregion

        #region Setup

        [ConfigHelp<int>("MultiPub", "MaxPubFreq", ConfigScope.Arena, Default = 1, Description = "Freqs above this are side games and are ignored by the stats engine.")]
        private void InitializeArena(Arena arena, ArenaData ad)
        {
            ad.MaxPubFreq = _configManager.GetInt(arena.Cfg!, "MultiPub", "MaxPubFreq", 1);
            ad.Goal0 = _mapData.FindRegionByName(arena, "Goal0");
            ad.Goal1 = _mapData.FindRegionByName(arena, "Goal1");
            ad.GoalScored = true;
            ad.ActiveBallId = -1;
            ad.AssistFrequency = -1;
        }

        private int GetCustomGameMask(Arena arena) => _configManager.GetInt(arena.Cfg!, "Soccer", "CustomGame", 0);

        #endregion

        #region Record lookup / reset

        private static PlayerStats? GetRecord(ArenaData ad, ReadOnlySpan<char> name, int freq, bool initialize)
        {
            int bucket = ((freq % MaxTeams) + MaxTeams) % MaxTeams;
            List<PlayerStats> list = ad.GameStats[bucket];

            foreach (PlayerStats stats in list)
            {
                if (name.Equals(stats.Name, StringComparison.OrdinalIgnoreCase))
                    return stats;
            }

            if (!initialize)
                return null;

            PlayerStats created = new()
            {
                Name = name.ToString(),
                NearBall = false,
                InShip = true,
                EnteredTick = ServerTick.Now,
                HasBall = false,
            };
            list.Add(created);
            return created;
        }

        private static void ClearGameStats(ArenaData ad)
        {
            ad.GameStats[0].Clear();
            ad.GameStats[1].Clear();
        }

        private static void ClearAssists(ArenaData ad)
        {
            ad.AssistPlayers.Clear();
        }

        private void RoundStart(Arena arena, ArenaData ad)
        {
            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena == arena && player.Ship != ShipType.Spec && player.Freq <= ad.MaxPubFreq)
                        GetRecord(ad, player.Name, player.Freq, initialize: true);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        #endregion

        #region Rating

        private static void CalculateRating(PlayerStats d)
        {
            double rating =
                d.Goals * 7 + d.Assists * 5 + d.Kills * 2 + d.BallKills * 3 + d.FarKills * 1
                + d.TeamKills * -1 + d.Deaths * -1 + d.BallDeaths * -2
                + d.Steals * 4 + d.Turnover * -3 + d.Saves * 5 + d.Spawns * 2
                + (d.CarriesTime / 100) * 0.1
                + d.NearTime * 0.1;

            int r = (int)rating;

            int bc = (int)(d.Carries * 0.5f);
            int carTime = (int)(d.CarriesTime) / 100 / 5;
            if (bc > carTime)
                bc = carTime;
            r += bc;
            d.Rating = r;

            int playingTime = (int)d.PlayingTime;
            if (d.InShip)
                playingTime += ServerTick.Now - d.EnteredTick;

            float playing = playingTime / 100.0f / 60.0f;
            if (playing > 0)
                d.Rpm = r / playing;
        }

        #endregion

        #region Ball pickup / possession-lost

        private void Callback_BallPickup(Arena arena, Player player, byte ballId)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            if (!ad.RoundStart)
            {
                ad.ActiveBallId = ballId;
                RoundStart(arena, ad);
            }

            PlayerStats? playerData = GetRecord(ad, player.Name, player.Freq, initialize: true);
            if (playerData is null)
                return;

            playerData.Carries++;
            ad.RoundStart = true;

            if (ad.GoalScored)
            {
                ad.GoalScored = false;
                playerData.Spawns++;
            }

            // Stray carry / double-hold (shouldn't normally happen). Preserved verbatim from ASSS.
            if (ad.PlayerWithBall is not null && ad.PlayerWithBall != player)
            {
                PlayerStats? last = GetRecord(ad, ad.PlayerWithBall.Name, ad.PlayerWithBall.Freq, initialize: false);
                if (last is not null && last.HasBall)
                {
                    playerData.CarriesTime += ServerTick.Now - playerData.PickupTick;
                    if (ad.LastWithBall != ad.PlayerWithBall)
                        last.Turnover++;
                    last.HasBall = false;
                }
            }

            // Assist chain + steal/save/turnover.
            if (ad.LastWithBall is not null && ad.LastWithBall != player)
            {
                AddAssistPlayer(ad, ad.LastWithBall.Name, player.Freq);

                if (ad.LastWithBall.Freq != player.Freq
                    && (ServerTick.Now - ad.LastReleasedTick) < StealTimeout)
                {
                    bool isSave = false;

                    if (ad.Goal0 is not null && ad.Goal1 is not null)
                    {
                        MapRegion ownGoal = (player.Freq % 2 == 0) ? ad.Goal0 : ad.Goal1;
                        if (ownGoal.ContainsCoordinate((short)(player.Position.X / 16), (short)(player.Position.Y / 16)))
                        {
                            isSave = true;
                            playerData.Saves++;
                            BroadcastPub(arena, ad, $"Save by {player.Name}!");
                            if (ad.PossibleChoke)
                                BroadcastPub(arena, ad, $"Choke by {ad.LastWithBall.Name}.");
                        }
                    }

                    if (!isSave)
                    {
                        playerData.Steals++;
                        PlayerStats? last = GetRecord(ad, ad.LastWithBall.Name, ad.LastWithBall.Freq, initialize: false);
                        if (last is not null)
                        {
                            last.Turnover++;
                            BroadcastPub(arena, ad, $"{(ad.PossibleChoke ? "Choke" : "Turnover")} by {last.Name}.");
                        }
                        BroadcastPub(arena, ad, $"Steal by {player.Name}!");
                    }
                }
            }

            ad.PlayerWithBall = player;
            ad.LastWithBall = player;
            playerData.HasBall = true;
            playerData.PickupTick = ServerTick.Now;
        }

        private void Callback_BallShoot(Arena arena, Player player, byte ballId)
        {
            // Possession lost (shot, killed while carrying, or left). This is the single place carry-time and choke are
            // accounted, covering the ASSS CB_BALLFIRE and the carry-time part of CB_BALLKILL.
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            ad.LastReleasedTick = ServerTick.Now;
            ad.LastWithBall = player;

            if (ad.PlayerWithBall != player)
            {
                ad.PlayerWithBall = null;
                return;
            }

            ad.PlayerWithBall = null;

            PlayerStats? playerData = GetRecord(ad, player.Name, player.Freq, initialize: false);
            if (playerData is null)
                return;

            if (playerData.HasBall)
            {
                playerData.CarriesTime += ServerTick.Now - playerData.PickupTick;
                playerData.HasBall = false;

                ad.PossibleChoke = IsInAttackGoal(ad, player);
            }
        }

        #endregion

        #region Goal / assists

        private void AddAssistPlayer(ArenaData ad, ReadOnlySpan<char> name, int frequency)
        {
            if (frequency != ad.AssistFrequency)
            {
                ClearAssists(ad);
                ad.AssistFrequency = frequency;
            }

            foreach (AssistPlayer ap in ad.AssistPlayers)
            {
                if (name.Equals(ap.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (frequency == ad.AssistFrequency)
                        ap.PassedTick = ServerTick.Now;
                    else
                        ad.AssistPlayers.Remove(ap);
                    return;
                }
            }

            ad.AssistPlayers.Add(new AssistPlayer { Name = name.ToString(), Frequency = frequency, PassedTick = ServerTick.Now });
        }

        private string? ProcessAssists(ArenaData ad, Player goalScorer)
        {
            if (ad.AssistPlayers.Count < 1)
            {
                ad.AssistFrequency = -1;
                return null;
            }

            if (ad.AssistPlayers.Count > 1)
                ad.AssistPlayers.Sort(static (a, b) => (b.PassedTick - a.PassedTick));

            ServerTick currentTick = ad.LastReleasedTick;
            ad.NumAssists = 0;
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (AssistPlayer ap in ad.AssistPlayers)
                {
                    if (string.Equals(ap.Name, goalScorer.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ap.Frequency != goalScorer.Freq)
                        continue;

                    PlayerStats? playerData = GetRecord(ad, ap.Name, ap.Frequency, initialize: false);
                    if (playerData is null)
                        continue;

                    if ((currentTick - ap.PassedTick) <= AssistTimeout)
                    {
                        playerData.Assists++;
                        if (sb.Length > 0)
                            sb.Append(", ");
                        sb.Append(ap.Name);
                        ad.NumAssists++;
                    }
                }

                ClearAssists(ad);
                ad.AssistFrequency = -1;
                return sb.Length > 0 ? sb.ToString() : null;
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private void Callback_BallGameGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if ((GetCustomGameMask(arena) & (1 << ballId)) != 0)
                return;

            ad.GoalScored = true;

            string? assists = ProcessAssists(ad, player);
            if (assists is not null)
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                try
                {
                    _playerData.Lock();
                    try
                    {
                        foreach (Player p in _playerData.Players)
                        {
                            if (p.Arena == arena && p.Status == PlayerState.Playing && p.Freq < ad.MaxPubFreq)
                                set.Add(p);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    _chat.SendSetMessage(set, $"Assist{(ad.NumAssists > 1 ? "s" : "")} By: {assists}");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }

            ad.PlayerWithBall = null;
            ad.LastWithBall = null;

            PlayerStats? scorerData = GetRecord(ad, player.Name, player.Freq, initialize: true);
            if (scorerData is null)
                return;

            scorerData.Goals++;
        }

        #endregion

        #region Kills

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (killed.Freq > ad.MaxPubFreq)
                return;

            if (!ad.RoundStart)
                return;

            PlayerStats? killerData = GetRecord(ad, killer.Name, killer.Freq, initialize: true);
            PlayerStats? killedData = GetRecord(ad, killed.Name, killed.Freq, initialize: true);
            if (killerData is null || killedData is null)
                return;

            // Was the killed player the ball carrier? (Merged ASSS CB_BALLKILL handling.) Robust to the ordering of this
            // handler versus the possession-lost handler: check the tracked carrier, or a just-released last carrier.
            bool wasBallKill = killed == ad.PlayerWithBall
                || (killed == ad.LastWithBall && (ServerTick.Now - ad.LastReleasedTick) <= 5);

            if (wasBallKill)
            {
                if (killer.Freq != killed.Freq)
                {
                    killerData.BallKills++;
                    killedData.BallDeaths++;
                }
                else
                {
                    killerData.TeamKills++;
                    killedData.Deaths++;
                }
                return;
            }

            if (killer.Freq == killed.Freq)
            {
                killerData.TeamKills++;
            }
            else if (ad.ActiveBallId >= 0 && _balls.TryGetBallData(arena, ad.ActiveBallId, out BallData ball))
            {
                if (GetSqrDistance(killed, ball) > NearBallSqrDistance)
                    killerData.FarKills++;
                else
                    killerData.Kills++;
            }
            else
            {
                killerData.Kills++;
            }

            killedData.Deaths++;
        }

        private static long GetSqrDistance(Player player, in BallData ball)
        {
            long dx = player.Position.X - ball.X;
            long dy = player.Position.Y - ball.Y;
            return dx * dx + dy * dy;
        }

        #endregion

        #region Ship/freq change, game start/over, player leave, base timer

        private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (oldFreq > ad.MaxPubFreq && newFreq > ad.MaxPubFreq)
                return;

            if (!ad.RoundStart)
                return;

            if (newShip == ShipType.Spec || newFreq > ad.MaxPubFreq)
            {
                // Leaving play.
                if (oldShip != ShipType.Spec)
                {
                    PlayerStats? playerData = GetRecord(ad, player.Name, oldFreq, initialize: false);
                    if (playerData is not null && playerData.InShip)
                    {
                        playerData.PlayingTime += ServerTick.Now - playerData.EnteredTick;
                        playerData.InShip = false;
                    }
                }
                return;
            }

            if (oldFreq != newFreq)
            {
                PlayerStats? oldData = GetRecord(ad, player.Name, oldFreq, initialize: false);
                if (oldData is not null && oldData.InShip)
                {
                    oldData.PlayingTime += ServerTick.Now - oldData.EnteredTick;
                    oldData.InShip = false;
                }

                PlayerStats? newData = GetRecord(ad, player.Name, newFreq, initialize: true);
                if (newData is not null)
                {
                    newData.EnteredTick = ServerTick.Now;
                    newData.InShip = true;
                }
            }
            else
            {
                PlayerStats? playerData = GetRecord(ad, player.Name, newFreq, initialize: true);
                if (playerData is not null && !playerData.InShip)
                {
                    playerData.EnteredTick = ServerTick.Now;
                    playerData.InShip = true;
                }
            }
        }

        private void Callback_BallGameStart(Arena arena)
        {
            if (arena.TryGetExtraData(_adKey, out ArenaData? ad) && !ad.RoundStart)
                RoundStart(arena, ad);
        }

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            DisplayChart(arena, ad, null);
            ClearGameStats(ad);

            ad.PlayerWithBall = null;
            ad.LastWithBall = null;
            ad.RoundStart = false;
            ad.GoalScored = true;
            ad.ActiveBallId = -1;
            ad.LastReleasedTick = new ServerTick(0);
            ad.GameTime = 0;
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if ((action != PlayerAction.LeaveArena && action != PlayerAction.Disconnect) || arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.PlayerWithBall == player)
            {
                ad.PlayerWithBall = null;
                ad.LastReleasedTick = ServerTick.Now;
            }

            if (ad.LastWithBall == player)
                ad.LastWithBall = null;
        }

        private bool Timer_Base(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (!ad.RoundStart || ad.ActiveBallId < 0)
                return true;

            if (!_balls.TryGetBallData(arena, ad.ActiveBallId, out BallData ball))
                return true;

            bool countedGameTime = false;

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.Arena != arena || player.Ship == ShipType.Spec)
                        continue;

                    if (!countedGameTime)
                    {
                        countedGameTime = true;
                        ad.GameTime++;
                    }

                    PlayerStats? playerData = GetRecord(ad, player.Name, player.Freq, initialize: true);
                    if (playerData is null)
                        continue;

                    if (GetSqrDistance(player, ball) > NearBallSqrDistance)
                    {
                        playerData.NearBall = false;
                    }
                    else if (playerData.NearBall)
                    {
                        playerData.NearTime++;
                    }
                    else
                    {
                        playerData.NearBall = true;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return true;
        }

        #endregion

        #region Helpers

        private bool IsInAttackGoal(ArenaData ad, Player player)
        {
            if (ad.Goal0 is null || ad.Goal1 is null)
                return false;

            MapRegion attackGoal = (player.Freq % 2 == 0) ? ad.Goal1 : ad.Goal0;
            return attackGoal.ContainsCoordinate((short)(player.Position.X / 16), (short)(player.Position.Y / 16));
        }

        private void BroadcastPub(Arena arena, ArenaData ad, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Arena == arena && p.Status == PlayerState.Playing && p.Freq <= ad.MaxPubFreq)
                            set.Add(p);
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                _chat.SendSetMessage(set, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        #endregion

        #region Chart

        private const string ChartHeader =
            "WARBIRDS                  RA  RPM  G  A   K  BK  TK   D  BD   S   T   V  W  BC  JAVS                      RA  RPM  G  A   K  BK  TK   D  BD   S   T   V  W  BC";
        private const string ChartSeparator =
            "------------------------ --- ---- -- -- --- --- --- --- --- --- --- --- -- ---  ------------------------ --- ---- -- -- --- --- --- --- --- --- --- --- -- ---";

        private void DisplayChart(Arena arena, ArenaData ad, Player? toPlayer)
        {
            List<PlayerStats> warbirds = ad.GameStats[0];
            List<PlayerStats> javs = ad.GameStats[1];

            // Pass 1: ratings, freq totals, MVPs.
            FreqTotals freqTotal0 = new();
            FreqTotals freqTotal1 = new();
            GameMvps mvp = new();

            foreach (PlayerStats d in warbirds)
                Accumulate(d, ref freqTotal0, mvp, ad);
            foreach (PlayerStats d in javs)
                Accumulate(d, ref freqTotal1, mvp, ad);

            warbirds.Sort(static (a, b) => b.Rating.CompareTo(a.Rating));
            javs.Sort(static (a, b) => b.Rating.CompareTo(a.Rating));

            HashSet<Player>? set = null;
            if (toPlayer is null)
            {
                set = _objectPoolManager.PlayerSetPool.Get();
                _playerData.Lock();
                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Arena == arena && p.Status == PlayerState.Playing
                            && (p.Ship == ShipType.Spec || p.Freq <= ad.MaxPubFreq))
                        {
                            set.Add(p);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            void Send(string line)
            {
                if (toPlayer is not null)
                    _chat.SendMessage(toPlayer, line);
                else
                    _chat.SendSetMessage(set!, line);
            }

            try
            {
                Send(ChartHeader);
                Send(ChartSeparator);

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    int rows = Math.Max(warbirds.Count, javs.Count);
                    for (int i = 0; i < rows; i++)
                    {
                        sb.Clear();
                        if (i < warbirds.Count)
                            AppendPlayerBlock(sb, warbirds[i]);
                        else
                            sb.Append(' ', 78);

                        if (i < javs.Count)
                        {
                            sb.Append("  ");
                            AppendPlayerBlock(sb, javs[i]);
                        }

                        Send(sb.ToString());
                    }

                    Send(ChartSeparator);

                    sb.Clear();
                    AppendTotalBlock(sb, freqTotal0);
                    sb.Append("  ");
                    AppendTotalBlock(sb, freqTotal1);
                    Send(sb.ToString());

                    Send(ChartSeparator);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }

                // MVP block.
                SendMvp(Send, "Most Goals:          ", mvp.Goals, mvp.GoalsNames, always: true, minutes: false);
                if (mvp.Assists > 0) SendMvp(Send, "Most Assists:        ", mvp.Assists, mvp.AssistsNames, always: true, minutes: false);
                if (mvp.Kills > 0) SendMvp(Send, "Most Kills:          ", mvp.Kills, mvp.KillsNames, always: true, minutes: false);
                if (mvp.Deaths > 0) SendMvp(Send, "Most Deaths:         ", mvp.Deaths, mvp.DeathsNames, always: true, minutes: false);
                if (mvp.Steals > 0) SendMvp(Send, "Most Steals:         ", mvp.Steals, mvp.StealsNames, always: true, minutes: false);
                SendMvp(Send, "Most Spawns:         ", mvp.Spawns, mvp.SpawnsNames, always: true, minutes: false);
                SendMvp(Send, "Most Ball Carries:   ", mvp.Carries, mvp.CarriesNames, always: true, minutes: false);

                int ballSeconds = (int)(mvp.Time / 100);
                Send($"Most Ball Time:    {ballSeconds / 60,2}:{ballSeconds % 60:D2}   {mvp.TimeNames}");

                if (mvp.Near > 0 && ad.GameTime > 0)
                {
                    double pct = (mvp.Near / (double)ad.GameTime) * 100.0;
                    Send($"Most Near Ball:     {pct,4:F1}%  {mvp.NearNames}");
                }

                Send($"Highest Rating:      {mvp.Rating,3}   {mvp.RatingNames}");
            }
            finally
            {
                if (set is not null)
                    _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private static void SendMvp(Action<string> send, string label, int value, string names, bool always, bool minutes)
        {
            send($"{label}{value,3}   {names}");
        }

        private static void AppendPlayerBlock(StringBuilder sb, PlayerStats d)
        {
            int totalKills = d.Kills + d.FarKills + d.BallKills;
            int totalDeaths = d.Deaths + d.BallDeaths;
            sb.Append($"{d.Name,-24} {d.Rating,3} {d.Rpm,4:F1} {d.Goals,2} {d.Assists,2} {totalKills,3} {d.BallKills,3} {d.TeamKills,3} {totalDeaths,3} {d.BallDeaths,3} {d.Steals,3} {d.Turnover,3} {d.Saves,3} {d.Spawns,2} {d.Carries,3}");
        }

        private static void AppendTotalBlock(StringBuilder sb, in FreqTotals t)
        {
            sb.Append($"{"TOTAL",-24} {t.Rating,3} {"",4} {t.Goals,2} {t.Assists,2} {t.Kills,3} {t.BallKills,3} {t.TeamKills,3} {t.Deaths,3} {t.BallDeaths,3} {t.Steals,3} {t.Turnover,3} {t.Saves,3} {t.Spawns,2} {t.Carries,3}");
        }

        private static void Accumulate(PlayerStats d, ref FreqTotals total, GameMvps mvp, ArenaData ad)
        {
            CalculateRating(d);

            int kills = d.Kills + d.FarKills + d.BallKills;
            int deaths = d.Deaths + d.BallDeaths;

            total.Rating += d.Rating;
            total.Goals += d.Goals;
            total.Assists += d.Assists;
            total.Kills += kills;
            total.BallKills += d.BallKills;
            total.TeamKills += d.TeamKills;
            total.Deaths += deaths;
            total.BallDeaths += d.BallDeaths;
            total.Steals += d.Steals;
            total.Turnover += d.Turnover;
            total.Saves += d.Saves;
            total.Spawns += d.Spawns;
            total.Carries += d.Carries;

            mvp.Add(ref mvp.Goals, ref mvp.GoalsNames, d.Goals, d.Name);
            mvp.Add(ref mvp.Assists, ref mvp.AssistsNames, d.Assists, d.Name);
            mvp.Add(ref mvp.Kills, ref mvp.KillsNames, kills, d.Name);
            mvp.Add(ref mvp.Deaths, ref mvp.DeathsNames, deaths, d.Name);
            mvp.Add(ref mvp.Steals, ref mvp.StealsNames, d.Steals, d.Name);
            mvp.Add(ref mvp.Spawns, ref mvp.SpawnsNames, d.Spawns, d.Name);
            mvp.Add(ref mvp.Carries, ref mvp.CarriesNames, d.Carries, d.Name);
            mvp.Add(ref mvp.Time, ref mvp.TimeNames, (int)d.CarriesTime, d.Name);
            mvp.Add(ref mvp.Near, ref mvp.NearNames, (int)d.NearTime, d.Name);
            mvp.Add(ref mvp.Rating, ref mvp.RatingNames, d.Rating, d.Name);
        }

        #endregion

        #region Commands / help

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the current stats chart.")]
        private void Command_chart(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is { } arena && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                DisplayChart(arena, ad, player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays details of what the chart columns mean.")]
        private void Command_charthelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "STAT   RATING POINTS   DESCRIPTION");
            _chat.SendMessage(player, "----   -------------   -----------");
            _chat.SendMessage(player, "  RA                   Current rating");
            _chat.SendMessage(player, " RPM                   Rating per minute of play");
            _chat.SendMessage(player, "   G         7         Goals scored");
            _chat.SendMessage(player, "   A         5         Assists       [last passer before a Goal is scored]");
            _chat.SendMessage(player, "   K         2         Kills");
            _chat.SendMessage(player, "  BK         3         Ball Kills    [Killing enemy player with the ball]");
            _chat.SendMessage(player, "             1         Far Kills     [Killing enemy player far from the ball]");
            _chat.SendMessage(player, "  TK        -1         Team Kills    [Killing members of your own team]");
            _chat.SendMessage(player, "   D        -1         Deaths");
            _chat.SendMessage(player, "   B        -2         Ball Deaths   [Deaths while carrying the ball]");
            _chat.SendMessage(player, "   S         4         Steals        [Stealing the ball from opposition]");
            _chat.SendMessage(player, "   T        -3         Turn Overs    [Losing the ball to the opposition]");
            _chat.SendMessage(player, "   V         5         Saves         [Catching ball from enemy in goal]");
            _chat.SendMessage(player, "   W         2         Spawns.       [Gathering ball from center after a goal]");
            _chat.SendMessage(player, "  BC         0.1       Ball Carries  [Times with the ball, Capped at Ball Time / 5]");
            _chat.SendMessage(player, "  NT         0.1       Near Time     [Time near the ball]");
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Resets the stats recording for this game. (staff)")]
        private void Command_resetstats(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is { } arena && arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                ClearGameStats(ad);
                _chat.SendArenaMessage(arena, (ChatSound)1, $"Game Stats reset by {player.Name}");
            }
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the list of PowerBall stats commands.")]
        private void Command_pbstatshelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            PrintHelp(player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the PowerBall stats module version information.")]
        private void Command_pbstatsversion(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "PowerBall Stats module (C# port of ASSS PB Stats Module v1.0 by POiD)");
        }

        private void PrintHelp(Player player)
        {
            _chat.SendMessage(player, "-----------------------------------------------------");
            _chat.SendMessage(player, "The following PB Stats Module commands are available:");
            _chat.SendMessage(player, "-----------------------------------------------------");
            _chat.SendMessage(player, $"{"?chart",-35} - Display the current Stats Chart");
            _chat.SendMessage(player, $"{"?charthelp",-35} - Display details of what the Chart columns mean");

            if (_capabilityManager.HasCapability(player, "cmd_resetstats"))
            {
                _chat.SendMessage(player, "-=-=-= Moderator Commands =-=-=-");
                _chat.SendMessage(player, $"{"?resetstats",-35} - Resets the stats recording for this game.");
            }
        }

        #endregion

        #region Helper types

        private sealed class PlayerStats
        {
            public string Name = "";
            public int Rating;
            public float Rpm;
            public int Spawns;
            public int Kills;
            public int BallKills;
            public int TeamKills;
            public int FarKills;
            public int Deaths;
            public int Goals;
            public int Assists;
            public int Steals;
            public int Turnover;
            public int Saves;
            public int BallDeaths;
            public int Carries;
            public bool HasBall;
            public long CarriesTime;  // centiseconds
            public long PlayingTime;  // centiseconds
            public long NearTime;     // seconds
            public bool NearBall;
            public bool InShip;
            public ServerTick EnteredTick;
            public ServerTick PickupTick;
        }

        private sealed class AssistPlayer
        {
            public string Name = "";
            public int Frequency;
            public ServerTick PassedTick;
        }

        private struct FreqTotals
        {
            public int Rating, Goals, Assists, Kills, BallKills, TeamKills, Deaths, BallDeaths, Steals, Turnover, Saves, Spawns, Carries;
        }

        private sealed class GameMvps
        {
            public int Goals; public string GoalsNames = "";
            public int Assists; public string AssistsNames = "";
            public int Kills; public string KillsNames = "";
            public int Deaths; public string DeathsNames = "";
            public int Steals; public string StealsNames = "";
            public int Spawns; public string SpawnsNames = "";
            public int Carries; public string CarriesNames = "";
            public int Time; public string TimeNames = "";
            public int Near; public string NearNames = "";
            public int Rating; public string RatingNames = "";

            public void Add(ref int value, ref string names, int statValue, string name)
            {
                if (statValue > value)
                {
                    value = statValue;
                    names = name;
                }
                else if (statValue == value && statValue != 0)
                {
                    names = names.Length == 0 ? name : $"{names}, {name}";
                }
            }
        }

        private sealed class ArenaData : IResettable
        {
            public InterfaceRegistrationToken<IPowerBallStats>? InterfaceToken;

            public readonly List<PlayerStats>[] GameStats = [new List<PlayerStats>(), new List<PlayerStats>()];
            public Player? PlayerWithBall;
            public Player? LastWithBall;
            public bool PossibleChoke;

            public int AssistFrequency = -1;
            public int NumAssists;
            public readonly List<AssistPlayer> AssistPlayers = [];

            public bool RoundStart;
            public bool GoalScored = true;
            public int ActiveBallId = -1;
            public long GameTime;
            public int MaxPubFreq = 1;
            public ServerTick LastReleasedTick;

            public MapRegion? Goal0;
            public MapRegion? Goal1;

            public bool TryReset()
            {
                InterfaceToken = null;
                GameStats[0].Clear();
                GameStats[1].Clear();
                PlayerWithBall = null;
                LastWithBall = null;
                PossibleChoke = false;
                AssistFrequency = -1;
                NumAssists = 0;
                AssistPlayers.Clear();
                RoundStart = false;
                GoalScored = true;
                ActiveBallId = -1;
                GameTime = 0;
                MaxPubFreq = 1;
                LastReleasedTick = new ServerTick(0);
                Goal0 = null;
                Goal1 = null;
                return true;
            }
        }

        #endregion
    }
}
