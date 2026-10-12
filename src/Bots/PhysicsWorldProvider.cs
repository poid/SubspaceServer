using System;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using QS.Networking.Protocols.Game;        // GameSettings
using QS.Networking.Protocols.Game.Enums;  // TileTypes
using QS.Physics.Legacy;                    // LevelArray, ReplayController, WorldStateConfig

namespace SS.Bots
{
    /// <summary>
    /// Builds a fully-configured physics world for an arena from the server's own state.
    /// </summary>
    /// <remarks>
    /// The three inputs <see cref="ReplayController.Configure"/> needs all come straight from the server:
    /// <list type="number">
    ///   <item><b>GameSettings</b> — parsed from the arena's raw Continuum client-settings buffer
    ///     (<see cref="IClientSettings.GetClientSettingsData"/>); the QS <see cref="GameSettings"/> type
    ///     reads that exact 1428-byte format, so no field-by-field mapping is needed.</item>
    ///   <item><b>LevelArray</b> — the map tiles from <see cref="IMapData"/> (tile numbers are shared with
    ///     <see cref="TileTypes"/>).</item>
    ///   <item><b>CollisionArenaSettings</b> — a bounce factor + wormhole switch time, both already parsed
    ///     into <c>GameSettings.arenas</c>.</item>
    /// </list>
    /// </remarks>
    internal sealed class PhysicsWorldProvider : IPhysicsWorldProvider
    {
        private readonly IMapData _mapData;
        private readonly IClientSettings _clientSettings;
        private readonly ILogManager _logManager;

        private const int MapTiles = 1024;

        public PhysicsWorldProvider(IMapData mapData, IClientSettings clientSettings, ILogManager logManager)
        {
            _mapData = mapData;
            _clientSettings = clientSettings;
            _logManager = logManager;
        }

        public ReplayController? CreateWorld(Arena arena, uint currentTick, uint[] rewindLanes, int maxCommandsPerTick)
        {
            // 1. Raw client settings -> QS GameSettings (the Continuum 1428-byte settings format).
            int required = S2C_ClientSettings.Length;
            Span<byte> settingsBytes = stackalloc byte[required];
            int written = _clientSettings.GetClientSettingsData(arena, settingsBytes);
            if (written < required)
            {
                _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena,
                    $"No physics world: client settings unavailable (got {written} of {required} bytes).");
                return null;
            }

            GameSettings gameSettings;
            try
            {
                gameSettings = new GameSettings(settingsBytes);
            }
            catch (ArgumentException ex)
            {
                // Thrown if the server's S2C_ClientSettings layout ever diverges from the 1428-byte format
                // the physics engine expects. Fail loudly rather than simulating with garbage settings.
                _logManager.LogA(LogLevel.Error, nameof(BotsModule), arena,
                    $"No physics world: could not parse client settings ({ex.Message}).");
                return null;
            }

            // 2. Map tiles -> LevelArray. LevelArray defaults every tile to Empty, so only non-empty tiles
            //    are written. One-time per arena attach; ~1M reads but only a few thousand writes.
            LevelArray level = BuildLevel(arena);

            // 3. Build and configure the controller. Snapshot lanes (delays, in 10ms physics ticks) give the
            //    rollback horizon for LATE real-player packets: the deepest lane sits a little past
            //    Lag:PingToSpec so a packet sent just before a high-ping player is specced still lands in a
            //    lane; the near lanes keep the common small-jitter rebuild cheap. Lanes are configurable via
            //    Bots:RewindLanes (the module parses + passes them here). Bot-vs-bot never goes late (both
            //    server-side); the sim-pump + packet-tick stamping (see BotsModule) is what exercises them.
            //    The collision scalars (bounce factor, wormhole switch time) are read from gameSettings.arenas
            //    inside Configure now, so no separate settings object is passed.
            //
            //    The pending-command buffer has to be sized for the rollback, not left at the library default
            //    (256). Every command is queued on every lane that has not reached its tick yet, so the DEEPEST
            //    lane holds each command for the whole horizon — plus the tick it was issued ahead by. At a
            //    70-tick horizon the default is full with 2-3 steering bots, and a full buffer refuses
            //    everything: bot steering and real-player packets alike. So: commands per tick x the ticks the
            //    deepest lane keeps them, with a quarter spare for bursts.
            uint horizon = 0;
            foreach (uint lane in rewindLanes)
                horizon = Math.Max(horizon, lane);

            WorldStateConfig defaults = WorldStateConfig.Default;
            long wanted = (long)maxCommandsPerTick * (horizon + 2) * 5 / 4;
            int pendingCommands = (int)Math.Clamp(wanted, defaults.MaxPendingCommands, 1 << 20);

            WorldStateConfig config = new(
                defaults.MaxShips, defaults.MaxWeapons, defaults.MaxBalls, defaults.MaxBricks, defaults.MaxFlags,
                defaults.MaxPrizes, pendingCommands, defaults.MaxEventsPerTick);
            ReplayController controller = new(currentTick, rewindLanes, in config);
            controller.Configure(level, gameSettings);

            _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                $"Physics world configured for bots (command buffer: {pendingCommands} per lane, for {maxCommandsPerTick} commands/tick over a {horizon}-tick horizon).");
            return controller;
        }

        private LevelArray BuildLevel(Arena arena)
        {
            LevelArray level = new();
            for (int y = 0; y < MapTiles; y++)
            {
                for (int x = 0; x < MapTiles; x++)
                {
                    byte tile = _mapData.GetTile(arena, new TileCoordinates((short)x, (short)y));
                    if (tile != 0) // 0 == MapTile.None / Empty; LevelArray is already all-Empty
                        level.SetTile(x, y, (TileTypes)tile);
                }
            }
            return level;
        }
    }
}
