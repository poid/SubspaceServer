using SS.Core;
using SS.Core.ComponentInterfaces;
using QS.Physics.Legacy;

namespace SS.Bots
{
    /// <summary>
    /// Default <see cref="IPhysicsWorldProvider"/>. NOT YET IMPLEMENTED — it returns
    /// <see langword="null"/> so the module loads cleanly and the rest of the pipeline
    /// (fake players, position feeding, tick loop, combat-event handling) is wired and ready,
    /// but no live simulation runs until the three bridges described below are built.
    /// </summary>
    internal sealed class PhysicsWorldProvider : IPhysicsWorldProvider
    {
        private readonly ILogManager _logManager;

        public PhysicsWorldProvider(ILogManager logManager)
        {
            _logManager = logManager;
        }

        public ReplayController? CreateWorld(Arena arena, uint currentTick)
        {
            // ---------------------------------------------------------------------------------
            // REMAINING WORK — build the three inputs ReplayController.Configure requires. All
            // three exist server-side already; the task is translating them into QS types.
            //
            //   1. LevelArray level
            //        The arena map's tile data. The server loads it (see IMapData); map its
            //        tiles into QS.Physics.Legacy.LevelArray.
            //
            //   2. CollisionArenaSettings collisionSettings
            //        The collision-relevant subset of the arena's settings.
            //
            //   3. GameSettings gameSettings            (QS.Networking.Protocols.Game.Settings)
            //        Ship/arena/prize settings parsed from the arena's Continuum client settings.
            //        Without this, ApplyShipAddition gives ships default (zero) thrust/recharge,
            //        so they will not move or fight correctly. The server holds the raw client
            //        settings; the QS "Game Common Protocol Library" has the types to parse into.
            //
            // Once the three are available:
            //
            //     WorldStateConfig config = WorldStateConfig.Default;   // or sized from arena settings
            //     ReplayController controller = new ReplayController(currentTick, new uint[] { 0 }, in config);
            //     controller.Configure(level, collisionSettings, gameSettings);
            //     return controller;
            //
            // A single canonical lane (delays = { 0 }) is correct for a live authoritative
            // server; the multi-lane rollback support exists for client-side prediction.
            // ---------------------------------------------------------------------------------

            _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena,
                "No physics world created: the map/collision/settings bridges in PhysicsWorldProvider are not implemented yet. " +
                "Bots can be created but will not simulate until IPhysicsWorldProvider is completed.");

            return null;
        }
    }
}
