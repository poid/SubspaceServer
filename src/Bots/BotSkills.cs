using System;
using QS.Networking.Protocols.Common;      // ID, Point
using QS.Networking.Protocols.Game.Enums;  // TileTypes
using QS.Physics.Legacy;                    // ReplayController, WorldState, EntityLocation, LevelArray
using QS.Physics.Legacy.Structs;            // ShipState
using SS.Bots.Navigation;                   // NavGeometry, TileCoord (hull-aware wall probing)
using SS.Packets.Game;                     // WeaponCodes

namespace SS.Bots
{
    /// <summary>
    /// Reusable building blocks for bot brains — perception, aiming, steering, and threat handling on
    /// top of the physics query/forecast API.
    /// </summary>
    /// <remarks>
    /// Brains compose these into a <see cref="BotDecision"/>. Keeping the common behaviours here lets
    /// every gameplay mode (defend, flag, ball, combat) and any custom brain share them, so a brain is
    /// mostly goal selection plus a call or two below. All methods are pure reads of the world plus
    /// small maths — no side effects, no commands enqueued (the module turns the returned decision into
    /// engine input).
    /// </remarks>
    public static class BotSkills
    {
        // ---------------- Self ----------------

        /// <summary>The bot's own ship state (read-only).</summary>
        public static ref readonly ShipState Self(in BotContext ctx) => ref ctx.World.Canonical.Ships[ctx.ShipSlot];

        /// <summary>The bot's current position (Coordinates1000).</summary>
        public static Point SelfPosition(in BotContext ctx) => ctx.World.Canonical.Ships[ctx.ShipSlot].Coordinates1000;

        // ---------------- Geometry / aiming ----------------

        /// <summary>The 40-ring rotation facing from one point toward another (0 = up, increasing clockwise).</summary>
        public static byte FaceRotation(Point from, Point to) => WorldState.DirectionToRotation(to.X - from.X, to.Y - from.Y);

