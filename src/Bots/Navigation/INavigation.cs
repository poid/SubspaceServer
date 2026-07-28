using System.Collections.Generic;

namespace SS.Bots.Navigation
{
    /// <summary>A tile-grid coordinate. Continuum maps are 1024 x 1024 tiles of 16 pixels each.</summary>
    public readonly record struct TileCoord(short X, short Y);

    /// <summary>Diagnostics about a built navigation graph.</summary>
    public readonly record struct NavStats(int RegionCount, int PortalCount, int BlockedTiles);

    /// <summary>
    /// Tile ↔ world (pixel) coordinate helpers. Ship positions are in pixels; the nav graph
    /// works in tiles. 16 pixels per tile (a left shift / right shift of 4).
    /// </summary>
    public static class NavGeometry
    {
        public const int TileSize = 16;
        public const int MapTiles = 1024;

        public static TileCoord ToTile(int worldX, int worldY) => new((short)(worldX >> 4), (short)(worldY >> 4));
        public static int TileCenterWorldX(TileCoord tile) => (tile.X << 4) + 8;
        public static int TileCenterWorldY(TileCoord tile) => (tile.Y << 4) + 8;
    }

    /// <summary>
    /// Per-arena navigation service consumed by every bot brain (all four gameplay modes go
    /// through this one interface — they differ only in which goal they ask for).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service is a two-part model, matching the discussion of the problem:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Static substrate</b> — a walkability grid (walls inflated by ship radius) and a
    ///     region/portal decomposition, built once from the arena's <c>LevelArray</c> because the
    ///     map geometry never changes.</item>
    ///   <item><b>Dynamic overlay</b> — door open/closed state and player-dropped bricks, pushed in
    ///     by <see cref="BotsModule"/> from the physics engine's door schedule and brick events.
    ///     Doors are priced as <i>waitable gates</i> (a closed-but-cycling door is a high-cost
    ///     passable edge, not a wall); bricks are fully-blocking, temporary tiles.</item>
    /// </list>
    /// <para>
    /// Path queries here are the global (Layer 1) planner. The local steering/motion layer that
    /// turns waypoints into thrust and rotation lives in the brains (see <c>Brains/NavSeekBrain</c>).
    /// </para>
    /// <para>
    /// The current <see cref="GridNavigation"/> implementation answers path queries with grid A*;
    /// the interface is deliberately shaped so the internals can be upgraded to Jump Point Search
    /// (faster on these uniform-cost grids) and D* Lite (incremental replanning when doors/bricks
    /// change) without any caller changing.
    /// </para>
    /// </remarks>
    public interface INavigation
    {
        /// <summary>True once the static substrate has been built for the arena.</summary>
        bool IsReady { get; }

        NavStats Stats { get; }

        // ---- Layer 1: path queries ----

        /// <summary>
        /// Finds a (smoothed) waypoint path from <paramref name="start"/> to <paramref name="goal"/>
        /// honouring the current door and brick state.
        /// </summary>
        /// <param name="currentTick">Used to price closed doors (expected wait) and drop expired bricks.</param>
        /// <param name="pathOut">Cleared, then filled with tile waypoints from start to goal on success.</param>
        /// <returns>False if unreachable given the current dynamic state.</returns>
        bool TryFindPath(TileCoord start, TileCoord goal, uint currentTick, List<TileCoord> pathOut);

        // ---- Line of sight / firing positions (combat and passing) ----

        /// <summary>True if nothing (wall, closed door, or brick) blocks the straight line between the tiles.</summary>
        bool HasLineOfSight(TileCoord from, TileCoord to, uint currentTick);

        /// <summary>
        /// Finds the nearest walkable tile to <paramref name="target"/>, within
        /// <paramref name="maxRangeTiles"/>, that has a clear line of fire to it — i.e. a firing
        /// position, which is usually what a combat or passing bot actually wants rather than the
        /// target's own tile.
        /// </summary>
        bool TryFindFiringPosition(TileCoord shooter, TileCoord target, int maxRangeTiles, uint currentTick, out TileCoord firingPosition);

        // ---- Region / connectivity (door-gated maps) ----

        /// <summary>The region id containing <paramref name="tile"/>, or -1 if the tile is blocked.</summary>
        int GetRegion(TileCoord tile);

        /// <summary>
        /// Whether <paramref name="a"/> and <paramref name="b"/> are reachable from one another
        /// <i>right now</i> — i.e. the region graph connects them using only currently-open doors.
        /// </summary>
        bool AreConnected(TileCoord a, TileCoord b, uint currentTick);

        // ---- Dynamic overlay feed (pushed by BotsModule) ----

        /// <summary>
        /// Updates door state from the physics world. The engine tracks which door tile types are
        /// open (<paramref name="openBitmask"/>: bit N = door type 162+N is passable) and when they
        /// last cycled, so a closed door's wait cost can be estimated as
        /// (<paramref name="lastSwitchTick"/> + <paramref name="doorDelay"/> − now).
        /// </summary>
        void UpdateDoors(byte openBitmask, uint lastSwitchTick, uint doorDelay);

        /// <summary>Marks the tiles spanned by a dropped brick as blocked until <paramref name="expireTick"/>.</summary>
        void AddBrick(TileCoord from, TileCoord to, uint expireTick);

        /// <summary>Clears bricks whose expiry has passed.</summary>
        void PruneExpiredBricks(uint currentTick);
    }
}
