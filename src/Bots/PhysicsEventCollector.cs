using System.Collections.Generic;
using QS.Physics.Legacy;

namespace SS.Bots
{
    /// <summary>
    /// Buffers events published by the canonical physics snapshot during <see cref="ReplayController.Tick"/>.
    /// </summary>
    /// <remarks>
    /// The engine invokes <see cref="OnEvent"/> synchronously from inside <c>Tick</c>. To avoid
    /// re-entrancy (a handler enqueuing new commands or ending fake players while the engine is
    /// mid-tick), we only record here; <see cref="BotsModule"/> drains and acts on the buffer
    /// after <c>Tick</c> returns. One collector per arena, so events never cross arenas.
    /// </remarks>
    internal sealed class PhysicsEventCollector : IPhysicsEventListener
    {
        public readonly List<EventSink.Entry> Events = new();

        public void OnEvent(in EventSink.Entry entry) => Events.Add(entry);

        public void Clear() => Events.Clear();
    }
}
