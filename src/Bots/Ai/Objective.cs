namespace SS.Bots.Ai
{
    /// <summary>
    /// A high-level goal chosen by a brain's strategic layer (a slow tick) that biases how its utility
    /// actions score. Modes map their situation to one of these; actions read it for context (e.g. an
    /// EngageEnemy action scores higher under <see cref="Fight"/> than under <see cref="Deliver"/>).
    /// </summary>
    public enum Objective : byte
    {
        Idle,
        Fight,     // hunt and kill enemies (Mode 4; also the combat sub-goal of other modes)
        Defend,    // hold / guard an objective (a base, a stationary flag)
        Retrieve,  // go get an objective (a loose ball, a capturable flag)
        Deliver,   // carry an objective somewhere (score the ball, take flags to safety)
        Support,   // help a teammate (escort a carrier, get open for a pass)
    }
}
