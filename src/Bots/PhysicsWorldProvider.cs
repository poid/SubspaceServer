using System;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using QS.Networking.Protocols.Game;        // GameSettings
using QS.Networking.Protocols.Game.Enums;  // TileTypes
using QS.Physics.Legacy;                    // LevelArray, CollisionArenaSettings, ReplayController, WorldStateConfig

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

        public ReplayController? CreateWorld(Arena arena, uint currentTick)
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

            // 3. Collision settings — both scalars are already parsed into the arena settings.
            CollisionArenaSettings collision = new()
            {
                BounceFactor = gameSettings.arenas.BounceFactor,
                WormholeSwitchTime = (int)gameSettings.arenas.WormholeSwitchTime,
                GameSettings = gameSettings,
            };

            // 4. Build and configure the controller. A single canonical lane (delays = {0}) is correct for a
            //    live authoritative server; the multi-lane rollback support is for client-side prediction.
            WorldStateConfig config = WorldStateConfig.Default;
            ReplayController controller = new(currentTick, [0u], in config);
            controller.Configure(level, collision, gameSettings);

            _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena, "Physics world configured for bots.");
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
