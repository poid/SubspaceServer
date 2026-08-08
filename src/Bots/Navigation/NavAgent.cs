using System.Collections.Generic;
using QS.Physics.Legacy; // Point, WorldState

namespace SS.Bots.Navigation
{
    /// <summary>
    /// Per-bot navigation state: a cached A* waypoint path to a goal, followed and replanned as the bot
    /// moves. One instance per bot. This is the local half of the two-layer nav model — it turns the global
    /// planner's (<see cref="INavigation.TryFindPath"/>) waypoints into a heading. Combat/close steering
    /// stays in <see cref="BotSkills"/>; this is for getting somewhere far, around walls.
    /// </summary>
    public sealed class NavAgent
    {
        private const int ReplanIntervalTicks = 100;             // ~1s between replans unless the goal tile moves
        private const long WaypointReached1000 = 1L * 16 * 1000; // within ~1 tile counts as reaching a waypoint
        private const long WaypointReachedSq = WaypointReached1000 * WaypointReached1000;

        private readonly List<TileCoord> _path = new();
        private int _index;
        private uint _lastPlanTick;
        private TileCoord _goalTile;
        private bool _hasGoal;

        /// <summary>How the last <see cref="TryHeadingTo"/> resolved, for diagnostics.</summary>
        public enum NavResult : byte { None, DirectLos, Path, NoPath }
        public NavResult LastResult { get; private set; }
        public int LastPathLen { get; private set; }
        /// <summary>The tile the follower steered toward on the last call, for diagnostics.</summary>
        public TileCoord LastAim { get; private set; }

        /// <summary>
        /// A heading (40-ring rotation) from <paramref name="self"/> toward <paramref name="target"/>,
        /// routing around walls via the nav graph. Returns false only when no path exists (the caller should
        /// fall back to local steering). Positions are Coordinates1000.
        /// </summary>
        public bool TryHeadingTo(INavigation nav, Point self, Point target, uint currentTick, out byte heading)
        {
            heading = 0;
            TileCoord selfTile = NavGeometry.ToTile(self.X / 1000, self.Y / 1000);
            TileCoord goalTile = NavGeometry.ToTile(target.X / 1000, target.Y / 1000);

            // Clear line to the goal for the whole HULL (not just a point): head straight and drop any stale
            // path. Hull-aware so we don't shortcut straight into a wall corner the ship's body would snag on.
            if (nav.HasClearLineForShip(selfTile, goalTile, currentTick))
            {
                _path.Clear();
                heading = Face(self, target);
                LastResult = NavResult.DirectLos;
                LastAim = goalTile;
                return true;
            }

            // (Re)plan when we have no path, the goal tile moved, or the plan is stale.
            bool goalMoved = !_hasGoal || goalTile.X != _goalTile.X || goalTile.Y != _goalTile.Y;
            if (_path.Count == 0 || goalMoved || currentTick - _lastPlanTick > ReplanIntervalTicks)
            {
                _lastPlanTick = currentTick;
                _goalTile = goalTile;
                _hasGoal = true;
                _index = 0;
                if (!nav.TryFindPath(selfTile, goalTile, currentTick, _path))
                    _path.Clear();
            }

            if (_path.Count == 0)
            {
                LastResult = NavResult.NoPath;
                LastPathLen = 0;
                LastAim = default;
                return false; // unreachable given the current door/brick state — caller falls back
            }

            // Advance past waypoints we've reached, then steer toward the next one (or the goal if consumed).
            while (_index < _path.Count && Reached(self, _path[_index]))
                _index++;

            TileCoord aimTile = _index < _path.Count ? _path[_index] : goalTile;
            Point aim = _index < _path.Count ? Center(_path[_index]) : target;
            heading = Face(self, aim);
            LastResult = NavResult.Path;
            LastPathLen = _path.Count;
            LastAim = aimTile;
            return true;
        }

        private static bool Reached(Point self, TileCoord wp)
        {
            Point c = Center(wp);
            long dx = self.X - c.X, dy = self.Y - c.Y;
            return dx * dx + dy * dy <= WaypointReachedSq;
        }

        private static Point Center(TileCoord t) => new(NavGeometry.TileCenterWorldX(t) * 1000, NavGeometry.TileCenterWorldY(t) * 1000);
        private static byte Face(Point from, Point to) => WorldState.DirectionToRotation(to.X - from.X, to.Y - from.Y);
    }
}
