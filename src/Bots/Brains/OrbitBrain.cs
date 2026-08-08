using QS.Physics.Legacy.Structs; // ShipState
using SS.Packets.Game;

namespace SS.Bots.Brains
{
    /// <summary>
    /// The simplest possible brain: thrust while continuously turning, so the ship flies a tight loop.
    /// It aims a fixed lead ahead of the ship's <i>actual</i> facing, so the engine turns it at its full
    /// rotation rate every tick (a blind fixed-increment target would barely turn now that thrust is
    /// continuous). It exists to prove the end-to-end pipeline — fake player, simulation, and
    /// <see cref="SS.Core.ComponentInterfaces.IGame.FakePosition"/> broadcast — makes a ship fly like a
    /// real one. Not a real opponent; for a navigation-driven example see <see cref="NavSeekBrain"/>.
    /// </summary>
    public sealed class OrbitBrain : IBotBrain
    {
        // Ring positions to lead the current facing by. Kept below a half-turn (20) so the shortest
        // path stays clockwise and the turn direction never flips.
        private const byte LeadPositions = 8;

        public BotDecision Think(in BotContext context)
        {
            ref readonly ShipState ship = ref context.World.Canonical.Ships[context.ShipSlot];
            byte target = (byte)((ship.Rotation + LeadPositions) % 40);

            return new BotDecision(
                ThrustOn: true,
                AfterburnerOn: false,
                Rotation: target,
                Fire: WeaponCodes.Null,
                FireLevel: 0);
        }
    }
}
