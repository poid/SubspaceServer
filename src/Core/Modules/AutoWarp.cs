using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for relocating, 'warping', players that move onto specially designated map regions defined in extended lvl files.
    /// Players can be warped to a new (x,y) coordinate on the map, and even be sent to another arena.
    /// </summary>
    [CoreModuleInfo]
    public sealed class AutoWarp(
        IArenaManager arenaManager,
        IGame game,
        IMainloop mainloop,
        IPrng prng) : IModule
    {
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly IGame _game = game ?? throw new ArgumentNullException(nameof(game));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IPrng _prng = prng ?? throw new ArgumentNullException(nameof(prng));

        /// <summary>
        /// For sending a player to another arena on the mainloop.
        /// </summary>
        /// <remarks>
        /// Assigned in <see cref="IModule.Load"/> rather than initialized with the field, because a field
        /// initializer cannot bind a delegate to an instance method.
        /// </remarks>
        private Action<ArenaWarp>? _mainloopWork_SendToArena;

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            _mainloopWork_SendToArena = MainloopWork_SendToArena;

            MapRegionCallback.Register(broker, Callback_MapRegion);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            MapRegionCallback.Unregister(broker, Callback_MapRegion);
            return true;
        }

        #endregion

        private void Callback_MapRegion(Player player, MapRegion region, short x, short y, bool entering)
        {
            if (player is null
                || region is null
                || !entering
                || region.AutoWarpDestinations.Count <= 0)
            {
                return;
            }

            var destination = region.AutoWarpDestinations.Count == 1
                ? region.AutoWarpDestinations[0]
                : region.AutoWarpDestinations[_prng.Number(0, region.AutoWarpDestinations.Count - 1)];

            if (string.IsNullOrWhiteSpace(destination.ArenaName))
            {
                // Warping within the arena is only a packet to the player, so it can be done here whatever
                // thread that is.
                _game.WarpTo(player, destination.X, destination.Y);
            }
            else
            {
                // Sending a player to another arena is not. It runs the arena state machine, which is
                // zone-wide and belongs to the mainloop, and this callback fires from the player's arena
                // thread when Net:HandlePacketsOnArenaThread is on.
                Action<ArenaWarp>? mainloopWork = _mainloopWork_SendToArena;
                if (mainloopWork is not null)
                    _mainloop.QueueMainWorkItem(mainloopWork, new ArenaWarp(player, destination.ArenaName, destination.X, destination.Y));
            }
        }

        private void MainloopWork_SendToArena(ArenaWarp warp)
        {
            // The player may have left, or already been warped elsewhere, between the region being entered
            // and this running.
            if (warp.Player.Status != PlayerState.Playing)
                return;

            _arenaManager.SendToArena(warp.Player, warp.ArenaName, warp.X, warp.Y);
        }

        #region Helper Types

        /// <summary>
        /// A player waiting to be sent to another arena, carried from the arena thread to the mainloop.
        /// </summary>
        private readonly record struct ArenaWarp(Player Player, string ArenaName, short X, short Y);

        #endregion
    }
}
