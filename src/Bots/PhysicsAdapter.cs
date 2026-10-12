using QS.Networking.Protocols.Common; // ID
using QS.Networking.Protocols.Game;   // ShipTypes, WeaponTypes
using QS.Physics.Legacy;              // Point, PhysicsCommand, CommandKind
using QS.Physics.Legacy.Structs;      // ShipState
using SS.Core;                         // ShipType
using SS.Packets.Game;                 // C2S_PositionPacket, WeaponCodes, PlayerPositionStatus

namespace SS.Bots
{
    /// <summary>
    /// The single seam between the two worlds:
    /// <list type="bullet">
    ///   <item>Server side — <c>SS.Packets.Game</c>: pixel coordinates, <see cref="WeaponCodes"/>, <see cref="ShipType"/>.</item>
    ///   <item>Physics side — <c>QS</c>: 1000-scaled coordinates, <see cref="WeaponTypes"/>, <see cref="ShipTypes"/>.</item>
    /// </list>
    /// Every conversion between the two representations lives here so the units and enum
    /// mappings are defined in exactly one place.
    /// </summary>
    internal static class PhysicsAdapter
    {
        /// <summary>
        /// Wire coordinates are pixels; <see cref="ShipState.Coordinates1000"/> is pixels * 1000.
        /// Confirmed by the physics engine's own docs ("pixel coords (Coordinates1000 / 1000)").
        /// </summary>
        private const int CoordScale = 1000;

        // Wire XSpeed/YSpeed are the SAME unit as the engine's Velocity: 1:1, no scaling. Both are
        // Coordinates1000 per tick, i.e. the ship's "Speed" setting unit — the engine clamps a ship's
        // Velocity magnitude directly against TopSpeedCurrent (= the Speed setting), and Continuum's
        // max wire XSpeed is likewise the Speed setting. Confirmed against the physics engine's
        // ShipPositionUpdate velocity-clamp tests.
        private const int VelocityScale = 1;

        // The engine's weapon Direction is a 0..39999 ring (FullRotation1000 = 40000, i.e. 40 ship
        // rotations × 1000), 0 = up, increasing clockwise — the same orientation as the 40-position ship
        // rotation. So a ship rotation (0..39) maps to a fire direction by ×1000.
        private const int RotationToDirection = 1000;

        // Enum values are identical across the two type systems (verified): ShipType.Warbird(0)
        // .. Spec(8) == ShipTypes.WARBIRD(0) .. SPECTATOR(8); WeaponCodes == WeaponTypes for
        // Bullet(1)..Thor(8). A straight byte cast is therefore safe for the in-game ships/weapons.
        public static ShipTypes ToPhysicsShip(ShipType ship) => (ShipTypes)(byte)ship;
        public static WeaponTypes ToPhysicsWeapon(WeaponCodes code) => (WeaponTypes)(byte)code;

        /// <summary>Builds the outbound position packet that describes a simulated ship.</summary>
        public static C2S_PositionPacket ToPositionPacket(in ShipState s)
        {
            return new C2S_PositionPacket
            {
                Rotation = (sbyte)s.Rotation,
                X = (short)(s.Coordinates1000.X / CoordScale),
                Y = (short)(s.Coordinates1000.Y / CoordScale),
                XSpeed = (short)(s.Velocity.X / VelocityScale),
                YSpeed = (short)(s.Velocity.Y / VelocityScale),
                Bounty = 0,                              // TODO: track and emit bounty from the sim
                Status = PlayerPositionStatus.Inert,
                Energy = (short)(s.Energy1000 / 1000),   // real sim energy (Energy1000 is energy * 1000)
            };
        }

