using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.PowerBall.ComponentInterfaces;
using SS.Utilities;
using System;

namespace SS.PowerBall.Modules
{
    /// <summary>
    /// PowerBall staff utility commands and the <c>?pbhelp</c> aggregator — a port of the ASSS <c>pbextras</c> module.
    /// </summary>
    /// <remarks>
    /// Provides staff commands to force-control the ball game and reset scores, and a <c>?pbhelp</c> command that
    /// aggregates the help output of the attached PowerBall modules.
    /// <para>
    /// The ASSS original also had a <c>?authhelp</c> command with text specific to the "Isometry" biller; it is omitted
    /// here as it is zone/biller-specific rather than part of the PowerBall game.
    /// </para>
    /// </remarks>
    [ModuleInfo("PowerBall extras (ASSS pbextras port): staff ball controls, score reset, and the ?pbhelp aggregator.")]
    public sealed class PowerBallExtras : IModule, IArenaAttachableModule
    {
        private const short CenterX = 8200;
        private const short CenterY = 8200;

        private readonly IBalls _balls;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IPlayerData _playerData;
        private readonly IScoreStats _scoreStats;

        public PowerBallExtras(
            IBalls balls,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IPlayerData playerData,
            IScoreStats scoreStats)
        {
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker) => true;

        bool IModule.Unload(IComponentBroker broker) => true;

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            _commandManager.AddCommand("pbhelp", Command_pbhelp, arena);
            _commandManager.AddCommand("centerball", Command_centerball, arena);
            _commandManager.AddCommand("endgame", Command_endgame, arena);
            _commandManager.AddCommand("startgame", Command_startgame, arena);
            _commandManager.AddCommand("stopgame", Command_stopgame, arena);
            _commandManager.AddCommand("scoreresetall", Command_scoreresetall, arena);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            _commandManager.RemoveCommand("pbhelp", Command_pbhelp, arena);
            _commandManager.RemoveCommand("centerball", Command_centerball, arena);
            _commandManager.RemoveCommand("endgame", Command_endgame, arena);
            _commandManager.RemoveCommand("startgame", Command_startgame, arena);
            _commandManager.RemoveCommand("stopgame", Command_stopgame, arena);
            _commandManager.RemoveCommand("scoreresetall", Command_scoreresetall, arena);
            return true;
        }

        #endregion

        #region Commands

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Displays the list of available PowerBall commands.")]
        private void Command_pbhelp(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            _chat.SendMessage(player, "------------------------------------------------------");
            _chat.SendMessage(player, "The following PB Extras Module commands are available:");
            _chat.SendMessage(player, "------------------------------------------------------");

            bool displayedMod = false;
            DisplayModCommand(player, ref displayedMod, "centerball", "?centerball [ballID]", "Center Ball [ballID] (default 0)");
            DisplayModCommand(player, ref displayedMod, "endgame", "?endgame", "End the current ball game");
            DisplayModCommand(player, ref displayedMod, "startgame", "?startgame", "Start a ball game");
            DisplayModCommand(player, ref displayedMod, "stopgame", "?stopgame", "Stop the current ball game and reset scores");
            DisplayModCommand(player, ref displayedMod, "scoreresetall", "?scoreresetall", "Reset the score (reset interval) for all players");

            // Aggregate help from the other attached PowerBall modules.
            IMultiPub? multiPub = arena.GetInterface<IMultiPub>();
            if (multiPub is not null)
            {
                try { multiPub.PrintHelp(player); }
                finally { arena.ReleaseInterface(ref multiPub); }
            }

            IPowerBallStats? stats = arena.GetInterface<IPowerBallStats>();
            if (stats is not null)
            {
                try { stats.PrintHelp(player); }
                finally { arena.ReleaseInterface(ref stats); }
            }
        }

        [CommandHelp(Targets = CommandTarget.None, Args = "[ballID]", Description = "Centers the given ball (default 0) on the map.")]
        private void Command_centerball(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            byte ballId = 0;
            ReadOnlySpan<char> arg = parameters.Trim();
            if (!arg.IsEmpty && byte.TryParse(arg, out byte parsed))
                ballId = parsed;

            if (!_balls.TryGetBallData(arena, ballId, out BallData ballData))
                return;

            ballData.State = BallState.OnMap;
            ballData.X = CenterX;
            ballData.Y = CenterY;
            ballData.XSpeed = 0;
            ballData.YSpeed = 0;
            ballData.Carrier = null;
            ballData.Time = ServerTick.Now;

            _balls.TryPlaceBall(arena, ballId, ref ballData);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Ends the current ball game (and triggers end-of-game handling).")]
        private void Command_endgame(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ResetBallGame(player.Arena, player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Starts a ball game (restores the configured ball count).")]
        private void Command_startgame(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Arena is { } arena)
                _balls.TrySetBallCount(arena, null); // null => revert to the arena's configured ball count
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Stops the current ball game and resets the scores.")]
        private void Command_stopgame(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ResetBallGame(player.Arena, player);
        }

        [CommandHelp(Targets = CommandTarget.None, Args = null, Description = "Resets the score (reset interval) for all players.")]
        private void Command_scoreresetall(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _playerData.Lock();
            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                    _scoreStats.ScoreReset(otherPlayer, PersistInterval.Reset);
            }
            finally
            {
                _playerData.Unlock();
            }

            _scoreStats.SendUpdates(null, null);
        }

        #endregion

        private void ResetBallGame(Arena? arena, Player player)
        {
            if (arena is null)
                return;

            IBallGamePoints? pg = arena.GetInterface<IBallGamePoints>();
            if (pg is null)
            {
                // No PowerBall scorer attached; fall back to just ending the ball game.
                _balls.EndGame(arena);
                return;
            }

            try
            {
                pg.ResetGame(arena, player);
            }
            finally
            {
                arena.ReleaseInterface(ref pg);
            }
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
    }
}
