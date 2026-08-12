using SS.Core;

namespace SS.PowerBall.ComponentCallbacks
{
    /// <summary>
    /// Callback fired by the Teams module when all teams are ready to start a league match. This is the port of the
    /// ASSS <c>CB_TEAMSREADY</c> callback. The PowerBallLeague module subscribes to it to run the match countdown.
    /// </summary>
    [CallbackHelper]
    public static partial class TeamsReadyCallback
    {
        public delegate void TeamsReadyDelegate(Arena arena);
    }
}
