using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;
using System.Collections.Generic;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// The "Proball" side-game — a port of the ASSS <c>proball</c> module.
    /// </summary>
    /// <remarks>
    /// A two-team capture-points soccer game played on a dedicated freq band (<c>MultiPub:ProballFreq</c> and +1) inside
    /// the main pub arena, in the lower-right region of the map. It keeps its own score for its own ball id and ends when
    /// a team reaches the capture-point target with the required win-by margin. The proball ball must be listed in the
    /// <c>Soccer:CustomGame</c> bitmask so that <see cref="PowerBallGamePoints"/> lets its goals through without scoring them.
    /// </remarks>
    [ModuleInfo("Proball side-game (ASSS proball port): 2-team capture-points soccer in the lower-right region.")]
    public sealed class ProBallGame : IModule, IArenaAttachableModule
    {
        // Region (in tiles) that counts a spectator as a proball participant.
        private const int RegionMinX = 400;
        private const int RegionMaxX = 620;
        private const int RegionMinY = 776;
        private const int RegionMaxY = 1006;

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private ArenaDataKey<ArenaData> _adKey;

        public ProBallGame(
            IArenaManager arenaManager,
            IChat chat,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
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

            BallGoalCallback.Register(arena, Callback_BallGoal);
            ArenaActionCallback.Register(arena, Callback_ArenaAction);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            // Note: the ASSS original mistakenly re-registered CB_ARENAACTION here instead of unregistering it. Fixed.
            BallGoalCallback.Unregister(arena, Callback_BallGoal);
            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);

            return true;
        }

        #endregion

        #region Config

        [ConfigHelp<int>("MultiPub", "ProballBall", ConfigScope.Arena, Default = 0, Description = "The ball id used by the proball side-game.")]
        [ConfigHelp<int>("MultiPub", "ProballCapturePoints", ConfigScope.Arena, Default = -8, Description = "Absolute capture points; a team wins at -ProballCapturePoints goals.")]
        [ConfigHelp<int>("MultiPub", "ProballWinBy", ConfigScope.Arena, Default = 2, Description = "A team must beat the other by this many goals to win.")]
        [ConfigHelp<int>("MultiPub", "ProballFreq", ConfigScope.Arena, Default = 30, Description = "Base freq of the proball side-game (uses this and +1).")]
        private void LoadConfig(Arena arena, ArenaData ad)
        {
            ConfigHandle ch = arena.Cfg!;
            ad.BallId = _configManager.GetInt(ch, "MultiPub", "ProballBall", 0);
            ad.CapturePoints = _configManager.GetInt(ch, "MultiPub", "ProballCapturePoints", -8);
            ad.WinBy = _configManager.GetInt(ch, "MultiPub", "ProballWinBy", 2);
            ad.StartFreq = _configManager.GetInt(ch, "MultiPub", "ProballFreq", 30);
        }

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create)
            {
                LoadConfig(arena, ad);
                ad.Scores[0] = ad.Scores[1] = 0;
            }
            else if (action == ArenaAction.ConfChanged)
            {
                LoadConfig(arena, ad);
            }
        }

        #endregion

        private void Callback_BallGoal(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ballId != ad.BallId)
                return;

            // The scoring team is decided by which side the goal is on (not the scorer's freq).
            int freq = goalCoordinates.X < 512 ? 1 : 0;
            ad.Scores[freq]++;

            HashSet<Player> teamSet = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Player> enemySet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player other in _playerData.Players)
                    {
                        if (other.Arena != arena || !IsProballPlayerOrSpec(other, ad))
                            continue;

                        if (other.Freq == player.Freq)
                            teamSet.Add(other);
                        else
                            enemySet.Add(other);
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                _chat.SendSetMessage(teamSet, ChatSound.Goal, $"Team Goal! by {player.Name}");
                _chat.SendSetMessage(enemySet, ChatSound.Goal, $"Enemy Goal! by {player.Name}");
                _chat.SendSetMessage(teamSet, $"SCORE: Warbirds:{ad.Scores[0]} Javelins:{ad.Scores[1]}");
                _chat.SendSetMessage(enemySet, $"SCORE: Warbirds:{ad.Scores[0]} Javelins:{ad.Scores[1]}");

                int otherFreq = 1 - freq;
                if (ad.Scores[freq] >= -ad.CapturePoints && ad.Scores[otherFreq] + ad.WinBy <= ad.Scores[freq])
                {
                    _chat.SendSetMessage(teamSet, ChatSound.Ding, "Soccer game over.");
                    _chat.SendSetMessage(enemySet, ChatSound.Ding, "Soccer game over.");
                    ad.Scores[0] = ad.Scores[1] = 0;
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(teamSet);
                _objectPoolManager.PlayerSetPool.Return(enemySet);
            }
        }

        private static bool IsProballPlayerOrSpec(Player player, ArenaData ad)
        {
            if (player.Freq == ad.StartFreq || player.Freq == ad.StartFreq + 1)
                return true;

            // Spectators sitting inside the proball region (rectangle in tiles, position is in pixels = tile*16).
            return player.Position.X >= RegionMinX * 16 && player.Position.X <= RegionMaxX * 16
                && player.Position.Y >= RegionMinY * 16 && player.Position.Y <= RegionMaxY * 16;
        }

        #region Helper types

        private sealed class ArenaData : IResettable
        {
            public int BallId;
            public int CapturePoints;
            public int WinBy;
            public int StartFreq;
            public readonly int[] Scores = new int[2];

            public bool TryReset()
            {
                BallId = 0;
                CapturePoints = 0;
                WinBy = 0;
                StartFreq = 0;
                Scores[0] = Scores[1] = 0;
                return true;
            }
        }

        #endregion
    }
}
