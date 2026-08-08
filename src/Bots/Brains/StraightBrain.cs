using QS.Physics.Legacy.Structs; // ShipState
using SS.Packets.Game;

namespace SS.Bots.Brains
{
    /// <summary>
    /// A diagnostic brain: thrust straight ahead in the ship's spawn facing and never turn, so the ship
    /// flies in a straight line until it hits something. Handy for validating wall-collision handling
    /// (bounce / stop / penetrate / kill).
    /// </summary>
    public sealed class StraightBrain : IBotBrain
    {
        public BotDecision Think(in BotContext context)
        {
            // Target the ship's own current rotation, so SteerShip issues no turn — pure forward thrust.
            ref readonly ShipState ship = ref context.World.Canonical.Ships[context.ShipSlot];
            return new BotDecision(
                ThrustOn: true,
                AfterburnerOn: false,
                Rotation: ship.Rotation,
                Fire: WeaponCodes.Null,
                FireLevel: 0);
        }
    }
}