        /// <summary>Introduces a ship into the sim the first time it is seen (real player or bot).</summary>
        public static PhysicsCommand ShipAddCommand(int slot, uint tick, int externalId, ShipType ship, short freq, in C2S_PositionPacket pos)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.ShipAddition,
                ShipSlot = slot,
                EntityId = new ID(externalId),
                ShipType = ToPhysicsShip(ship),
                Frequency = (ushort)freq,
                Rotation = (byte)pos.Rotation,
                Position = new Point(pos.X * CoordScale, pos.Y * CoordScale),
                Velocity = new Point(pos.XSpeed * VelocityScale, pos.YSpeed * VelocityScale),
            };
        }

        /// <summary>Authoritative position override, applied from every real client packet.</summary>
        public static PhysicsCommand ShipPositionCommand(int slot, uint tick, in C2S_PositionPacket pos)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.ShipPositionUpdate,
                ShipSlot = slot,
                Rotation = (byte)pos.Rotation,
                Position = new Point(pos.X * CoordScale, pos.Y * CoordScale),
                Velocity = new Point(pos.XSpeed * VelocityScale, pos.YSpeed * VelocityScale),
            };
        }

        /// <summary>
        /// Fires a real player's carried weapon in the sim. The engine spawns the projectile from the
        /// command's OWN position/direction/velocity (mirroring a real C2S fire packet) — NOT from the
        /// ship slot — so these must be filled from the same position packet that carried the fire, or the
        /// bullet spawns at (0,0) pointing up and never hits anything.
        /// </summary>
        public static PhysicsCommand WeaponFireCommand(int slot, uint tick, in C2S_PositionPacket pos)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.WeaponFire,
                ShipSlot = slot,
                WeaponType = ToPhysicsWeapon(pos.Weapon.Type),
                WeaponLevel = pos.Weapon.Level,
                Position = new Point(pos.X * CoordScale, pos.Y * CoordScale),
                Direction = pos.Rotation * RotationToDirection,
                Velocity = new Point(pos.XSpeed * VelocityScale, pos.YSpeed * VelocityScale),
            };
        }

        /// <summary>
        /// Fires a bot's decided weapon in the sim, spawning it from the bot's current simulated position,
        /// facing, and velocity. Same requirement as the real-player overload: the engine reads the spawn
        /// point/direction/velocity off the command, so they come from the firing ship's live state.
        /// The weapon is already a physics-side <see cref="WeaponTypes"/> — the bot AI decides in QS terms,
        /// so no conversion is needed (unlike the real-player packet overload, which maps WeaponCodes).
        /// </summary>
        public static PhysicsCommand WeaponFireCommand(int slot, uint tick, WeaponTypes weapon, byte level, in ShipState firer)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.WeaponFire,
                ShipSlot = slot,
                WeaponType = weapon,
                WeaponLevel = level,
                Position = firer.Coordinates1000,
                Direction = firer.Rotation * RotationToDirection,
                Velocity = firer.Velocity,
            };
        }

        // Coordinates1000 per tile: 16 px/tile × 1000. Warp coordinates (0x32 / ?warpto) are TILE coords.
        private const int TileToCoord = 16 * CoordScale;

        /// <summary>
        /// Teleports a simulated ship to a TILE coordinate and halts it, mirroring a server-initiated warp
        /// (the <c>0x32</c> packet a real client would act on). Used to apply <c>?warpto</c> to a bot, whose
        /// position is simulation-driven rather than client-reported.
        /// </summary>
        public static PhysicsCommand ShipWarpCommand(int slot, uint tick, short tileX, short tileY, byte rotation)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.ShipPositionUpdate,
                ShipSlot = slot,
                Rotation = rotation,
                Position = new Point(tileX * TileToCoord, tileY * TileToCoord),
                Velocity = default, // a warp stops the ship
            };
        }

        /// <summary>
        /// The sim's external ID for one of the server's bricks. The server numbers bricks from 0, and 0 is the
        /// one ID the sim's slot allocator reserves for "no entity", so they are shifted up by one.
        /// </summary>
        public static int BrickExternalId(in BrickData brick) => (ushort)brick.BrickId + 1;

        /// <summary>
        /// Hands the sim a brick the server's brick module has already placed: its two tile corners, team and
        /// identity, existing from <paramref name="tick"/>. The sim does not second-guess it (no ship has to
        /// own a brick or be standing there). <paramref name="alreadyOld"/> is how many ticks of its life had
        /// gone by at <paramref name="tick"/> — non-zero only when the brick is older than the sim can rewind
        /// to — so that it still expires when the real one does.
        /// </summary>
        public static PhysicsCommand BrickAddCommand(int brickSlot, uint tick, in BrickData brick, int alreadyOld)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.BrickAdd,
                BrickSlot = brickSlot,
                EntityId = new ID(BrickExternalId(in brick)),
                BrickStart = new Point(brick.X1 * TileToCoord, brick.Y1 * TileToCoord),
                BrickEnd = new Point(brick.X2 * TileToCoord, brick.Y2 * TileToCoord),
                Frequency = (ushort)brick.Freq,
                WeaponLevel = alreadyOld, // BrickAdd reads the brick's age from here
            };
        }

        /// <summary>
        /// Tells the sim how many flags a ship is carrying. The server's flag game owns the flags; the sim only
        /// needs the count, for its flag-carrier rules (damage taken, weapon level, fire delay and cost).
        /// </summary>
        public static PhysicsCommand FlagCountCommand(int shipSlot, uint tick, int flagCount)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.SetFlagCount,
                ShipSlot = shipSlot,
                WeaponLevel = flagCount, // SetFlagCount reads the count from here
            };
        }

        /// <summary>Removes a ship from the sim (bot despawn, or a real player leaving).</summary>
        public static PhysicsCommand ShipRemoveCommand(int slot, uint tick)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.ShipRemoval,
                ShipSlot = slot,
            };
        }

        /// <summary>Introduces a FREE (not-held) ball into the sim at a position. The slot must already be
        /// allocated via <see cref="ReplayController.GetOrAllocateBallSlot"/>.</summary>
        public static PhysicsCommand BallAddCommand(int ballSlot, uint tick, int ballExternalId, Point position1000, Point velocity, short freq)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.BallAdd,
                BallSlot = ballSlot,
                EntityId = new ID(ballExternalId),
                Position = position1000,
                Velocity = velocity,
                Frequency = (ushort)freq,
            };
        }

        /// <summary>Fires/kicks the ball a ship is carrying, with the given launch position and velocity.</summary>
        public static PhysicsCommand BallFireCommand(int shipSlot, int ballSlot, uint tick, Point position1000, Point velocity, short freq)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.BallFire,
                ShipSlot = shipSlot,
                BallSlot = ballSlot,
                Position = position1000,
                Velocity = velocity,
                Frequency = (ushort)freq,
            };
        }

        /// <summary>Removes a ball from the sim (e.g. to reset it after a goal).</summary>
        public static PhysicsCommand BallRemoveCommand(int ballSlot, uint tick)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.BallRemoval,
                BallSlot = ballSlot,
            };
        }
    }
}
