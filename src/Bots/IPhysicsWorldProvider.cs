using SS.Core;
using QS.Physics.Legacy;

namespace SS.Bots
{
    /// <summary>
    /// Produces a fully-configured physics world for an arena.
    /// </summary>
    /// <remarks>
    /// This is the main remaining integration seam. Before a <see cref="ReplayController"/> can
    /// simulate, <see cref="ReplayController.Configure"/> must be given three things, all of
    /// which the server already has but which must be translated into the engine's types:
    /// <list type="number">
    ///   <item>the arena's map as a <c>LevelArray</c> (from the server's loaded .lvl tile data);</item>
    ///   <item>the collision-relevant arena settings as a <c>CollisionArenaSettings</c>;</item>
    ///   <item>the ship/arena/prize settings as a <c>GameSettings</c>, parsed from the arena's
    ///         Continuum client settings.</item>
    /// </list>
    /// Implementations return <see langword="null"/> when those inputs are not available for an
    /// arena, in which case the module runs without a simulation for that arena.
    /// </remarks>
    public interface IPhysicsWorldProvider
    {
        /// <param name="arena">The arena to build a world for.</param>
        /// <param name="currentTick">The tick to seed the controller's clock with.</param>
        /// <returns>A configured, ready-to-<see cref="ReplayController.Tick"/> controller, or
        /// <see langword="null"/> if the arena's physics inputs are unavailable.</returns>
        ReplayController? CreateWorld(Arena arena, uint currentTick);
    }
}
