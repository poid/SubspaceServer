using System;
using System.Collections.Generic;
using QS.Physics.Legacy.Structs; // ShipState
using SS.Bots.Navigation;
using SS.Packets.Game;

namespace SS.Bots.Brains
{
    /// <summary>
    /// Example navigation-driven brain: path to a goal tile and fly the path.
    /// </summary>
    /// <remarks>
    /// <para>Demonstrates how every gameplay mode consumes <see cref="INavigation"/>. The modes differ
    /// only in how they pick <see cref="_goal"/> each tick — a defended point, a moving flag/ball, a
    /// firing position behind an enemy — the path-following and steering below are shared.</para>
    /// <para>Two layers, as discussed: <see cref="INavigation.TryFindPath"/> is the global planner;
    /// the steering here (face the next waypoint, thrust when roughly aligned) is the local motion
    /// layer. It is deliberately minimal. TODO: proper "arrive" steering (slow into turns), and use
    /// the physics engine as a look-ahead oracle (clone + step a few ticks to reject a thrust that
    /// would hit a wall) instead of trusting the smoothed path blindly.</para>
    /// </remarks>
    public sealed class NavSeekBrain : IBotBrain
    {
        private readonly List<TileCoord> _path = new();
        private int _waypoint;
        private uint _lastPlanTick;
        private TileCoord _goal;

        // Replan at most this often (ticks). Between replans we follow the cached path; a moving goal
        // or a blocked path forces an earlier replan. Cheap stand-in for incremental replanning.
        private const uint ReplanInterval = 50;

        public NavSeekBrain(TileCoord goal) => _goal = goal;

        /// <summary>Update the destination (e.g. the mode is chasing a moving flag/ball/enemy).</summary>
        public void SetGoal(TileCoord goal) => _goal = goal;

        public BotDecision Think(in BotContext context)
        {
            ref readonly ShipState ship = ref context.World.Canonical.Ships[context.ShipSlot];
            int px = ship.Coordinates1000.X / 1000;
            int py = ship.Coordinates1000.Y / 1000;
            TileCoord here = NavGeometry.ToTile(px, py);

            if (NeedsReplan(context, here))
            {
                _path.Clear();
                _waypoint = 0;
                _lastPlanTick = context.CurrentTick;
                context.Navigation.TryFindPath(here, _goal, context.CurrentTick, _path);
            }

            // Advance past waypoints we've reached.
            while (_waypoint < _path.Count && Reached(here, _path[_waypoint]))
                _waypoint++;

            if (_waypoint >= _path.Count)
                return Idle(); // arrived or no path

            TileCoord wp = _path[_waypoint];
            byte desired = HeadingTo(px, py, NavGeometry.TileCenterWorldX(wp), NavGeometry.TileCenterWorldY(wp));

            // Thrust only when roughly facing the waypoint, so we don't accelerate off-course.
            bool aligned = RingDistance(ship.Rotation, desired) <= 5;
            return new BotDecision(ThrustOn: aligned, AfterburnerOn: false, Rotation: desired, Fire: WeaponCodes.Null, FireLevel: 0);
        }

        private bool NeedsReplan(in BotContext context, TileCoord here)
        {
            if (_path.Count == 0) return true;
            if (context.CurrentTick - _lastPlanTick >= ReplanInterval) return true;
            // If a brick/door change made the next hop impassable, replanning will route around it.
            return false;
        }

        private static bool Reached(TileCoord here, TileCoord wp) => Math.Abs(here.X - wp.X) <= 0 && Math.Abs(here.Y - wp.Y) <= 0;

        private static BotDecision Idle() => new(ThrustOn: false, AfterburnerOn: false, Rotation: 0, Fire: WeaponCodes.Null, FireLevel: 0);

        // Continuum ring: 0 = up (-Y), 10 = right (+X), 20 = down (+Y), 30 = left (-X).
        private static byte HeadingTo(int fromX, int fromY, int toX, int toY)
        {
            double angle = Math.Atan2(toX - fromX, -(toY - fromY)); // 0 == up, increasing clockwise
            if (angle < 0) angle += 2 * Math.PI;
            int step = (int)Math.Round(angle / (2 * Math.PI) * 40) % 40;
            return (byte)step;
        }

        // Shortest distance between two positions on the 40-step ring.
        private static int RingDistance(int a, int b)
        {
            int d = Math.Abs(a - b) % 40;
            return Math.Min(d, 40 - d);
        }
    }
}
