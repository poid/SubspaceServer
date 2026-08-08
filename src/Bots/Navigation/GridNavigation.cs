using System;
using System.Collections.Generic;
using QS.Networking.Protocols.Game.Enums; // TileTypes
using QS.Physics.Legacy;                   // LevelArray

namespace SS.Bots.Navigation
{
    /// <summary>
    /// Grid-based <see cref="INavigation"/> over a Continuum tile map.
    /// </summary>
    /// <remarks>
    /// <para>Built once per arena from the static <see cref="LevelArray"/>. Walls are classified and
    /// inflated by the ship radius; the walkable tiles are flood-filled into regions separated by
    /// doors, and door tiles become portals between regions. Door open/closed state and bricks are
    /// pushed in afterwards and consulted at query time.</para>
    /// <para>Path queries use grid A* with generation-stamped work arrays (no per-query allocation
    /// or full-grid clears). This is the baseline; the two documented upgrades are Jump Point Search
    /// (replace the neighbour expansion) and D* Lite (replace the from-scratch search with an
    /// incremental repair driven by <see cref="UpdateDoors"/>/<see cref="AddBrick"/> edits). Both fit
    /// behind the existing methods.</para>
    /// </remarks>
    public sealed class GridNavigation : INavigation
    {
        private const int Size = NavGeometry.MapTiles;      // 1024
        private const int N = Size * Size;                  // 1,048,576 tiles

        // ---- Static substrate (immutable after Build) ----
        private readonly bool[] _wall = new bool[N];        // RAW wall/hazard (hard block + region boundary)
        private readonly bool[] _nearWall = new bool[N];    // within a ship-radius of a raw wall — SOFT A* cost only
        private readonly bool[] _door = new bool[N];        // door tile (conditionally passable)
        private readonly byte[] _doorBit = new byte[N];     // which door type (0-7), valid where _door
        private readonly int[] _region = new int[N];        // region id, or -1 if blocked/door
        private int _regionCount;
        private int _portalCount;
        private int _shipRadiusTiles;                        // hull half-width, for hull-aware line clearance

        // ---- Dynamic overlay ----
        private byte _doorOpenMask;                         // bit N set => door type 162+N is open
        private uint _doorLastSwitchTick;
        private uint _doorDelay;
        private readonly Dictionary<int, uint> _bricks = new(); // tile index -> expire tick

        // ---- A* work arrays (generation-stamped to avoid clearing 1M entries per query) ----
        private readonly float[] _g = new float[N];
        private readonly int[] _cameFrom = new int[N];
        private readonly int[] _stamp = new int[N];
        private int _gen;
        private readonly PriorityQueue<int, float> _open = new();

        // Cost of moving onto a closed door, per tick of expected wait. Tunable: too low and bots
        // never detour; too high and they never wait. TODO: tune against typical DoorDelay/detour length.
        private const float WaitCostPerTick = 0.05f;
        private const float CardinalCost = 1.0f;
        private const float DiagonalCost = 1.41421356f;
        // Soft penalty for planning through a tile within a ship-radius of a wall. High enough to prefer
        // open lanes, low enough to still take a tight gap when it's the only route (vs. hard-blocking,
        // which disconnected the map). Tunable.
        private const float NearWallCost = 3.0f;

        public bool IsReady { get; private set; }
        public NavStats Stats => new(_regionCount, _portalCount, CountBlocked());

        private GridNavigation() { }

        /// <summary>Builds the navigation graph from an arena's static tile data.</summary>
        /// <param name="level">The physics world's level array (same geometry the sim uses).</param>
        /// <param name="shipRadiusTiles">How far to inflate walls so paths keep the hull clear.</param>
        public static GridNavigation Build(LevelArray level, int shipRadiusTiles)
        {
            GridNavigation nav = new();
            nav._shipRadiusTiles = Math.Max(0, shipRadiusTiles);
            nav.Classify(level);
            nav.Inflate(nav._shipRadiusTiles);
            nav.BuildRegions();
            nav.IsReady = true;
            return nav;
        }

        private static int Index(int x, int y) => (y << 10) + x;
        private static bool InBounds(int x, int y) => (uint)x < Size && (uint)y < Size;

        // ---- Tile classification ----

