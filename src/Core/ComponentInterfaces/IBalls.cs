using SS.Core.Map;
using SS.Utilities;
using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Goal configuration
    /// </summary>
    public enum SoccerMode
    {
        /// <summary>
        /// All goals are open for scoring by any freq.
        /// </summary>
        All,

        /// <summary>
        /// Left vs Right: Even freqs (defend left side) vs odd freqs (defend right side).
        /// </summary>
        LeftRight,

        /// <summary>
        /// Top vs Bottom: Same as <see cref="LeftRight"/> but goals oriented vertically.
        /// Even freqs (defend top) vs odd freqs (defend bottom).
        /// </summary>
        TopBottom,

        /// <summary>
        /// 4 quadrants, 1 quadrant to defend.
        /// </summary>
        QuadrantsDefend1,

        /// <summary>
        /// 4 quadrants, 3 quadrants to defend.
        /// </summary>
        QuadrantsDefend3,

        /// <summary>
        /// 4 sides, 1 side to defend.
        /// </summary>
        SidesDefend1,

        /// <summary>
        /// 4 sides, 3 sides to defend.
        /// </summary>
        SidesDefend3,
    }

    public struct BallSettings
    {
        /// <summary>
        /// The # of balls.
        /// </summary>
        public int BallCount;

        /// <summary>
        /// Mode that affects which goals each team needs to defend and which goals each team can score on.
        /// </summary>
        public SoccerMode Mode;

        /// <summary>
        /// The delay between a goal and the ball respawning.
        /// </summary>
        public int RespawnTimeAfterGoal;

        /// <summary>
        /// Centiseconds. The timer has a resolution of 25 centiseconds though.
        /// </summary>
        public int SendTime;

        /// <summary>
        /// Whether a death on a goal tile scores or not.
        /// </summary>
        public bool DeathScoresGoal;

        /// <summary>
        /// How much "pass delay" should be trimmed off for someone killing a ball carrier.
        /// </summary>
        public int KillerIgnorePassDelay;
    }

    public enum BallState
    {
        /// <summary>
        /// The ball doesn't exist.
        /// </summary>
        None,

        /// <summary>
        /// The ball is on the map or has been fired.
        /// </summary>
        OnMap,

        /// <summary>
        /// The ball is being carried.
        /// </summary>
        Carried,

        /// <summary>
        /// The ball is waiting to be spawned again.
        /// </summary>
        Waiting,
    }

    public struct BallData
    {
        /// <summary>
        /// The state of the ball.
        /// </summary>
        public BallState State;

        public short X;
        public short Y;
        public short XSpeed;
        public short YSpeed;

        /// <summary>
        /// The player that is carrying or last touched the ball.
        /// </summary>
        public Player? Carrier;

        /// <summary>
        /// Freq of the carrier.
        /// </summary>
        public short Freq;

        /// <summary>
        /// The time that the ball was last fired (will be 0 for balls being held).
        /// For <see cref="BallState.Waiting"/>, this time is the time when the ball will be re-spawned.
        /// </summary>
        public ServerTick Time;

        /// <summary>
        /// The time the server last got an update on ball data.
        /// It might differ from <see cref="Time"/> due to lag.
        /// </summary>
        public ServerTick LastUpdate;
    }

    public interface IBalls : IComponentInterface
    {
        /// <summary>
        /// Gets a copy of the current ball settings of an arena.
        /// </summary>
        /// <param name="arena">The arena to get ball settings for.</param>
        /// <param name="ballSettings">The settings if true.</param>
        /// <returns>True if settings could be retrieved. False if there was a problem getting settings (arena is null, etc).</returns>
        bool TryGetBallSettings(Arena arena, out BallSettings ballSettings);

        /// <summary>
        /// Sets the # of balls in an arena.
        /// </summary>
        /// <param name="arena">The arena to set the ball count for.</param>
        /// <param name="ballCount">The # of balls the arena should have. <see langword="null"/> to use the value from the config.</param>
        /// <returns>True if the ball count was set.  False if an invalid ball count was specified.</returns>
        bool TrySetBallCount(Arena arena, int? ballCount);

        /// <summary>
        /// Gets a copy of ball data for a specified ball in a specified arena.
        /// </summary>
        /// <param name="arena">The arena to get ball data for.</param>
        /// <param name="ballId">The Id of the ball to get data for.</param>
        /// <returns>True if the ball data was retrieved. False if an invalid <paramref name="ballId"/>.</returns>
        bool TryGetBallData(Arena arena, int ballId, out BallData ballData);

        /// <summary>
        /// Sets the parameters of a ball.
        /// </summary>
        /// <param name="arena">The arena to set ball data for.</param>
        /// <param name="ballId">The Id of the ball to set data for.</param>
        /// <param name="ballData">The data to set.</param>
        /// <returns>True if the ball data was set. False if an invalid <paramref name="ballId"/>.</returns>
        bool TryPlaceBall(Arena arena, byte ballId, ref BallData ballData);

        /// <summary>
        /// Tries to respawn a specified ball.
        /// </summary>
        /// <param name="arena">The arena to respawn the ball in..</param>
        /// <param name="ballId">The Id of the ball to respawn.</param>
        /// <returns></returns>
        bool TrySpawnBall(Arena arena, int ballId);

        /// <summary>
        /// Ends the ball game. This phases balls which will respawn. Fires <see cref="ComponentCallbacks.BallGameOverCallback"/>.
        /// </summary>
        /// <param name="arena">The arena to end the ball game for.</param>
        /// <param name="winnerFreq">The winning freq to report to <see cref="ComponentCallbacks.BallGameOverCallback"/>, or <c>-1</c> when the game ended with no single winner (timer, reset, etc.).</param>
        void EndGame(Arena arena, short winnerFreq = -1);

        /// <summary>
        /// Records a goal for a ball, running the full server-side goal handling — goal advisors
        /// (<see cref="IBallsAdvisor.AllowGoal"/>), the <see cref="ComponentCallbacks.BallGoalCallback"/>
        /// (scoring), and the normal ball respawn/phase. Intended for a host driving a ball whose goal
        /// detection happens outside the client protocol (e.g. server-side bots). The ball should be in
        /// <see cref="BallState.OnMap"/> for the respawn to occur.
        /// </summary>
        /// <param name="arena">The arena the goal occurred in.</param>
        /// <param name="ballId">The ball that scored.</param>
        /// <param name="scorer">The player credited with the goal.</param>
        /// <param name="goalTile">The goal tile the ball entered.</param>
        /// <returns>True if the goal was handled; false on invalid input.</returns>
        bool TryReportGoal(Arena arena, byte ballId, Player scorer, TileCoordinates goalTile);

        /// <summary>
        /// Requests a ball pickup on behalf of a player, running the same server-side validation and
        /// arbitration as the C2S pickup packet (<see cref="ComponentCallbacks.BallPickupCallback"/> fires on
        /// success). Intended for a host driving a player outside the client protocol (e.g. server-side bots):
        /// the ball must be <see cref="BallState.OnMap"/>, the player must not already carry a ball, goal
        /// advisors may veto, and the FIRST valid request wins a contested pickup (it flips the ball to
        /// <see cref="BallState.Carried"/>, so a later request loses). The client-only "stale coords" time
        /// check is skipped since the caller is the server.
        /// </summary>
        /// <returns>True if the pickup was granted (the player now carries the ball); otherwise false.</returns>
        bool TryPickupBall(Arena arena, byte ballId, Player player);

        /// <summary>
        /// Requests a ball shot on behalf of a player, running the same server-side handling as the C2S shoot
        /// packet — advisors, <see cref="ComponentCallbacks.BallShootCallback"/>, and goal-on-goal-tile
        /// detection. The player must currently carry the ball. Intended for a server-side bot that computed
        /// its own shot (position, velocity, and fire time).
        /// </summary>
        /// <returns>True if the shot was accepted (the ball is now on the map); otherwise false.</returns>
        bool TryShootBall(Arena arena, byte ballId, Player player, short x, short y, short xSpeed, short ySpeed, ServerTick time);

        /// <summary>
        /// Gets goal info for a coordinate in an arena, for a given team.
        /// The <paramref name="arena"/>'s <see cref="SoccerMode"/> setting is a major determining factor.
        /// </summary>
        /// <param name="arena">The arena to get information for.</param>
        /// <param name="freq">The team to get information for.</param>
        /// <param name="coordinates">The coordinates to check.</param>
        /// <param name="isScorable">True if the coordinate is a goal tile and the <paramref name="freq"/> can score on it. Otherwise, false.</param>
        /// <param name="ownerFreq">
        /// The freq that owns the goal. <see langword="null"/> if there is no owner. 
        /// There is no owner for <see cref="SoccerMode.All"/>, <see cref="SoccerMode.QuadrantsDefend3"/>, and <see cref="SoccerMode.SidesDefend3"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Arena is null.</exception>
        /// <exception cref="Exception">Invalid <see cref="SoccerMode"/> for the arena.</exception>
        void GetGoalInfo(Arena arena, short freq, TileCoordinates coordinates, out bool isScorable, out short? ownerFreq);
    }
}