        /// <summary>Euclidean distance squared between two points (Coordinates1000² — divide sqrt by 16000 for tiles).</summary>
        public static long DistanceSquared(Point a, Point b)
        {
            long dx = a.X - b.X;
            long dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>Shortest distance between two rotations on the 40-position ring (0..20).</summary>
        public static int RingDistance(int a, int b)
        {
            int d = Math.Abs(a - b) % 40;
            return Math.Min(d, 40 - d);
        }

        /// <summary>Whether <paramref name="current"/> faces within <paramref name="tolerance"/> ring positions of <paramref name="target"/>.</summary>
        public static bool IsFacing(int current, int target, int tolerance) => RingDistance(current, target) <= tolerance;

        /// <summary>
        /// Adds up to ± <paramref name="maxError"/> ring positions of deterministic pseudo-random error to
        /// a rotation, for difficulty-scaled aim. Deterministic in (tick, slot) so it varies each tick
        /// without an RNG or allocation. Returns the rotation unchanged when <paramref name="maxError"/> ≤ 0.
        /// </summary>
        public static byte Jitter(byte rotation, int maxError, uint tick, int slot)
        {
            if (maxError <= 0)
                return rotation;

            uint h = tick * 2654435761u + (uint)slot * 40503u;
            int err = (int)(h % (uint)(2 * maxError + 1)) - maxError;
            return (byte)(((rotation + err) % 40 + 40) % 40);
        }

        /// <summary>
        /// Solves the firing intercept against a target using this ship's bullet speed: the rotation to
        /// face for a lead shot, and ticks-to-hit. False if the target can't be intercepted (outruns the bullet).
        /// </summary>
        public static bool TryAimBullet(in BotContext ctx, ID targetId, out byte fireRotation, out int ticksToHit)
            => TryAimWeapon(in ctx, targetId, Self(ctx).ShipSettings.BulletSpeed, out fireRotation, out ticksToHit);

        /// <summary>
        /// Solves the firing intercept against a target for an arbitrary projectile speed (1000-scale/tick) —
        /// e.g. bullets (<see cref="Structs.ShipSettings.BulletSpeed"/>) or bombs
        /// (<see cref="Structs.ShipSettings.BombSpeed"/>). False if the target outruns the projectile.
        /// </summary>
        public static bool TryAimWeapon(in BotContext ctx, ID targetId, int projectileSpeed1000, out byte fireRotation, out int ticksToHit)
            => ctx.World.TrySolveIntercept(ctx.SelfId, targetId, projectileSpeed1000, out _, out ticksToHit, out fireRotation);

        /// <summary>Whether the bot's primary weapon is off cooldown this tick (ready to fire).</summary>
        public static bool WeaponReady(in BotContext ctx) => ctx.CurrentTick >= Self(ctx).NextWeaponFireTick;

        // ---------------- Targeting ----------------

        /// <summary>The nearest enemy this bot currently has line of sight to — the best shoot target.</summary>
        public static bool TryGetTarget(in BotContext ctx, out EntityLocation enemy, long maxRange1000 = 0)
            => ctx.World.TryGetClosestVisibleEnemy(ctx.SelfId, out enemy, maxRange1000);

        // ---------------- Threats ----------------

        /// <summary>
        /// The soonest incoming enemy weapon that will hit within <paramref name="lookaheadTicks"/> and
        /// impacts within <paramref name="reactWithinTicks"/> — i.e. worth dodging right now.
        /// </summary>
        public static bool TryGetImminentThreat(in BotContext ctx, int lookaheadTicks, int reactWithinTicks, out EntityLocation weapon)
        {
            if (ctx.World.TryGetIncomingThreat(ctx.SelfId, lookaheadTicks, out weapon, out int ticksToImpact, out _)
                && ticksToImpact <= reactWithinTicks)
                return true;

            weapon = default;
            return false;
        }

        // ---------------- Safe zones ----------------

        private const int TileSize1000 = 16000; // Coordinates1000 per tile (16 px × 1000)

        // 8 compass directions (screen coords: +x right, +y down) for scanning out of a safe zone.
        private static readonly (int dx, int dy)[] Compass8 =
            { (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1) };

        /// <summary>
        /// Whether a world point (Coordinates1000) sits on a safe-zone tile. Inside a safe zone the engine
        /// suppresses weapon fire and blocks damage, so a bot must neither camp there nor chase an enemy
        /// onto one. Out-of-bounds points count as not-safe.
        /// </summary>
        public static bool IsSafeZone(in BotContext ctx, Point position1000)
        {
            int tx = position1000.X / TileSize1000;
            int ty = position1000.Y / TileSize1000;
            if ((uint)tx >= 1024u || (uint)ty >= 1024u)
                return false;
            return ctx.World.Canonical.Level.GetTile(tx, ty) == TileTypes.SafeTile;
        }

        /// <summary>Whether the bot itself is standing on a safe-zone tile.</summary>
        public static bool SelfInSafeZone(in BotContext ctx) => IsSafeZone(in ctx, SelfPosition(in ctx));

        /// <summary>
        /// Finds the shortest way out of the current safe zone: scans the 8 compass directions outward and
        /// returns the facing toward the nearest open (empty) tile. False if none is found within
        /// <paramref name="maxTiles"/> (e.g. boxed in) — the caller should just keep moving.
        /// </summary>
        public static bool TryFindSafeExit(in BotContext ctx, out byte rotation, int maxTiles = 20)
        {
            LevelArray level = ctx.World.Canonical.Level;
            Point self = SelfPosition(in ctx);
            int stx = self.X / TileSize1000;
            int sty = self.Y / TileSize1000;

            int bestDist = int.MaxValue;
            (int dx, int dy) bestDir = default;

            foreach ((int dx, int dy) in Compass8)
            {
                for (int step = 1; step <= maxTiles; step++)
                {
                    int tx = stx + dx * step;
                    int ty = sty + dy * step;
                    if ((uint)tx >= 1024u || (uint)ty >= 1024u)
                        break; // ran off the map in this direction

                    TileTypes t = level.GetTile(tx, ty);
                    if (t == TileTypes.SafeTile)
                        continue; // still inside the safe zone — keep scanning outward

                    // First non-safe tile in this direction: take it only if it's open space to head into.
                    if (t == TileTypes.Empty && step < bestDist)
                    {
                        bestDist = step;
                        bestDir = (dx, dy);
                    }
                    break; // stop at the first non-safe tile regardless of type
                }
            }

            if (bestDist == int.MaxValue)
            {
                rotation = 0;
                return false;
            }
            rotation = WorldState.DirectionToRotation(bestDir.dx, bestDir.dy);
            return true;
        }

        // ---------------- Walls / routing ----------------

        private const int MapTiles = 1024;

        /// <summary>Whether the bot has a clear line of fire to the enemy (radar sees through walls; guns don't).</summary>
        public static bool HasShot(in BotContext ctx, ID targetId) => ctx.World.HasLineOfSight(ctx.SelfId, targetId);

        /// <summary>
        /// Steers a desired heading around walls: keeps it if the straight path is clear for a few tiles,
        /// otherwise fans out left/right and returns the nearest clear heading. Whisker avoidance — cheap and
        /// allocation-free, enough to slide along/around simple obstacles (not a full path planner). This is
        /// what stops radar-driven pursuit from ramming a bot straight into the wall between it and its target.
        /// </summary>
        public static byte AvoidWalls(in BotContext ctx, byte desired, int probeTiles = 6)
        {
            if (HeadingClear(in ctx, desired, probeTiles))
                return desired;

            // Fan out to a full half-turn each side so a hull that's boxed in on its desired heading can
            // still find an escape — including turning nearly around when the only clearance is behind it.
            for (int off = 1; off <= 20; off++)
            {
                byte left = (byte)((desired + off) % 40);
                if (HeadingClear(in ctx, left, probeTiles))
                    return left;
                byte right = (byte)((desired + 40 - off) % 40);
                if (HeadingClear(in ctx, right, probeTiles))
                    return right;
            }
            return desired; // boxed in on all sides — keep heading toward the goal
        }

        /// <summary>
        /// Heading toward a (possibly far) point, routing around walls via the bot's A* path-follower — for
        /// goal-directed movement (a ball goal, a defend spot, a distant enemy). Falls back to local whisker
        /// avoidance if there's no agent or no path. Combat close-in steering should keep using
        /// <see cref="AvoidWalls"/> directly; this is for getting somewhere the straight line can't reach.
        /// </summary>
        public static byte NavigateTo(in BotContext ctx, Point target)
        {
            Point self = SelfPosition(in ctx);
            if (ctx.Agent is not null && ctx.Agent.TryHeadingTo(ctx.Navigation, self, target, ctx.CurrentTick, out byte heading))
                return AvoidWalls(in ctx, heading); // follow the route, but dodge a wall the straight hop to the waypoint would hit
            return AvoidWalls(in ctx, FaceRotation(self, target));
        }

        private const int MoveThrustTolerance = 7; // ring positions (~63°): only thrust when roughly on-heading

        /// <summary>
        /// A full move decision toward a (possibly far) point: path-follow the heading, and thrust ONLY when
        /// roughly pointed that way. The thrust gate is what stops a slow-turning bot from thrusting into a
        /// wall while it pivots — pinning itself against it. Use this for goal-directed movement.
        /// </summary>
        // Below this speed there's no point braking. Above it, if we're barrelling toward an obstacle in our
        // direction of travel we reverse-thrust to shed speed. Coordinates1000/tick.
        private const int BrakeSpeed1000 = 700;
        // Higher speed => look further ahead for the wall we need to brake for (braking distance grows with speed).
        private const double BrakeLookaheadScale = 220.0;

        public static BotDecision Navigate(in BotContext ctx, Point target)
        {
            ref readonly ShipState s = ref Self(in ctx);
            byte heading = SlideAlongWalls(in ctx, NavigateTo(in ctx, target));

            // Anticipatory braking: if we're moving fast and an obstacle is coming up in our PATH OF TRAVEL,
            // face along our velocity and thrust REVERSE to bleed speed (so we can round the corner instead of
            // ramming it), then resume once it's clear/slow. A clear line keeps FULL forward speed — no cruise
            // cap, so bots stay as fast as a real player on the open.
            long speedSq = (long)s.Velocity.X * s.Velocity.X + (long)s.Velocity.Y * s.Velocity.Y;
            if (speedSq > (long)BrakeSpeed1000 * BrakeSpeed1000)
            {
                byte velHeading = WorldState.DirectionToRotation(s.Velocity.X, s.Velocity.Y);
                int lookahead = (int)Math.Clamp(3 + Math.Sqrt(speedSq) / BrakeLookaheadScale, 3, 14);
                if (!HeadingClear(in ctx, velHeading, lookahead))
                    return Steer(velHeading, thrust: false) with { ThrustReverse = true };
            }

            bool onHeading = IsFacing(s.Rotation, heading, MoveThrustTolerance);
            return Steer(heading, thrust: onHeading);
        }

        // ~Continuum ship collision radius (px), in Coordinates1000. Ships are ~14px; the ball is ~2px,
        // which is why a thin centre-line ray (fine for the ball) lets the wider hull clip a corner.
        private const int ShipRadius1000 = 14 * 1000;

        // Clear if the ship's whole HULL can travel probeTiles ahead along `rotation` without hitting a wall.
        // Casts three parallel rays offset by ±ship radius across the width of travel, so a wall the centre
        // line misses — a corner the body would clip — still reads as blocked (a thin ray does not). Uses the
        // nav LOS over raw walls (0 = up, clockwise).
        private static bool HeadingClear(in BotContext ctx, byte rotation, int probeTiles)
        {
            Point self = SelfPosition(in ctx);
            double a = rotation * (Math.PI / 20.0);       // 2π/40 per ring position
            double hx = Math.Sin(a), hy = -Math.Cos(a);   // heading unit (screen coords: +x right, +y down)
            double perpX = -hy, perpY = hx;               // perpendicular unit (hull width axis)
            long dist = (long)probeTiles * TileSize1000;
            int max = (MapTiles * 16 - 1) * 1000;

            for (int side = -1; side <= 1; side++)
            {
                int ox = (int)(perpX * ShipRadius1000) * side;
                int oy = (int)(perpY * ShipRadius1000) * side;
                int sx = Math.Clamp(self.X + ox, 0, max);
                int sy = Math.Clamp(self.Y + oy, 0, max);
                int ex = Math.Clamp(self.X + ox + (int)(hx * dist), 0, max);
                int ey = Math.Clamp(self.Y + oy + (int)(hy * dist), 0, max);
                if (!ctx.Navigation.HasLineOfSight(NavGeometry.ToTile(sx / 1000, sy / 1000), NavGeometry.ToTile(ex / 1000, ey / 1000), ctx.CurrentTick))
                    return false;
            }
            return true;
        }

        private const int WallSlideMarginPx = 3; // small look-ahead so we slide just before grinding a wall

        /// <summary>
        /// Adjusts a heading so the ship never grinds into a wall it's already against. If the hull's leading
        /// edge on an axis touches a wall, that axis is dropped so the ship thrusts ALONG the wall instead of
        /// into it. Pushing into a wall bounces and damps ALL velocity every tick (the physics reflects the
        /// impact axis and damps the orthogonal), so grinding escapes agonisingly slowly; sliding lets the free
        /// axis accelerate cleanly. Returns the heading unchanged when both axes are clear (open space) or both
        /// are blocked (a true inside corner — the caller's wall-avoidance fan handles that).
        /// </summary>
        public static byte SlideAlongWalls(in BotContext ctx, byte heading)
        {
            double a = heading * (Math.PI / 20.0);
            double sinA = Math.Sin(a), fwdY = -Math.Cos(a); // screen coords: +x right, +y down
            int dx = sinA > 0.15 ? 1 : (sinA < -0.15 ? -1 : 0);
            int dy = fwdY > 0.15 ? 1 : (fwdY < -0.15 ? -1 : 0);

            if (dx > 0 && BoxBlocked(in ctx, 1, 0)) dx = 0;
            else if (dx < 0 && BoxBlocked(in ctx, -1, 0)) dx = 0;
            if (dy > 0 && BoxBlocked(in ctx, 0, 1)) dy = 0;
            else if (dy < 0 && BoxBlocked(in ctx, 0, -1)) dy = 0;

            if (dx == 0 && dy == 0)
                return heading; // open on neither axis (inside corner) — nothing to slide to
            return WorldState.DirectionToRotation(dx, dy);
        }

        // True if the ship's collision box, stepped one hull-radius toward (signX,signY), overlaps a wall on
        // that axis — the same leading-edge extent check the physics uses (PlayerCollisionFunctions), so it
        // reflects real geometry rather than a thin ray. Exactly one of signX/signY is non-zero.
        private static bool BoxBlocked(in BotContext ctx, int signX, int signY)
        {
            ref readonly ShipState s = ref Self(in ctx);
            LevelArray lvl = ctx.World.Canonical.Level;
            long r = (long)s.Radius * 1000;
            long reach = r + WallSlideMarginPx * 1000;
            long X = s.Coordinates1000.X, Y = s.Coordinates1000.Y;

            if (signX != 0)
            {
                int col = (int)((X + signX * reach) / 1000) / 16;
                int y0 = (int)((Y - r) / 1000) / 16, y1 = (int)((Y + r) / 1000) / 16;
                for (int ty = y0; ty <= y1; ty++)
                    if (IsSolidTile(lvl, col, ty)) return true;
            }
            else
            {
                int row = (int)((Y + signY * reach) / 1000) / 16;
                int x0 = (int)((X - r) / 1000) / 16, x1 = (int)((X + r) / 1000) / 16;
                for (int tx = x0; tx <= x1; tx++)
                    if (IsSolidTile(lvl, tx, row)) return true;
            }
            return false;
        }

        // Whether a tile blocks a ship, mirroring the sim's IsTileBlocking (PlayerCollisionFunctions): tiles
        // 1..161 are solid, doors are treated as passable here (their state isn't a grind-wall concern), and
        // the high special/decorative range is passable. Out-of-bounds counts as wall.
        private static bool IsSolidTile(LevelArray lvl, int tx, int ty)
        {
            if ((uint)tx >= MapTiles || (uint)ty >= MapTiles) return true;
            TileTypes t = lvl.GetTile(tx, ty);
            if (t == TileTypes.Empty) return false;
            if (t < TileTypes.DoorTileVertical162) return true;         // 1..161 solid
            if (t <= TileTypes.DoorTileHorizontal169) return false;      // 162..169 doors
            if (t >= TileTypes.FlagTile && t <= TileTypes.InvisibleAfterFlyUnder) return false;
            return t switch
            {
                TileTypes.WormHole or TileTypes.WeaponSink or TileTypes.BrickBlocker or TileTypes.AnimatedGreen => false,
                _ => true,
            };
        }

        // ---------------- Decision builders ----------------

        /// <summary>Face a rotation, optionally thrusting and/or firing.</summary>
        public static BotDecision Steer(byte rotation, bool thrust, WeaponCodes fire = WeaponCodes.Null, byte fireLevel = 0)
            => new(ThrustOn: thrust, AfterburnerOn: false, Rotation: rotation, Fire: fire, FireLevel: fireLevel);

        /// <summary>Head toward a world point: face it and thrust.</summary>
        public static BotDecision SeekPoint(in BotContext ctx, Point target, bool thrust = true)
            => Steer(FaceRotation(SelfPosition(ctx), target), thrust);

        /// <summary>Hold the current facing without thrusting (drift / coast).</summary>
        public static BotDecision Coast(in BotContext ctx) => Steer(Self(ctx).Rotation, thrust: false);

        /// <summary>Dodge a threat by thrusting perpendicular to its approach line.</summary>
        public static BotDecision Evade(in BotContext ctx, in EntityLocation threat)
        {
            Point self = SelfPosition(ctx);

            // Incoming direction ≈ from the weapon toward us; dodge perpendicular to it (rotate 90°).
            int inx = self.X - threat.Position.X;
            int iny = self.Y - threat.Position.Y;
            byte dodge = WorldState.DirectionToRotation(iny, -inx);
            return Steer(dodge, thrust: true);
        }
    }
}
