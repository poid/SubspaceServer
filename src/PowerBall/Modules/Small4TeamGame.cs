using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// The "Small 4-Team" side-game scoreboard — a port of the ASSS <c>small4tmpb</c> module.
    /// </summary>
    /// <remarks>
    /// Four teams each start with 1 point and steal each other's; the first to 4 wins. This module only maintains the
    /// 4-team LVZ scoreboard: each team's score is a digit object at <c>Small4TeamScore0 + score + 5*teamIndex</c>. The
    /// actual scoring is done by <see cref="PowerBallGamePoints"/> (read via <see cref="IBallGamePoints.GetScores"/>).
    /// <para>
    /// Note: the ASSS original reads scores for freqs 0..3. Whether the small-4-team players sit on freqs 0..3 or on the
    /// <c>MultiPub:Small4TmFreq</c> band is a zone-config concern; this port preserves the freq 0..3 read and should be
    /// verified against the target zone's setup.
    /// </para>
    /// </remarks>
    [ModuleInfo("Small 4-Team side-game scoreboard (ASSS small4tmpb port): renders the 4-team LVZ score digits.")]
    public sealed class Small4TeamGame : IModule, IArenaAttachableModule
    {
        private const int TeamCount = 4;

        private readonly IArenaManager _arenaManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly ILvzObjects _lvzObjects;

        private ArenaDataKey<ArenaData> _adKey;

        public Small4TeamGame(
            IArenaManager arenaManager,
            IChat chat,
            ICommandManager commandManager,
            ILvzObjects lvzObjects)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _lvzObjects = lvzObjects ?? throw new ArgumentNullException(nameof(lvzObjects));
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
            if (!arena.TryGetExtraData(_adKey, out _))
                return false;

            ArenaActionCallback.Register(arena, Callback_ArenaAction);
            BallGameGoalCallback.Register(arena, Callback_BallGameGoal);
            BallGameOverCallback.Register(arena, Callback_BallGameOver);

            _commandManager.AddCommand("small4tmrules", Command_small4tmrules, arena);

            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _commandManager.RemoveCommand("small4tmrules", Command_small4tmrules, arena);

            ArenaActionCallback.Unregister(arena, Callback_ArenaAction);
            BallGameGoalCallback.Unregister(arena, Callback_BallGameGoal);
            BallGameOverCallback.Unregister(arena, Callback_BallGameOver);

            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
                MakeLvz(arena, LvzUpdate.Initialize);
        }

        private void Callback_BallGameGoal(Arena arena, Player player, byte ballId, SS.Core.Map.TileCoordinates goalCoordinates)
        {
            MakeLvz(arena, LvzUpdate.Goal);
        }

        private void Callback_BallGameOver(Arena arena, short winnerFreq)
        {
            MakeLvz(arena, LvzUpdate.GameOver);
        }

        #endregion

        private enum LvzUpdate
        {
            /// <summary>Turn on the "1" digit for every team.</summary>
            Initialize,

            /// <summary>Update the digits for teams whose score changed.</summary>
            Goal,

            /// <summary>Reset all digits back to "1" and show the game-over banner.</summary>
            GameOver,
        }

        private void MakeLvz(Arena arena, LvzUpdate update)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            Span<int> current = stackalloc int[TeamCount];
            if (update == LvzUpdate.Goal)
                ReadScores(arena, current);
            else
                current.Fill(1);

            // Up to (2 toggles per team) + 1 game-over object.
            Span<LvzObjectToggle> toggles = stackalloc LvzObjectToggle[TeamCount * 2 + 1];
            int count = 0;

            for (int i = 0; i < TeamCount; i++)
            {
                if (update == LvzUpdate.Initialize)
                {
                    toggles[count++] = new LvzObjectToggle((short)(PbLvz.Small4TeamScore0 + 1 + 5 * i), true);
                }
                else if (ad.Scores[i] != current[i])
                {
                    toggles[count++] = new LvzObjectToggle((short)(PbLvz.Small4TeamScore0 + ad.Scores[i] + 5 * i), false);
                    toggles[count++] = new LvzObjectToggle((short)(PbLvz.Small4TeamScore0 + current[i] + 5 * i), true);
                }
            }

            if (update == LvzUpdate.GameOver)
                toggles[count++] = new LvzObjectToggle(PbLvz.GameOver, true);

            if (count > 0)
                _lvzObjects.Toggle(arena, toggles[..count]);

            for (int i = 0; i < TeamCount; i++)
                ad.Scores[i] = current[i];
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

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Describes the rules for the small-4-team side-game.")]
        private void Command_small4tmrules(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, "Small4Tm Rules:");
            _chat.SendMessage(player, "Each team starts with 1 point. The goal is to steal the other 3 points.");
            _chat.SendMessage(player, "First team to reach 4 points wins.");
            _chat.SendMessage(player, "Keep an eye on the score to avoid scoring goals worth 0 points.");
            _chat.SendMessage(player, "Wall-passing is allowed.");
        }

        #region Helper types

        private sealed class ArenaData : IResettable
        {
            public readonly int[] Scores = new int[TeamCount];

            public bool TryReset()
            {
                Array.Clear(Scores);
                return true;
            }
        }

        #endregion
    }
}
