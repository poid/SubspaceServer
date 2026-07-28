using SS.Packets.Game;

namespace SS.Bots.Brains
{
    /// <summary>
    /// The simplest possible brain: hold thrust and rotate one step per tick, so the ship flies in a
    /// loop. It ignores navigation entirely and exists only to prove the end-to-end pipeline — fake
    /// player, simulation, and <see cref="SS.Core.ComponentInterfaces.IGame.FakePosition"/> broadcast
    /// — makes a ship visibly move on real clients. Not a real opponent. For a navigation-driven
    /// example see <see cref="NavSeekBrain"/>.
    /// </summary>
    public sealed class OrbitBrain : IBotBrain
    {
        private byte _rotation;

        public BotDecision Think(in BotContext context)
        {
            _rotation = (byte)((_rotation + 1) % 40);
            return new BotDecision(
                ThrustOn: true,
                AfterburnerOn: false,
                Rotation: _rotation,
                Fire: WeaponCodes.Null,
                FireLevel: 0);
        }
    }
}
