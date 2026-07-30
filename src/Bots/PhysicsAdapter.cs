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

        /// <summary>Fires a weapon in the sim (a real player's carried fire, or a bot's decision).</summary>
        public static PhysicsCommand WeaponFireCommand(int slot, uint tick, WeaponCodes code, byte level)
        {
            return new PhysicsCommand
            {
                Tick = tick,
                Kind = CommandKind.WeaponFire,
                ShipSlot = slot,
                WeaponType = ToPhysicsWeapon(code),
                WeaponLevel = level,
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
    }
}