        private static bool IsDoor(TileTypes t) => t >= TileTypes.DoorTileVertical162 && t <= TileTypes.DoorTileHorizontal169;

        private static bool IsPassable(TileTypes t) => t switch
        {
            TileTypes.Empty or TileTypes.FlagTile or TileTypes.SafeTile or TileTypes.GoalTile => true,
            // Fly-over / fly-under / invisible-after-fly-under (173-191) — ships pass through.
            >= TileTypes.FlyOverTile1 and <= TileTypes.InvisibleAfterFlyUnder => true,
            // Weapon sink (ship passes), team brick / brick-blocker / animated green (ship passes).
            TileTypes.WeaponSink or TileTypes.AnimatedTeamBrick or TileTypes.BrickBlocker or TileTypes.AnimatedGreen => true,
            _ => false, // everything else (walls, invisible solids, asteroids, wormhole, warp sinks, enemy brick) blocks or is hazardous
        };

        private void Classify(LevelArray level)
        {
            bool hasData = level.HasTileData;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = Index(x, y);
                    TileTypes t = hasData ? level.GetTile(x, y) : TileTypes.Empty;
                    if (IsDoor(t))
                    {
                        _door[i] = true;
                        _doorBit[i] = (byte)((int)t - (int)TileTypes.DoorTileVertical162);
                    }
                    else if (!IsPassable(t))
                    {
                        _wall[i] = true;
                    }
                }
            }
        }

        // Configuration-space margin: mark every tile within Chebyshev radius of a raw wall in a SEPARATE
        // _nearWall layer used only as an A* preference cost — NOT as a hard block. Hard-blocking this
        // margin (the old behaviour) closes 1-2 tile gaps that ships actually fly through, which split
        // pbsmall's open playfield into 4 disconnected nav regions and stranded ball carriers with NoPath.
        // Raw walls alone drive hard-blocking and region connectivity; the margin just makes the planner
        // prefer routes with hull clearance while still allowing a tight gap when it's the only way through.
        private void Inflate(int radius)
        {
            if (radius == 0)
                return;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!_wall[Index(x, y)]) // dilate RAW walls into the soft margin
                        continue;

                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= Size) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx;
                            if ((uint)nx < Size)
                                _nearWall[Index(nx, ny)] = true;
                        }
                    }
                }
            }
        }

        // Flood-fill walkable (non-wall, non-door) tiles into regions. Doors are boundaries, so each
        // region is an area you can traverse without passing a door; doors then link the regions they
        // border (the portal graph). This is what makes door-gated connectivity a cheap region-level
        // question instead of a full-grid search.
        private void BuildRegions()
        {
            Array.Fill(_region, -1);
            Queue<int> queue = new();
            int region = 0;

            for (int start = 0; start < N; start++)
            {
                if (_wall[start] || _door[start] || _region[start] != -1)
                    continue;

                _region[start] = region;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int x = i & (Size - 1);
                    int y = i >> 10;
                    Span<(int nx, int ny)> neighbours =
                    [
                        (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1),
                    ];
                    foreach ((int nx, int ny) in neighbours)
                    {
                        if (!InBounds(nx, ny)) continue;
                        int ni = Index(nx, ny);
                        if (_wall[ni] || _door[ni] || _region[ni] != -1) continue;
                        _region[ni] = region;
                        queue.Enqueue(ni);
                    }
                }
                region++;
            }
            _regionCount = region;

            // Count portals: a door tile that borders at least two distinct regions links them. We
            // keep the count for diagnostics; AreConnected walks the region graph on demand via doors.
            // TODO: precompute an explicit region-adjacency list keyed by door type so AreConnected is
            // O(regions) instead of re-scanning door tiles, and so wait-cost routing can prefer the
            // portal that opens soonest.
            HashSet<long> seenPairs = new();
            for (int i = 0; i < N; i++)
            {
                if (!_door[i]) continue;
                int a = NeighbourRegion(i, -1);
                int b = NeighbourRegion(i, a);
                if (a >= 0 && b >= 0 && a != b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (seenPairs.Add(key))
                        _portalCount++;
                }
            }
        }

        private int NeighbourRegion(int doorIndex, int exclude)
        {
            int x = doorIndex & (Size - 1);
            int y = doorIndex >> 10;
            Span<(int nx, int ny)> neighbours = [(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)];
            foreach ((int nx, int ny) in neighbours)
            {
                if (!InBounds(nx, ny)) continue;
                int r = _region[Index(nx, ny)];
                if (r >= 0 && r != exclude)
                    return r;
            }
            return -1;
        }

        // ---- Dynamic overlay ----

        public void UpdateDoors(byte openBitmask, uint lastSwitchTick, uint doorDelay)
        {
            _doorOpenMask = openBitmask;
            _doorLastSwitchTick = lastSwitchTick;
            _doorDelay = doorDelay;
        }

        public void AddBrick(TileCoord from, TileCoord to, uint expireTick)
        {
            foreach (int i in RasterizeLine(from.X, from.Y, to.X, to.Y))
                _bricks[i] = expireTick;
        }

        public void PruneExpiredBricks(uint currentTick)
        {
            if (_bricks.Count == 0)
                return;

            // Small collections in practice; a scan is fine.
            List<int>? expired = null;
            foreach ((int i, uint expire) in _bricks)
            {
                if (currentTick >= expire)
                    (expired ??= new()).Add(i);
            }
            if (expired is not null)
                foreach (int i in expired)
                    _bricks.Remove(i);
        }

        private bool DoorOpen(int doorBit) => (_doorOpenMask & (1 << doorBit)) != 0;

        // Expected ticks until a closed door of this type next cycles open. Best-effort: doors cycle
        // every _doorDelay ticks from _doorLastSwitchTick. If we don't know the schedule yet, treat
        // it as a full delay's wait. Doors that never cycle would ideally be walls — a refinement.
        private float ClosedDoorWaitCost(uint currentTick)
        {
            if (_doorDelay == 0)
                return WaitCostPerTick * 100f; // unknown schedule: a modest fixed penalty
            uint next = _doorLastSwitchTick + _doorDelay;
            uint wait = next > currentTick ? next - currentTick : _doorDelay;
            return WaitCostPerTick * wait;
        }

        // ---- Blocking predicates ----

        // Movement: walls and bricks block outright; a closed door is passable with a wait cost.
        private bool BlocksMovement(int i, uint now) => _wall[i] || IsBrick(i, now);

        // Line of fire: walls, bricks, AND closed doors block (weapons don't pass a closed door).
        private bool BlocksSight(int i, uint now) => _wall[i] || IsBrick(i, now) || (_door[i] && !DoorOpen(_doorBit[i]));

        private bool IsBrick(int i, uint now) => _bricks.TryGetValue(i, out uint expire) && now < expire;

        // ---- Region queries ----

        public int GetRegion(TileCoord tile)
        {
            if (!InBounds(tile.X, tile.Y)) return -1;
            return _region[Index(tile.X, tile.Y)];
        }

        public bool AreConnected(TileCoord a, TileCoord b, uint currentTick)
        {
            int ra = GetRegion(a);
            int rb = GetRegion(b);
            if (ra < 0 || rb < 0) return false;
            if (ra == rb) return true;

            // BFS the region graph, crossing a door only if it is currently open.
            HashSet<int> visited = new() { ra };
            Queue<int> queue = new();
            queue.Enqueue(ra);
            while (queue.Count > 0)
            {
                int r = queue.Dequeue();
                if (r == rb) return true;
                foreach (int nr in OpenNeighbourRegions(r, currentTick))
                {
                    if (visited.Add(nr))
                        queue.Enqueue(nr);
                }
            }
            return false;

            // Local scan of door tiles bordering region r whose door is open. O(doors); the TODO in
            // BuildRegions is to precompute this adjacency so connectivity doesn't rescan door tiles.
            IEnumerable<int> OpenNeighbourRegions(int r, uint now)
            {
                for (int i = 0; i < N; i++)
                {
                    if (!_door[i] || !DoorOpen(_doorBit[i])) continue;
                    int x = i & (Size - 1);
                    int y = i >> 10;
                    bool borders = false;
                    int other = -1;
                    Span<(int nx, int ny)> ns = [(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)];
                    foreach ((int nx, int ny) in ns)
                    {
                        if (!InBounds(nx, ny)) continue;
                        int reg = _region[Index(nx, ny)];
                        if (reg == r) borders = true;
                        else if (reg >= 0) other = reg;
                    }
                    if (borders && other >= 0)
                        yield return other;
                }
            }
        }

        // ---- Line of sight (supercover Bresenham) ----

        public bool HasLineOfSight(TileCoord from, TileCoord to, uint currentTick)
        {
            int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                if (!InBounds(x0, y0) || BlocksSight(Index(x0, y0), currentTick))
                    return false;
                if (x0 == x1 && y0 == y1)
                    return true;
                int e2 = err << 1;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        // Hull-aware clearance for a SHIP with width: the centre ray plus two rays offset perpendicular by the
        // ship radius. A straight segment that only grazes a wall corner is clear for a point ray but a snag
        // for the hull, so the offset ray catches it. This is what keeps path smoothing from collapsing a
        // corner-routing path into a straight line the ship's body can't fly (the "stuck against a diagonal
        // wall" bug). Falls back to the thin ray when the radius is 0.
        public bool HasClearLineForShip(TileCoord from, TileCoord to, uint currentTick)
        {
            int r = _shipRadiusTiles;
            if (r <= 0)
                return HasLineOfSight(from, to, currentTick);

            int dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (len < 0.5)
                return InBounds(from.X, from.Y) && !BlocksSight(Index(from.X, from.Y), currentTick);

            int ox = (int)Math.Round(-dy / len * r); // perpendicular offset, ship-radius long
            int oy = (int)Math.Round(dx / len * r);
            for (int side = -1; side <= 1; side++)
            {
                int fx = from.X + ox * side, fy = from.Y + oy * side;
                int tx = to.X + ox * side, ty = to.Y + oy * side;
                if (!InBounds(fx, fy) || !InBounds(tx, ty))
                    return false; // the hull edge would leave the map / clip the border
                if (!HasLineOfSight(new TileCoord((short)fx, (short)fy), new TileCoord((short)tx, (short)ty), currentTick))
                    return false;
            }
            return true;
        }

        // ---- Path query (grid A*) ----

        public bool TryFindPath(TileCoord start, TileCoord goal, uint currentTick, List<TileCoord> pathOut)
        {
            pathOut.Clear();
            if (!InBounds(start.X, start.Y) || !InBounds(goal.X, goal.Y))
                return false;

            int startIdx = Index(start.X, start.Y);
            int goalIdx = Index(goal.X, goal.Y);

            // A ship can physically sit in the inflated margin right next to a wall — a tile the nav marks
            // "blocked". Snap a blocked start/goal to the nearest walkable tile so we can still path OUT of
            // the margin (or up to a target sitting in one) instead of bailing to a straight line into the wall.
            if (BlocksMovement(startIdx, currentTick))
            {
                startIdx = NearestWalkable(start.X, start.Y, currentTick);
                if (startIdx < 0) return false;
            }
            if (BlocksMovement(goalIdx, currentTick))
            {
                goalIdx = NearestWalkable(goal.X, goal.Y, currentTick);
                if (goalIdx < 0) return false;
            }

            _gen++;
            _open.Clear();
            _g[startIdx] = 0f;
            _stamp[startIdx] = _gen;
            _cameFrom[startIdx] = -1;
            _open.Enqueue(startIdx, Heuristic(startIdx, goalIdx));

            while (_open.TryDequeue(out int current, out _))
            {
                if (current == goalIdx)
                {
                    Reconstruct(current, currentTick, pathOut);
                    return true;
                }

                int cx = current & (Size - 1);
                int cy = current >> 10;
                float baseG = _g[current];

                for (int dir = 0; dir < 8; dir++)
                {
                    int nx = cx + DX[dir];
                    int ny = cy + DY[dir];
                    if (!InBounds(nx, ny)) continue;
                    int ni = Index(nx, ny);
                    if (BlocksMovement(ni, currentTick)) continue;

                    bool diagonal = DX[dir] != 0 && DY[dir] != 0;
                    if (diagonal)
                    {
                        // No corner cutting: both orthogonal neighbours must be free.
                        if (BlocksMovement(Index(cx + DX[dir], cy), currentTick) ||
                            BlocksMovement(Index(cx, cy + DY[dir]), currentTick))
                            continue;
                    }

                    float step = diagonal ? DiagonalCost : CardinalCost;
                    if (_door[ni] && !DoorOpen(_doorBit[ni]))
                        step += ClosedDoorWaitCost(currentTick); // price the wait rather than forbidding it
                    if (_nearWall[ni])
                        step += NearWallCost; // prefer hull clearance, but don't forbid tight gaps

                    float tentative = baseG + step;
                    if (_stamp[ni] != _gen || tentative < _g[ni])
                    {
                        _g[ni] = tentative;
                        _cameFrom[ni] = current;
                        _stamp[ni] = _gen;
                        _open.Enqueue(ni, tentative + Heuristic(ni, goalIdx));
                    }
                }
            }
            return false;
        }

        private static readonly int[] DX = [1, -1, 0, 0, 1, 1, -1, -1];
        private static readonly int[] DY = [0, 0, 1, -1, 1, -1, 1, -1];

        private static float Heuristic(int a, int b)
        {
            int ax = a & (Size - 1), ay = a >> 10;
            int bx = b & (Size - 1), by = b >> 10;
            int dx = Math.Abs(ax - bx), dy = Math.Abs(ay - by);
            // Octile distance — admissible for 8-connected grids.
            int min = Math.Min(dx, dy), max = Math.Max(dx, dy);
            return DiagonalCost * min + CardinalCost * (max - min);
        }

        // Reconstruct then string-pull: drop intermediate waypoints the ship can fly past in a
        // straight line, so it flies smooth lines instead of a grid staircase.
        private void Reconstruct(int goalIdx, uint currentTick, List<TileCoord> pathOut)
        {
            List<TileCoord> raw = new();
            for (int i = goalIdx; i != -1; i = _cameFrom[i])
                raw.Add(new TileCoord((short)(i & (Size - 1)), (short)(i >> 10)));
            raw.Reverse();

            if (raw.Count <= 2)
            {
                pathOut.AddRange(raw);
                return;
            }

            int anchor = 0;
            pathOut.Add(raw[0]);
            for (int i = 2; i < raw.Count; i++)
            {
                // Hull-aware: only skip raw[i-1] if the ship's whole body can fly anchor->i. Using a thin ray
                // here let the smoothed line graze wall corners the hull then snags on (the stuck-at-centre bug).
                if (!HasClearLineForShip(raw[anchor], raw[i], currentTick))
                {
                    pathOut.Add(raw[i - 1]);
                    anchor = i - 1;
                }
            }
            pathOut.Add(raw[^1]);
        }

        // ---- Firing position ----

        public bool TryFindFiringPosition(TileCoord shooter, TileCoord target, int maxRangeTiles, uint currentTick, out TileCoord firingPosition)
        {
            firingPosition = default;
            if (!InBounds(target.X, target.Y))
                return false;

            // Spiral outward from the target; return the first walkable tile with a clear line of fire
            // that is within weapon range. Cheap and good enough for a scaffold; a smarter version would
            // prefer positions toward the shooter and away from other threats.
            for (int r = 0; r <= maxRangeTiles; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
                        int tx = target.X + dx, ty = target.Y + dy;
                        if (!InBounds(tx, ty)) continue;
                        int i = Index(tx, ty);
                        if (BlocksMovement(i, currentTick)) continue;
                        TileCoord candidate = new((short)tx, (short)ty);
                        if (HasLineOfSight(candidate, target, currentTick))
                        {
                            firingPosition = candidate;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // ---- Helpers ----

        // Nearest non-blocking tile to (x,y), spiralling out to a small radius. -1 if none found nearby.
        private int NearestWalkable(int x, int y, uint now)
        {
            const int MaxR = 6;
            for (int r = 1; r <= MaxR; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int ny = y + dy;
                    if ((uint)ny >= Size) continue;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
                        int nx = x + dx;
                        if ((uint)nx >= Size) continue;
                        int i = Index(nx, ny);
                        if (!BlocksMovement(i, now))
                            return i;
                    }
                }
            }
            return -1;
        }

        private int CountBlocked()
        {
            int n = 0;
            for (int i = 0; i < N; i++)
                if (_wall[i]) n++;
            return n;
        }

        private static IEnumerable<int> RasterizeLine(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                if (InBounds(x0, y0))
                    yield return Index(x0, y0);
                if (x0 == x1 && y0 == y1)
                    yield break;
                int e2 = err << 1;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }
    }
}
