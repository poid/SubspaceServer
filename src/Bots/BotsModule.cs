using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;             // Stopwatch (per-pump timing diagnostic)
using System.Text;                    // StringBuilder (staleness histogram report)
using Microsoft.Extensions.ObjectPool;
using QS.Networking.Protocols.Common; // ID
using QS.Networking.Protocols.Game;   // WeaponTypes (the bot AI's fire decision)
using QS.Physics.Legacy;              // ReplayController, PhysicsCommand, CommandKind, EventKind, EventSink
using QS.Physics.Legacy.Structs;      // ShipState
using QS.Bots;                        // IBotBrain, BotDecision, BotContext, BotSkills (now in QS.Bots)
using QS.Bots.Ai;                     // BotProfile, BallView, Perception
using QS.Bots.Brains;                 // OrbitBrain, CombatBrain, BallBrain, StraightBrain
using QS.Navigation;                  // INavigation, GridNavigation, NavAgent, NavStats, NavGeometry
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using SS.Core.Map;                                     // TileCoordinates
using QS.Networking.Protocols.Game.Enums;              // TileTypes (DIAG tile dump)
using BallState = SS.Core.ComponentInterfaces.BallState; // server ball state (enum)
using SimBall = QS.Physics.Legacy.Structs.BallState;     // sim ball entity (struct)

namespace SS.Bots
{
    /// <summary>
    /// Server-side AI players ("bots"), driven by the QS physics engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each attached arena gets its own authoritative <see cref="ReplayController"/> world. The heartbeat
    /// runs on the <b>arena's own thread</b> via <see cref="IArenaTimer"/> (not the mainloop), which is
    /// where the arena's game state — including <see cref="IBalls"/>, which asserts it — must be touched.
    /// The arena's thread is the single writer of all bot state (the world, nav, and bot list), so the
    /// heartbeat needs no locks. The data flow is:
    /// </para>
    /// <list type="number">
    ///   <item>Real players' position packets arrive on <see cref="PlayerPositionPacketCallback"/>
    ///         (which fires on the mainloop, or the arena thread when packets are handled there) and are
    ///         copied into a lock-free queue. The heartbeat drains them into the world at the top of each
    ///         tick, so the simulation — and every bot — has full knowledge of the arena.</item>
    ///   <item>On a 10 Hz arena timer: each bot's <see cref="IBotBrain"/> decides, its intent is turned
    ///         into physics commands, the world is stepped, and combat is resolved from the engine's event
    ///         stream (the physics engine — not the server — is authoritative for weapons and damage).</item>
    ///   <item>Each bot's resulting motion is broadcast to real clients through
    ///         <see cref="IGame.FakePosition"/>, exactly like a human player's movement.</item>
    /// </list>
    /// <para>
    /// Because bot state has a single writer (the arena thread), the admin commands that create or remove
    /// bots do the player-lifecycle work on their own thread but route the simulation/bot-list mutations
    /// onto the arena thread with <see cref="IArenaLoop.QueueArenaWorkItem{TState}"/>.
    /// </para>
    /// <para>
    /// Attach it per-arena with the <c>Modules:AttachModules</c> setting in arena.conf. Nothing
    /// simulates until <see cref="IPhysicsWorldProvider"/> can build a configured world for the
    /// arena — see that interface and README.md for the remaining bridge work.
    /// </para>
    /// </remarks>
    [ModuleInfo("Server-side AI players (bots) driven by the QS physics engine.")]
    public sealed class BotsModule : IModule, IArenaAttachableModule
    {
        private readonly IArenaManager _arenaManager;
        private readonly IFake _fake;
        private readonly IGame _game;
        private readonly IArenaTimer _arenaTimer;
        private readonly IArenaLoop _arenaLoop;
        private readonly ILogManager _logManager;
        private readonly ICommandManager _commandManager;
        private readonly IChat _chat;
        private readonly IMapData _mapData;
        private readonly IClientSettings _clientSettings;
        private readonly IConfigManager _configManager;

        private IPhysicsWorldProvider _worldProvider = null!;
        private ArenaDataKey<ArenaData> _adKey;

        // The zone's door and green seeds (see SyncSeeds). Optional: a zone that does not load the Security
        // module sends its clients no seeds either, so there is nothing to agree with.
        private ISecuritySeedSync? _seedSync;

        /// <summary>The seeds the Security module last gave every client, and the moment they took effect.
        /// Replaced whole (never changed in place) by <see cref="Callback_SecuritySeedChanged"/> on the
        /// mainloop thread and read by each arena's pump on its own thread — hence volatile, and hence a
        /// class: a reader sees either the old set or the new one, never a mix.</summary>
        private volatile ZoneSeeds? _seeds;

        private sealed class ZoneSeeds(uint green, uint door, ServerTick timestamp)
        {
            public readonly uint Green = green;
            public readonly uint Door = door;
            public readonly ServerTick Timestamp = timestamp;
        }

        /// <summary>Milliseconds per physics tick (the sim's internal fixed timestep). 100 ticks/sec.</summary>
        private const int MsPerSimTick = 10;

        // ---- Default cadences (ms). All configurable per arena under the [Bots] section. ----
        // The three are DECOUPLED (see ArenaTimer_Pump): the sim-pump advances physics + drains real-player
        // packets often (so rewind staleness ≈ the pump interval, not 100 ms); Think runs the AI less often;
        // Send broadcasts bot positions at a human-client-like rate so bots don't look laggy.
        /// <summary>Default sim-pump cadence (ms): how often physics advances and real-player packets are
        /// drained. Smaller ⇒ shallower rewinds + more responsive, at more per-pump overhead. Rounded to a
        /// multiple of <see cref="MsPerSimTick"/>.</summary>
        private const int DefaultSimPumpMs = 20;
        /// <summary>Default AI cadence (ms): how often each bot re-decides. 10 Hz like a human's effective rate.</summary>
        private const int DefaultThinkMs = 100;
        /// <summary>Default position-send cadence (ms): how often a bot's position is broadcast. A human client
        /// sends ~10-20 ms while active; fires are sent immediately regardless of this.</summary>
        private const int DefaultSendMs = 20;
        /// <summary>Fallback lane shape (ticks) used only when <c>Bots:RewindLanes</c> is set to something
        /// unparseable. The real default is AUTO-derived from <c>Lag:PingToSpec</c> (see ReadBotSettings).</summary>
        private const string DefaultRewindLanes = "0,15,30,50,70";

        /// <summary>Default headroom (ms) added to <c>Lag:PingToSpec</c> for the auto rollback horizon. A
        /// player's position-packet staleness can sit this far over PingToSpec without being specced (the spec
        /// metric is a blended EWMA of ping, so it lags and under-weights position staleness); the histogram's
        /// <c>beyond%</c> tail is the signal to raise this if real packets are being clamped.</summary>
        private const int DefaultRewindHorizonMarginMs = 100;

        /// <summary>Default / maximum number of snapshot lanes between the live lane (0) and the horizon when
        /// the lanes are auto-derived. See the <c>Bots:IntermediateLanes</c> help text for what each is for.</summary>
        private const int DefaultIntermediateLanes = 2;
        private const int MaxIntermediateLanes = 3;

        /// <summary>Defaults for the controller's lane auto-tune (<see cref="ReplayController.EnableLaneAutoTune"/>):
        /// how often it reconsiders the intermediate lanes, the headroom it leaves above observed lag, and how
        /// much a lane must be able to tighten before it is worth moving.</summary>
        private const int DefaultLaneRetuneMinutes = 10;
        private const int DefaultLaneRetuneMarginTicks = 3;
        private const int DefaultLaneRetuneFlexTicks = 3;

        /// <summary>Default for <c>Bots:MaxCommandsPerTick</c>: the sim commands per tick the rollback's
        /// command buffer is sized for. A steering bot issues up to 2 a tick (3 when braking), a real player
        /// well under 1, so this covers roughly 60 bots plus players.</summary>
        private const int DefaultMaxCommandsPerTick = 128;

        /// <summary>The sim clock follows the wall clock: each pump runs as many ticks as real time has
        /// moved on since the last one. After a stall it repays the backlog at no more than this many times
        /// the normal pump size per pump, so the catch-up is spread over a few pumps instead of being one
        /// long one that stalls everything queued behind it on the arena thread.</summary>
        private const int MaxCatchUpFactor = 4;

        /// <summary>How far (ticks) the sim may fall behind the wall clock before the backlog is written
        /// off instead of repaid. Only reached when the sim cannot run in real time at all; without it the
        /// backlog would grow without bound and every pump would be a maximum-size one forever.</summary>
        private const int MaxTickDebt = 50;

        /// <summary>Ticks between "cannot keep up" warnings.</summary>
        private const uint OverloadWarnIntervalTicks = 1000;

        /// <summary>Velocity² (Coordinates1000/tick, squared) below which a bot counts as "nearly stopped" for
        /// the adaptive send back-off (mirrors Continuum sub_43C920's near-stationary check).</summary>
        private const long NearlyStoppedSpeedSq = 500L * 500;

        /// <summary>How far to inflate walls when building the nav grid. TODO: derive from ship radius.</summary>
        private const int ShipRadiusTiles = 1;


        /// <summary>Most bricks kept waiting for a sim that does not exist yet (no bot has been spawned in the
        /// arena). Comfortably more than a client can show at once; older ones are dropped first.</summary>
        private const int MaxPendingBricks = 512;

        /// <summary>External id for the arena's single simulated ball (ball ID space is separate from ships).</summary>
        private const int SimBallExternalId = 0xBA11;

        public BotsModule(
            IArenaManager arenaManager,
            IFake fake,
            IGame game,
            IArenaTimer arenaTimer,
            IArenaLoop arenaLoop,
            ILogManager logManager,
            ICommandManager commandManager,
            IChat chat,
            IMapData mapData,
            IClientSettings clientSettings,
            IConfigManager configManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _fake = fake ?? throw new ArgumentNullException(nameof(fake));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _arenaTimer = arenaTimer ?? throw new ArgumentNullException(nameof(arenaTimer));
            _arenaLoop = arenaLoop ?? throw new ArgumentNullException(nameof(arenaLoop));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        #region Module life-cycle

        bool IModule.Load(IComponentBroker broker)
        {
            _worldProvider = new PhysicsWorldProvider(_mapData, _clientSettings, _logManager);
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _seedSync = broker.GetInterface<ISecuritySeedSync>();
            if (_seedSync is not null)
            {
                _seedSync.GetCurrentSeedInfo(out uint greenSeed, out uint doorSeed, out uint timestamp);
                _seeds = new ZoneSeeds(greenSeed, doorSeed, new ServerTick(timestamp));
            }
            SecuritySeedChangedCallback.Register(broker, Callback_SecuritySeedChanged);

            _commandManager.AddCommand("spawnbot", Command_spawnbot);
            _commandManager.AddCommand("killbots", Command_killbots);
            _commandManager.AddCommand("loadtest", Command_loadtest);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _commandManager.RemoveCommand("spawnbot", Command_spawnbot);
            _commandManager.RemoveCommand("killbots", Command_killbots);
            _commandManager.RemoveCommand("loadtest", Command_loadtest);

            SecuritySeedChangedCallback.Unregister(broker, Callback_SecuritySeedChanged);
            if (_seedSync is not null)
                broker.ReleaseInterface(ref _seedSync);
            _seeds = null;

            _arenaManager.FreeArenaData(ref _adKey);
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            // The physics world is created lazily (see EnsureWorld), on the first ?spawnbot — NOT here.
            // At attach time (arena creation) the ClientSettings module may not have loaded the arena's
            // settings yet; configuring the sim then yields all-zero ship settings (no thrust/speed/
            // energy), so the ship spawns frozen. By the time a player spawns a bot, settings are loaded.
            //
            // The heartbeat runs on the ARENA's thread (IArenaTimer), not the mainloop, so it can touch the
            // arena's game state — including IBalls, which asserts it runs on the arena thread.
            ReadBotSettings(arena, ad);

            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPosition);
            WarpToCallback.Register(arena, Callback_WarpTo);
            BricksPlacedCallback.Register(arena, Callback_BricksPlaced);
            FlagGainCallback.Register(arena, Callback_FlagGain);
            FlagLostCallback.Register(arena, Callback_FlagLost);
            int pumpMs = (int)ad.PumpTicks * MsPerSimTick;
            _arenaTimer.SetTimer<Arena>(arena, ArenaTimer_Pump, pumpMs, pumpMs, arena, arena);
            return true;
        }

        [ConfigHelp<int>("Bots", "SimPumpInterval", ConfigScope.Arena, Default = DefaultSimPumpMs,
            Description = "How often (ms) the bot physics sim advances and real-player packets are applied. " +
                "Smaller = shallower rollback rewinds and more responsive bots, at more per-pump overhead. " +
                "Rounded to a multiple of 10 ms (the sim tick); clamped to [10, 100].")]
        [ConfigHelp<int>("Bots", "ThinkInterval", ConfigScope.Arena, Default = DefaultThinkMs,
            Description = "How often (ms) each bot re-runs its AI decision. Clamped to >= the sim-pump interval.")]
        [ConfigHelp<int>("Bots", "SendInterval", ConfigScope.Arena, Default = DefaultSendMs,
            Description = "How often (ms) a bot's position is broadcast to clients. A human client sends " +
                "~10-20 ms while active; weapon fires are sent immediately regardless. Clamped to >= the sim-pump interval.")]
        [ConfigHelp("Bots", "RewindLanes", ConfigScope.Arena,
            Description = "Comma-separated snapshot lane delays (ticks) for the real-player rollback: the rewind " +
                "checkpoints and horizon (0 is implied). LEAVE UNSET to let the server manage them — the deepest " +
                "lane becomes Lag:PingToSpec + Bots:RewindHorizonMargin, with Bots:IntermediateLanes lanes under " +
                "it that the server re-seats to fit the lag it observes (Bots:LaneRetuneInterval). Setting this " +
                "is a full manual override: the lanes stay exactly as listed and are never re-seated.")]
        [ConfigHelp<int>("Bots", "IntermediateLanes", ConfigScope.Arena, Default = DefaultIntermediateLanes, Min = 0, Max = MaxIntermediateLanes,
            Description = "How many rollback snapshot lanes sit between the live lane (0) and the rewind horizon. " +
                "Ignored when Bots:RewindLanes is set. A real player's packet always arrives a little late; the " +
                "sim rewinds to the nearest lane older than the packet and re-simulates forward from there, so " +
                "each lane is a checkpoint that caps how far that re-simulation has to run. Each lane is also a " +
                "full extra copy of the sim that is advanced every tick, so more is not free. The server places " +
                "these lanes itself from the lag it observes (see Bots:LaneRetuneInterval). " +
                "2 is what a zone normally needs: a 'crowd' lane just above the lag of most players, and a 'laggy' " +
                "lane just above the players who lag noticeably more, leaving the horizon for rare spikes. " +
                "3 can help a zone that has players from two distinct regions online together (say US and EU) " +
                "and ALSO gets occasional really laggy players: each region gets a lane and the worst laggers " +
                "get their own, instead of every one of their packets rewinding to the horizon. Two regions " +
                "without such laggers, or one region with them, are already covered by 2. " +
                "1 saves CPU on a small or single-region zone. 0 leaves only the horizon.")]
        [ConfigHelp<int>("Bots", "LaneRetuneInterval", ConfigScope.Arena, Default = DefaultLaneRetuneMinutes,
            Description = "Minutes between re-seats of the intermediate rollback lanes. Each interval the server " +
                "looks at how late real-player packets actually were and moves the lanes to sit just above each " +
                "group of lag it saw. Lanes only move for a reason: one found too shallow for the lag observed is " +
                "always deepened, one that could be shallower is moved only if it would tighten by more than " +
                "Bots:LaneRetuneFlex. With too little traffic to judge by (an empty or near-empty arena) the " +
                "lanes stay exactly where they are. Never changes the horizon or the number of lanes, needs no " +
                "restart, and does not affect what the simulation computes. 0 = off (lanes stay at their " +
                "starting spread). Ignored when Bots:RewindLanes is set.")]
        [ConfigHelp<int>("Bots", "LaneRetuneFlex", ConfigScope.Arena, Default = DefaultLaneRetuneFlexTicks,
            Description = "Ticks (10 ms each) a rollback lane must be able to tighten by before a re-seat moves " +
                "it. Keeps the lanes from being shuffled every interval over a tick or two of noise. A lane that " +
                "is too shallow is always deepened regardless.")]
        [ConfigHelp<int>("Bots", "LaneRetuneMargin", ConfigScope.Arena, Default = DefaultLaneRetuneMarginTicks,
            Description = "Ticks (10 ms each) of headroom a re-seated rollback lane keeps above the lag it is " +
                "placed for, so ordinary jitter does not push packets past it to the next, deeper lane.")]
        [ConfigHelp<int>("Bots", "MaxCommandsPerTick", ConfigScope.Arena, Default = DefaultMaxCommandsPerTick, Min = 16, Max = 4096,
            Description = "The most sim commands per 10 ms tick the rollback's command buffer is sized for. " +
                "Every command is kept until the deepest rollback lane has replayed it, so the buffer holds " +
                "this many times the rewind horizon. A bot issues up to 2 a tick while steering (3 when " +
                "braking), a real player well under 1, so the default suits about 60 bots plus players. If " +
                "more arrive than fit, the sim refuses the excess — bots stop responding and player input is " +
                "lost — and the server logs a warning that says so. Raising it costs memory, and a little CPU " +
                "on every rewind.")]
        [ConfigHelp<int>("Bots", "NavSearchLimit", ConfigScope.Arena, Default = GridNavigation.DefaultMaxSearchExpansions, Min = 1024, Max = 1048576,
            Description = "The most map tiles one bot path search may examine before giving up. Without a limit, a " +
                "search for a target that cannot be reached (walled off by bricks, say) examines the whole map " +
                "before it can tell, which stalls the arena for hundreds of milliseconds. A search that hits the " +
                "limit is treated as 'no path' and the bot steers locally instead. It has to be higher than the " +
                "longest real route on the map needs: raise it for large maze-like maps if bots stop finding " +
                "routes they should; lower it if path searches are costing too much.")]
        [ConfigHelp<int>("Bots", "RewindHorizonMargin", ConfigScope.Arena, Default = DefaultRewindHorizonMarginMs,
            Description = "Headroom (ms) added to Lag:PingToSpec for the auto rollback horizon (when " +
                "Bots:RewindLanes is unset). Covers the staleness a player can carry over PingToSpec before the " +
                "spec metric's EWMA catches up. Raise it if the histogram shows a non-trivial 'beyond' tail.")]
        [ConfigHelp<int>("Bots", "LaneStatsInterval", ConfigScope.Arena, Default = 0,
            Description = "Minutes between staleness-histogram reports — the cumulative distribution of how late " +
                "real-player packets arrived, to help tune Bots:RewindLanes. 0 = off.")]
        [ConfigHelp<int>("Bots", "LaneStatsBucket", ConfigScope.Arena, Default = 5,
            Description = "Staleness-histogram slice width in ticks (10 ms each). Smaller = finer distribution " +
                "at the cost of a wider log line. Only used when Bots:LaneStatsInterval > 0.")]
        private void ReadBotSettings(Arena arena, ArenaData ad)
        {
            ConfigHandle ch = arena.Cfg!;

            int pumpMs = _configManager.GetInt(ch, "Bots", "SimPumpInterval", DefaultSimPumpMs);
            pumpMs = Math.Clamp((pumpMs / MsPerSimTick) * MsPerSimTick, MsPerSimTick, 100); // multiple of a tick, [10,100]
            if (pumpMs < MsPerSimTick) pumpMs = MsPerSimTick;
            ad.PumpTicks = (uint)(pumpMs / MsPerSimTick);

            int thinkMs = _configManager.GetInt(ch, "Bots", "ThinkInterval", DefaultThinkMs);
            ad.ThinkTicks = (uint)Math.Max(ad.PumpTicks, (uint)(Math.Max(MsPerSimTick, thinkMs) / MsPerSimTick));

            int sendMs = _configManager.GetInt(ch, "Bots", "SendInterval", DefaultSendMs);
            ad.SendTicks = (uint)Math.Max(ad.PumpTicks, (uint)(Math.Max(MsPerSimTick, sendMs) / MsPerSimTick));

            // Lanes: an explicit Bots:RewindLanes wins (full manual control); otherwise AUTO-derive — the
            // deepest lane (horizon) = Lag:PingToSpec + Bots:RewindHorizonMargin, because a player whose packet
            // staleness exceeds that is being specced anyway (LagAction specs on a blended EWMA of ping, so
            // staleness can run a bit over PingToSpec before the average crosses — hence the margin, not a pin).
            //
            // Under the horizon go Bots:IntermediateLanes lanes. Where they start is only a first guess: the
            // controller re-seats them itself from the lag it observes (Bots:LaneRetuneInterval).
            string? lanesCfg = _configManager.GetStr(ch, "Bots", "RewindLanes");
            ad.AutoTuneLanes = false;
            ad.LaneRetuneMinutes = 0;
            if (string.IsNullOrWhiteSpace(lanesCfg))
            {
                int pingToSpecMs = _configManager.GetInt(ch, "Lag", "PingToSpec", 600);
                int marginMs = _configManager.GetInt(ch, "Bots", "RewindHorizonMargin", DefaultRewindHorizonMarginMs);
                int horizonTicks = Math.Clamp((pingToSpecMs + marginMs) / MsPerSimTick, 20, 200);
                int intermediates = Math.Clamp(
                    _configManager.GetInt(ch, "Bots", "IntermediateLanes", DefaultIntermediateLanes), 0, MaxIntermediateLanes);
                ad.Lanes = BuildAutoLanes(horizonTicks, intermediates);

                int retuneMin = Math.Clamp(_configManager.GetInt(ch, "Bots", "LaneRetuneInterval", DefaultLaneRetuneMinutes), 0, 24 * 60);
                if (retuneMin > 0 && ad.Lanes.Length > 2)
                {
                    int tuneMargin = Math.Clamp(_configManager.GetInt(ch, "Bots", "LaneRetuneMargin", DefaultLaneRetuneMarginTicks), 0, 20);
                    int tuneFlex = Math.Clamp(_configManager.GetInt(ch, "Bots", "LaneRetuneFlex", DefaultLaneRetuneFlexTicks), 0, 20);
                    ad.LaneTune = new LaneAutoTuneOptions(
                        (uint)retuneMin * 60u * 100u, tuneMargin, tuneFlex, LaneAutoTuneOptions.Default.MinSamples); // minutes → ticks (100/s)
                    ad.LaneRetuneMinutes = retuneMin;
                    ad.AutoTuneLanes = true;
                }
            }
            else
            {
                ad.Lanes = ParseLanes(lanesCfg);
            }

            ad.MaxCommandsPerTick = Math.Clamp(
                _configManager.GetInt(ch, "Bots", "MaxCommandsPerTick", DefaultMaxCommandsPerTick), 16, 4096);

            ad.NavSearchLimit = Math.Clamp(
                _configManager.GetInt(ch, "Bots", "NavSearchLimit", GridNavigation.DefaultMaxSearchExpansions), 1024, 1 << 20);

            int statsMin = _configManager.GetInt(ch, "Bots", "LaneStatsInterval", 0);
            ad.StatsIntervalTicks = statsMin > 0 ? (uint)statsMin * 60u * 100u : 0u; // minutes → ticks (100/s)

            // Fixed-width staleness slices from 0 to the horizon (+ 1 overflow bucket). Width is configurable so
            // an op can trade resolution against log width; the count follows from the deepest lane (horizon).
            ad.StatsBucketTicks = (uint)Math.Max(1, _configManager.GetInt(ch, "Bots", "LaneStatsBucket", 5));
            uint horizon = ad.Lanes[^1];
            int slices = (int)((horizon + ad.StatsBucketTicks - 1) / ad.StatsBucketTicks); // ceil(horizon / width)
            ad.StaleBuckets = new long[slices + 1]; // + overflow (staler than the horizon)
        }

        // Parses a "0,15,30,50,70" lane list into sorted, deduped, ascending tick delays with 0 guaranteed
        // present (the canonical lane). Falls back to the default on empty/garbage so a typo can't disable bots.
        private uint[] ParseLanes(string? text)
        {
            SortedSet<uint> set = [0];
            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (uint.TryParse(part, out uint v))
                        set.Add(v);
            }
            if (set.Count <= 1)
                return [0, 15, 30, 50, 70];
            uint[] lanes = new uint[set.Count];
            set.CopyTo(lanes);
            return lanes;
        }

        // Builds the STARTING lane set for a derived horizon: 0 (canonical), the horizon, and `intermediates`
        // lanes between them. These are only where the lanes sit until the controller's auto-tune has seen
        // enough real traffic to place them properly, so they are the proportions that held up best in the
        // LoadClient runs at a 70-tick horizon ({0,25,50,70}; and the historical {0,15,30,50,70} for three).
        // Deduped (a small horizon can round two fractions together).
        private static uint[] BuildAutoLanes(int horizonTicks, int intermediates)
        {
            double[] frac = intermediates switch
            {
                <= 0 => [],
                1 => [0.36],
                2 => [0.36, 0.71],
                _ => [0.214, 0.429, 0.714],
            };
            SortedSet<uint> set = [0, (uint)horizonTicks];
            foreach (double f in frac)
                set.Add((uint)Math.Round(f * horizonTicks));
            uint[] lanes = new uint[set.Count];
            set.CopyTo(lanes);
            return lanes;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            // Stop the heartbeat, then wait until the arena's thread has drained everything queued for it
            // (a running tick, plus any spawn/kill work items) before touching the bot state here on the
            // mainloop. After the drain the arena's thread is no longer a writer, so this teardown is safe.
            _arenaTimer.ClearTimer<Arena>(arena, ArenaTimer_Pump, arena);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPosition);
            WarpToCallback.Unregister(arena, Callback_WarpTo);
            BricksPlacedCallback.Unregister(arena, Callback_BricksPlaced);
            FlagGainCallback.Unregister(arena, Callback_FlagGain);
            FlagLostCallback.Unregister(arena, Callback_FlagLost);
            _arenaLoop.WaitForArenaWorkItemDrain(arena);

            foreach (Bot bot in ad.Bots)
                _fake.EndFaked(bot.Player);

            if (ad.CarryFlags is not null)
                arena.ReleaseInterface(ref ad.CarryFlags);
            if (ad.Balls is not null)
                arena.ReleaseInterface(ref ad.Balls);

            ad.World?.SetEventListener(null);
            ((IResettable)ad).TryReset();
            return true;
        }

        #endregion

        #region Real-player feed

        // Mirror every real player's motion into the arena's simulation so the world model — and therefore
        // every bot — reflects reality. Bots are excluded (they are driven by the sim, not fed back into it).
        //
        // This fires on the mainloop (or the arena thread when Net:HandlePacketsOnArenaThread is on), which
        // is NOT necessarily the arena thread that owns the sim. So it must not touch the sim directly: it
        // only copies the packet into a lock-free queue that the heartbeat drains on the arena thread. This
        // is the hot path (every real player, ~10 Hz), so it takes no lock.
        private void Callback_PlayerPosition(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (player.Type == ClientType.Fake)
                return;

            // Spectators don't collide or fire, so feeding their motion into the sim does nothing useful — and
            // once commands are stamped at the packet's (past) tick, every spectator packet would spuriously
            // trigger a rollback rebuild. Skip them; they're fed again the moment they enter a ship.
            if (player.Ship == ShipType.Spec)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return;

            ad.PositionInputs.Enqueue(new PositionInput(player, player.Ship, player.Freq, in positionPacket));
        }

        // A server op issued ?warpto (any IGame.WarpTo) on a player. If it's one of our bots, apply the warp
        // to its SIMULATED ship — the bot has no real client, so the 0x32 warp packet goes nowhere, and the
        // sim is the authority for its position. Lets ops reposition / unstick a single bot with the standard
        // command. Fires on the caller's (command/mainloop) thread, so the sim mutation is routed onto the
        // arena's thread like ?spawnbot. Coordinates are TILE coords (matching the warp packet).
        private void Callback_WarpTo(Player player, short tileX, short tileY)
        {
            if (player.Type != ClientType.Fake)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return;

            int playerId = player.Id;
            _arenaLoop.QueueArenaWorkItem(arena, _ =>
            {
                ReplayController? world = ad.World;
                if (world is null)
                    return;

                Bot? bot = FindBot(ad, playerId);
                if (bot is null)
                    return;

                byte rotation = world.Canonical.Ships[bot.ShipSlot].Rotation;
                world.EnqueueCommand(PhysicsAdapter.ShipWarpCommand(bot.ShipSlot, world.CurrentTick + 1, tileX, tileY, rotation));
            }, arena);
        }

        // The server's brick module has placed brick(s) — a real player's, or a staff command's. The sim has
        // to have them too, or bots fly straight through walls every client can see. This can fire on any
        // thread, and `bricks` is a pooled list the brick module takes back as soon as we return, so only
        // copy the (value-type) entries into a lock-free queue; the pump hands them to the sim.
        private void Callback_BricksPlaced(Arena arena, Player? player, IReadOnlyList<BrickData> bricks)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            for (int i = 0; i < bricks.Count; i++)
                ad.BrickInputs.Enqueue(bricks[i]);

            // Nothing drains the queue until the arena has a sim (the first bot spawned). Until then keep
            // only the most recent bricks, so an arena that never gets a bot cannot grow it forever.
            while (ad.BrickInputs.Count > MaxPendingBricks && ad.BrickInputs.TryDequeue(out _)) { }
        }

        // The server's flag game changed how many flags a player carries. For a bot, the sim needs the new
        // count (it applies the flag-carrier rules to the bot's ship). Like the brick callback this can fire
        // on any thread, so it only notes WHO changed; the pump reads the count and tells the sim.
        private void Callback_FlagGain(Arena arena, Player player, short flagId, FlagPickupReason reason)
            => NoteFlagCountChanged(arena, player);

        private void Callback_FlagLost(Arena arena, Player player, short flagId, FlagLostReason reason)
            => NoteFlagCountChanged(arena, player);

        private void NoteFlagCountChanged(Arena arena, Player player)
        {
            if (player.Type == ClientType.Fake && arena.TryGetExtraData(_adKey, out ArenaData? ad))
                ad.FlagChanges.Enqueue(player);
        }

        // The Security module has picked new door and green seeds (it does once a minute) and is sending them
        // to every client. Fires on the mainloop thread, so only note them; each arena's pump hands them to
        // its sim on its own thread (SyncSeeds).
        private void Callback_SecuritySeedChanged(uint greenSeed, uint doorSeed, ServerTick timestamp)
            => _seeds = new ZoneSeeds(greenSeed, doorSeed, timestamp);

        #endregion

        #region Bot heartbeat

        // The bot SIM-PUMP. Runs on the ARENA's thread (via IArenaTimer) — the single writer of all bot state,
        // so every call below (IBalls, FakePosition, FakeKill, the sim) is made directly. This is ONE of the
        // three decoupled cadences (see AttachModule): it advances physics and drains real-player packets every
        // pump (small, so rewind staleness stays low); AI Think() runs every ThinkTicks; position SEND
        // runs every SendTicks (and immediately on a fire). All three are gated off world.CurrentTick below.
        private bool ArenaTimer_Pump(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return true; // keep the timer alive; nothing to simulate yet

            ReplayController world = ad.World;

            // ---- SIM CLOCK: run as many ticks as real time has moved on, not a fixed number per pump. ----
            // The arena timer re-arms only after this method returns, so pumps arrive every
            // (SimPumpInterval + however long the pump took), not every SimPumpInterval. Advancing a fixed
            // number of ticks each time therefore ran the sim slow — a 12 ms pump on a 20 ms interval gave
            // 0.6x real time, and it got worse the busier the arena was. TickDebt is how many ticks the sim
            // owes the wall clock; each pump adds what has elapsed and repays what it can.
            ServerTick now = ServerTick.Now;
            int elapsed = now - ad.LastPumpTime;
            ad.LastPumpTime = now;
            if (elapsed > 0)
                ad.TickDebt += elapsed;

            int maxSteps = (int)ad.PumpTicks * MaxCatchUpFactor;
            if (ad.TickDebt > MaxTickDebt)
            {
                // More than half a second behind: the sim cannot run in real time at this load (or the process
                // was frozen). Write the backlog off rather than repay it — repaying would only make every
                // pump longer and put the sim further behind. Sim time slips against the wall clock by the
                // amount written off; the warning below says so.
                int writtenOff = ad.TickDebt - maxSteps;
                ad.SkippedTicks += writtenOff;
                ad.SkippedTotal += writtenOff;
                ad.TickDebt = maxSteps;
            }

            int steps = Math.Min(ad.TickDebt, maxSteps);
            if (steps <= 0)
                return true; // fired again inside the same 10 ms tick: nothing to simulate yet

            // PERF (temporary): time the pump BODY (drain + think + sim + events + send) — the number the
            // LoadClient test reads. Excludes the periodic diag/histogram logging below (that is scaffolding).
            long pumpStart = Stopwatch.GetTimestamp();

            // Pull real players' latest positions/fire out of the lock-free queue and into the sim before
            // anything reads the world this pump. Doing this EVERY pump (not every 100 ms) is what keeps the
            // rollback staleness ≈ the pump interval.
            SyncSeeds(ad, arena, world, now);
            DrainPositionInputs(ad, world, now);
            DrainBrickInputs(ad, arena, world, now);
            DrainFlagChanges(ad, world);

            // Ball (Mode 3): the SERVER owns ownership; the sim only PREDICTS. Reconcile the sim to the
            // server's authoritative ball each pump (seeds it, and corrects last pump's misprediction).
            ReconcileBallToSim(ad, arena, world);

            // PERF (temporary): where the pump's time goes — drain / think / sim ticks / everything else.
            long splitMark = Stopwatch.GetTimestamp();
            ad.SplitDrain += splitMark - pumpStart;

            // ---- THINK cadence: re-decide each bot + refresh the nav overlay. The decision is HELD between
            //      updates and executed by every pump below, so a slower Think still yields smooth motion. ----
            if (world.CurrentTick - ad.LastThinkTick >= ad.ThinkTicks)
            {
                ad.LastThinkTick = world.CurrentTick;
                ThinkAllBots(ad, arena, world);
            }
            {
                long afterThink = Stopwatch.GetTimestamp();
                ad.SplitThink += afterThink - splitMark;
                splitMark = afterThink;
            }

            // ---- SIM pump: steer each bot every tick (rate-limited turn + thrust → real ship motion), apply
            //      each bot's one-shot action (fire / kick) ONCE per decision, and advance the sim a tick at a
            //      time (so the event collector sees every tick's combat events). ----
            for (int sub = 0; sub < steps; sub++)
            {
                uint cmdTick = world.CurrentTick + 1;
                foreach (Bot bot in ad.Bots)
                {
                    if (bot.IsDead)
                        continue; // don't steer or fire a corpse (fixes phantom rapid-fire after death)

                    BotDecision d = bot.Decision;
                    world.SteerShip(new ID(bot.Player.Id), cmdTick, d.Rotation, d.ThrustOn, d.AfterburnerOn);

                    // Reverse thrust (brake): SteerShip only does forward thrust, so issue a ThrustOnly with
                    // ThrustReverse ourselves. It turns toward d.Rotation (via SteerShip) while decelerating.
                    if (d.ThrustReverse)
                        world.EnqueueCommand(new PhysicsCommand
                        {
                            Tick = cmdTick,
                            Kind = CommandKind.ThrustOnly,
                            ShipSlot = bot.ShipSlot,
                            ThrustReverse = true,
                            AfterburnerOn = d.AfterburnerOn,
                        });

                    // One-shot per decision (fire + ball-kick), on the FIRST pump tick after a new Think — NOT
                    // every tick (the engine's fire delay still governs the real cadence). Spawn the weapon from
                    // the bot's live sim state and CAPTURE that exact spawn state + fire tick, so the weapon
                    // packet we send carries it verbatim — a client re-simulating from the packet must spawn the
                    // weapon at the identical point/heading/velocity, or its sim diverges from ours. Every weapon.
                    if (bot.NewDecisionPending && sub == 0)
                    {
                        if (d.Fire != WeaponTypes.None)
                        {
                            ref readonly ShipState fireState = ref world.Canonical.Ships[bot.ShipSlot];
                            bot.FireShip = fireState;
                            bot.FireTick = cmdTick;
                            world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(bot.ShipSlot, cmdTick, d.Fire, d.FireLevel, in fireState));
                        }
                        if (d.BallAction == BallIntent.Fire && ad.SimBallSlot >= 0)
                            TryBotKick(ad, world, bot, cmdTick, d.BallTarget);
                        bot.NewDecisionPending = false;
                    }
                }
                long beforeTick = Stopwatch.GetTimestamp();
                world.Tick(1);
                ad.SplitSim += Stopwatch.GetTimestamp() - beforeTick;

                // Keep the server (authoritative) and sim (predictor) agreeing on ball ownership each tick:
                // request the pickup/shot the sim predicts, then reconcile the sim to the server's ruling.
                SyncBallOwnership(ad, arena, world);
            }
            ad.TickDebt -= steps; // what is left (normally 0) is how far the sim still trails the wall clock
            ad.StepsSum += steps;

            // Resolve combat + ball events from the physics event stream (accumulated across this pump's ticks).
            // Sets each firing bot's weapon-visual (FiredThisTick + LastFiredType/Level), deaths, goals, rebuild count.
            ProcessEvents(ad, arena, world);

            // Final reconcile: pull in any server ball change since the ticks so the sim ends matching truth.
            ReconcileBallToSim(ad, arena, world);

            // ---- Position SEND: per-bot ADAPTIVE rate, mirroring Continuum's own position-send logic
            //      (decompile sub_43C920). A fire goes out IMMEDIATELY (once) from its exact fire-time spawn
            //      state; otherwise a bot sends at the base rate (Bots:SendInterval) while moving/turning, and
            //      backs off to 2x when holding position and 4x when nearly stopped — so bots look as responsive
            //      and as bandwidth-cheap as a real client. ----
            foreach (Bot bot in ad.Bots)
            {
                if (bot.FiredThisTick)
                {
                    SendBot(ad, bot, world, weapon: true); // weapon packet carries a position too
                    bot.FiredThisTick = false;         // consumed — the weapon is sent exactly once
                    continue;
                }

                if (bot.IsDead)
                    continue; // a corpse neither moves nor needs updates (it'll re-enter on respawn)

                ref readonly ShipState ship = ref world.Canonical.Ships[bot.ShipSlot];
                bool moved = ship.Coordinates1000.X != bot.LastSentPos.X
                          || ship.Coordinates1000.Y != bot.LastSentPos.Y
                          || ship.Rotation != bot.LastSentRot;

                uint delay = ad.SendTicks; // base: moving/turning
                if (!moved)
                {
                    delay *= 2; // holding position/heading
                    long speedSq = (long)ship.Velocity.X * ship.Velocity.X + (long)ship.Velocity.Y * ship.Velocity.Y;
                    if (speedSq < NearlyStoppedSpeedSq)
                        delay *= 2; // nearly stopped → back off further (sub_43C920 does the same 2x/2x)
                }

                // A warp is sent at once, like a fire: the bot is suddenly somewhere else.
                if (bot.WarpFlashPending || world.CurrentTick - bot.LastSentTick >= delay)
                    SendBot(ad, bot, world, weapon: false);
            }

            // PERF (temporary): end of the measured pump body — accumulate for the 1 Hz REWIND report.
            long pumpDelta = Stopwatch.GetTimestamp() - pumpStart;
            ad.PumpTimeSum += pumpDelta;
            if (pumpDelta > ad.PumpTimeMax)
                ad.PumpTimeMax = pumpDelta;
            ad.PumpSamples++;

            // ---- Health (NOT a diagnostic — keep): say so when the sim is refusing commands. ----
            // A refused command is bot steering that never happened or player input that never reached the
            // sim. The refusals arrive as PendingCommandsOverflow events (counted in ProcessEvents).
            if (world.CurrentTick - ad.LastHealthTick >= 100)
            {
                ad.LastHealthTick = world.CurrentTick;
                ad.RefusedLastSecond = ad.RefusedCommands;
                if (ad.RefusedCommands > 0)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena,
                        $"The bot sim refused {ad.RefusedCommands} command(s) in the last second — its command buffer is full, " +
                        $"so bots are not steering and player input is being lost. Raise Bots:MaxCommandsPerTick (now {ad.MaxCommandsPerTick}).");
                    ad.RefusedCommands = 0;
                }
            }

            // ---- Health (keep): say so when the sim cannot keep up with real time. ----
            if (ad.SkippedTicks > 0 && world.CurrentTick - ad.LastOverloadWarnTick >= OverloadWarnIntervalTicks)
            {
                ad.LastOverloadWarnTick = world.CurrentTick;
                _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena,
                    $"The bot sim cannot keep up with real time: {ad.SkippedTicks} tick(s) ({ad.SkippedTicks * MsPerSimTick} ms) written off " +
                    $"since the last warning, with {ad.Bots.Count} bot(s). Bots and their shots are running slow for players.");
                ad.SkippedTicks = 0;
            }

            // ---- Staleness histogram (configurable, for lane tuning): every Bots:LaneStatsInterval minutes. ----
            if (ad.StatsIntervalTicks > 0 && world.CurrentTick - ad.LastStatsTick >= ad.StatsIntervalTicks)
            {
                ad.LastStatsTick = world.CurrentTick;
                ReportStaleHistogram(ad, arena);
            }

            // DIAG (temporary): once/sec, one line PER bot so we see the actual combatants (freq, target
            // freq, fire state) and how close enemy bullets get. fq==tgtFq would mean teammates (no hits).
            // nWpn is the nearest ENEMY bullet in px: near the ~18px hit box with no WeaponHit => collision
            // scan; never close => bullets never reach the ship (spawn/travel/aim). ~1/sec (100 ticks).
            if (world.CurrentTick - ad.LastDiagTick >= 100)
            {
                ad.LastDiagTick = world.CurrentTick;

                // DIAG/PERF (temporary): rollback activity + pump cost over the last SIM second (which is
                // now also a wall second, unless `behind`/`skipped` say the sim is trailing). rebuilds/s = how
                // many pumps rewound the lanes; refused/s = commands the sim refused (must be 0); maxLate =
                // the furthest behind the sim's own tick a real-player packet landed (0 = everything was on
                // time). pump = pumps this second / average ticks each ran / how long the pump body took,
                // average and worst (µs) — the LoadClient test's headline numbers. behind = ticks the sim
                // still owes the wall clock right now; skipped = ticks written off because it could not keep
                // up. Then reset the window.
                long freq = Stopwatch.Frequency;
                long pumpAvgUs = ad.PumpSamples > 0 ? ad.PumpTimeSum * 1_000_000 / freq / ad.PumpSamples : 0;
                long pumpMaxUs = ad.PumpTimeMax * 1_000_000 / freq;
                double stepsAvg = ad.PumpSamples > 0 ? (double)ad.StepsSum / ad.PumpSamples : 0;
                // nav = path searches this second / how many found no path / how many were cut off at the
                // search limit / the most tiles a SUCCESSFUL search examined (vs GridNavigation.MaxSearchExpansions).
                NavSearchStats nav = ad.Nav is GridNavigation grid ? grid.TakeSearchStats() : default;
                _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                    $"REWIND rebuilds/s={ad.RebuildCount} refused/s={ad.RefusedLastSecond} maxLate={ad.MaxStaleTicks}t ({ad.MaxStaleTicks * 10}ms) lanes=[{string.Join(",", ad.Lanes)}] pump={ad.PumpSamples}x{stepsAvg:0.0}t/{pumpAvgUs}us avg/{pumpMaxUs}us max behind={ad.TickDebt}t skipped={ad.SkippedTotal}t bricks={world.Canonical.ActiveBrickCount} wpnFix[players +{ad.WeaponCorrections[0]}/-{ad.WeaponCorrections[1]}/~{ad.WeaponCorrections[2]} bots +{ad.WeaponCorrections[3]}/-{ad.WeaponCorrections[4]}/~{ad.WeaponCorrections[5]}] ms/s[drain={ad.SplitDrain * 1000 / freq} think={ad.SplitThink * 1000 / freq} sim={ad.SplitSim * 1000 / freq} total={ad.PumpTimeSum * 1000 / freq}] nav={nav.Searches}/{nav.Failed}/{nav.CutOff}/{nav.MostExaminedBySuccess} think={ad.ThinkTicks}t send={ad.SendTicks}t bots={ad.Bots.Count}");
                ad.RebuildCount = 0;
                ad.MaxStaleTicks = 0;
                System.Array.Clear(ad.WeaponCorrections);
                ad.PumpTimeSum = 0;
                ad.PumpTimeMax = 0;
                ad.PumpSamples = 0;
                ad.StepsSum = 0;
                ad.SplitDrain = ad.SplitThink = ad.SplitSim = 0;

                // The per-bot CBT/DMG/NAV/MAP dump is for debugging a handful of bots — it's heavy (MAP alone
                // is 9 lines × bots) and would swamp + skew a load test, so skip it past a small count. The
                // REWIND/LANESTATS lines above (the load-test metrics) always print.
                foreach (Bot bot in ad.Bots)
                {
                    if (ad.Bots.Count > 8)
                        break;
                    ID id = new(bot.Player.Id);
                    ref readonly ShipState s = ref world.Canonical.Ships[bot.ShipSlot];
                    bool hasTgt = world.TryGetClosestEnemy(id, out EntityLocation tgt);
                    int distTiles = hasTgt ? (int)(System.Math.Sqrt(tgt.DistanceSquared) / 16000) : -1;
                    bool hasWpn = world.TryGetClosestThreat(id, out EntityLocation wpn);
                    long wpnPx = hasWpn ? (long)(System.Math.Sqrt(wpn.DistanceSquared) / 1000) : -1;
                    // rr = current rotation rate the engine uses; ir = the arena's InitialRotation setting.
                    // If rr/ir are far below your zone's Rotation setting, the sim is reading a low value
                    // (settings/parse); if they match it but turning still looks slow, it's the rotation
                    // rate calibration or the sim-pump size, not the value.
                    _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                        $"CBT {bot.Player.Name} fq={s.Frequency} tile=({s.Coordinates1000.X / 16000},{s.Coordinates1000.Y / 16000}) rot={s.Rotation}->{bot.Decision.Rotation} thr={bot.Decision.ThrustOn} rev={bot.Decision.ThrustReverse} vel=({s.Velocity.X},{s.Velocity.Y}) rad={s.Radius} rr={s.RotationRateCurrent} nav={bot.Nav.LastResult}/{bot.Nav.LastPathLen} ball={bot.Decision.BallAction} tgtFq={(hasTgt ? tgt.Frequency : -1)} dist={distTiles}t");

                    // DMG diag (temporary): weapon-damage debugging. energy = current/max; nearWpn = nearest
                    // ENEMY weapon in px (ship hit box ~18px, so < ~20 means it should be connecting);
                    // bombDmg/bulletDmg = the sim's ArenaSettings damage the hit code actually uses. If those
                    // are 0 the sim got a zero-damage settings blob (feed bug) and no hit can hurt; if they're
                    // non-zero and energy stays pinned at max while nearWpn goes small, hits land but damage
                    // is being blocked/reset.
                    _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                        $"DMG {bot.Player.Name} energy={s.Energy1000 / 1000}/{s.EnergyMax} nearWpn={wpnPx}px bombDmg={world.Canonical.GameSettings.arenas.BombDamageLevel} bulletDmg={world.Canonical.GameSettings.arenas.BulletDamageLevel}");

                    // NAV diag: pinpoint why pathing fails. selfR/goalR = nav region ids (-1 => that tile is
                    // itself nav-blocked, i.e. sits in an inflated-wall/door tile). conn = the two regions link
                    // through the region graph. stats = regions/portals/blockedTiles — a blockedTiles count that
                    // is a large fraction of the 1,048,576-tile map means wall inflation is closing gaps that
                    // ships can actually fly through (the prime suspect for a stuck bot on a small map).
                    if (ad.Nav is not null)
                    {
                        TileCoord selfT = NavGeometry.ToTile(s.Coordinates1000.X / 1000, s.Coordinates1000.Y / 1000);
                        int selfR = ad.Nav.GetRegion(selfT);
                        int goalR = -2; bool conn = false; TileCoord goalT = default;
                        if (world.TryGetNearestGoal(s.Coordinates1000, s.Frequency, out Point gp))
                        {
                            goalT = NavGeometry.ToTile(gp.X / 1000, gp.Y / 1000);
                            goalR = ad.Nav.GetRegion(goalT);
                            conn = ad.Nav.AreConnected(selfT, goalT, world.CurrentTick);
                        }
                        NavStats st = ad.Nav.Stats;
                        _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                            $"NAV {bot.Player.Name} selfTile=({selfT.X},{selfT.Y}) selfR={selfR} goalTile=({goalT.X},{goalT.Y}) goalR={goalR} conn={conn} aim=({bot.Nav.LastAim.X},{bot.Nav.LastAim.Y}) stats={st.RegionCount}/{st.PortalCount}/{st.BlockedTiles}");

                        // MAP diag: the SIM's actual collision tiles around the bot (ground truth for the wedge).
                        // '@'=bot, '.'=empty, '#'=solid wall, 'd'=door, 'G'=goal, 'S'=safe, 'F'=flag, 'o'=fly-over,
                        // 'w'=wormhole. If '#' sits diagonally adjacent to '@', the ship's radius box overlaps it
                        // and blocks BOTH axes toward it — a wedge a centre-line probe can't see.
                        LevelArray lvl = world.Canonical.Level;
                        for (int dy = -4; dy <= 4; dy++)
                        {
                            char[] rowc = new char[15];
                            for (int dx = -7; dx <= 7; dx++)
                            {
                                int tx = selfT.X + dx, ty = selfT.Y + dy;
                                char c;
                                if (dx == 0 && dy == 0) c = '@';
                                else if ((uint)tx >= 1024 || (uint)ty >= 1024) c = ' ';
                                else c = lvl.GetTile(tx, ty) switch
                                {
                                    TileTypes.Empty => '.',
                                    TileTypes.GoalTile => 'G',
                                    TileTypes.SafeTile => 'S',
                                    TileTypes.FlagTile => 'F',
                                    TileTypes.WormHole => 'w',
                                    >= TileTypes.DoorTileVertical162 and <= TileTypes.DoorTileHorizontal169 => 'd',
                                    >= TileTypes.FlyOverTile1 and <= TileTypes.InvisibleAfterFlyUnder => 'o',
                                    _ => '#',
                                };
                                rowc[dx + 7] = c;
                            }
                            _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena, $"MAP {bot.Player.Name} y={selfT.Y + dy} {new string(rowc)}");
                        }
                    }
                }

                // DIAG (temporary): ball seed/pickup/location. inUse=false => not seeded (bots see no ball);
                // carrier=-1 => loose; compare simTile to srv to spot a mirror/location mismatch.
                if (ad.Balls is not null)
                {
                    WorldState wc = world.Canonical;
                    int bslot = ad.SimBallSlot;
                    bool inUse = bslot >= 0 && (uint)bslot < (uint)wc.MaxBalls && wc.BallSlotInUse[bslot];
                    // Carrier + the goal THAT carrier's freq is told to score in. If goal=(-1,-1) the freq has
                    // no scorable goal (mode/ownership issue); if two opposing-freq carriers show the SAME goal
                    // tile, ownership isn't splitting by side (mode 0, or goals both one side of the x=512 line).
                    int carrier = -1, carrierFq = -1, goalTx = -1, goalTy = -1, hold = -1;
                    if (inUse && wc.TryGetBallCarrierSlot(bslot, out int cs))
                    {
                        ref readonly ShipState cShip = ref wc.Ships[cs];
                        carrier = cShip.ID.Int;
                        carrierFq = cShip.Frequency;
                        hold = cShip.BallCarryTimeLeft; // ticks before the sim auto-throws — the "shoot before this hits 0" signal
                        if (world.TryGetNearestGoal(cShip.Coordinates1000, cShip.Frequency, out Point cg))
                        { goalTx = cg.X / 16000; goalTy = cg.Y / 16000; }
                    }
                    int bx = inUse ? wc.Balls[bslot].Coordinates1000.X / 16000 : -1;
                    int by = inUse ? wc.Balls[bslot].Coordinates1000.Y / 16000 : -1;
                    string srv = ad.Balls.TryGetBallData(arena, 0, out BallData sbd) ? $"{sbd.State}@({sbd.X / 16},{sbd.Y / 16})" : "none";
                    _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                        $"BALL slot={bslot} inUse={inUse} simTile=({bx},{by}) carrier={carrier} carrierFq={carrierFq} hold={hold} goal=({goalTx},{goalTy}) srv={srv}");
                }
            }

            return true;
        }

        // Runs each live bot's AI once (the THINK cadence), storing a decision that every sim-pump then
        // executes until the next Think — so a slower AI rate still yields smooth, rate-limited motion. Flags a
        // fresh decision so the pump performs its one-shot actions (fire / kick) exactly once. Dead/entering
        // bots are skipped and zeroed. Also refreshes the nav overlay. Runs on the arena thread.
        private void ThinkAllBots(ArenaData ad, Arena arena, ReplayController world)
        {
            if (ad.Nav is not null)
            {
                // Doors: what is open now, when they last changed, and the arena's own Door:DoorDelay and
                // Door:DoorMode — a fixed mode (0-255) means a closed door never opens, so nav must treat it
                // as a wall rather than as somewhere to wait.
                WorldState canonical = world.Canonical;
                ad.Nav.UpdateDoors(canonical.DoorOpenBitmask, canonical.LastDoorSwitchTick,
                    canonical.GameSettings.arenas.DoorDelay, doorsCycle: canonical.GameSettings.arenas.DoorMode < 0);
                ad.Nav.PruneExpiredBricks(world.CurrentTick);
            }

            BallData serverBall = default;
            bool haveBall = ad.Balls is not null
                && ad.Balls.TryGetBallData(arena, 0, out serverBall)
                && serverBall.State is BallState.OnMap or BallState.Carried;

            foreach (Bot bot in ad.Bots)
            {
                // The bot's carried-flag count reaches the sim as a COMMAND when the server's flag game says
                // it changed (Callback_FlagGain / Callback_FlagLost → DrainFlagChanges). This is only the
                // safety net behind that: the sim also clears the count on its own — when the ship dies, and
                // when its own carry timer runs out — and the server's flag game does not always follow (no
                // client ever sends a bot's "dropped my flags" packet). So if the two have drifted apart, say
                // so again — through the command stream, never by writing the lane directly: a direct write
                // exists in one lane only and the next rollback rebuild throws it away.
                // TODO(threading): ICarryFlagGame (CarryFlags.cs) has no OnArenaThread assertion, unlike IBalls,
                // so calling it from this arena-thread pump is not yet verified thread-safe. It is null (skipped)
                // in the combat/ball arenas used for testing; verify before running bots in a carry-flag arena.
                if (ad.CarryFlags is not null)
                {
                    int serverCount = ad.CarryFlags.GetFlagCount(bot.Player);
                    if (world.Canonical.Ships[bot.ShipSlot].FlagCount != (uint)Math.Max(0, serverCount))
                        world.EnqueueCommand(PhysicsAdapter.FlagCountCommand(bot.ShipSlot, world.CurrentTick + 1, serverCount));
                }

                ref readonly ShipState s = ref world.Canonical.Ships[bot.ShipSlot];
                bot.IsDead = s.DeathRemainingTimer > 0 || s.EnterDelayRemaining > 0;
                if (bot.IsDead)
                {
                    bot.Decision = default;
                    bot.NewDecisionPending = false;
                    continue;
                }

                BotContext context = new()
                {
                    World = world,
                    Navigation = ad.Nav!, // non-null whenever World is (both built in EnsureWorld)
                    ShipSlot = bot.ShipSlot,
                    SelfId = new ID(bot.Player.Id),
                    CurrentTick = world.CurrentTick,
                    Agent = bot.Nav,
                    Ball = haveBall ? BuildBallView(bot, world, ad.SimBallSlot, in serverBall) : default,
                };
                bot.Decision = bot.Brain.Think(in context);
                bot.NewDecisionPending = true; // one-shot actions (fire / kick) consumed by the next pump
            }
        }

        // Broadcasts one bot through the server's normal position path (relayed to / extrapolated by clients
        // exactly like a real player). When weapon=true, sends the pending weapon from its EXACT fire-time spawn
        // state (position/facing/velocity) so a client re-simulating from the packet stays deterministic, with
        // the fire tick backdated into Time (→ C2SLatency, so a traveling weapon fast-forwards to our sim).
        // Otherwise sends the bot's current position. Runs on the arena thread.
        private void SendBot(ArenaData ad, Bot bot, ReplayController world, bool weapon)
        {
            // The sim's current tick is "now" less whatever the sim still owes the wall clock (normally 0).
            // Packet times are stamped from that, so a client places the bot at the moment the sim state
            // actually describes.
            ServerTick simNow = ServerTick.Now - (uint)Math.Max(0, ad.TickDebt);

            C2S_PositionPacket pos;
            if (weapon)
            {
                pos = PhysicsAdapter.ToPositionPacket(in bot.FireShip);
                pos.Weapon.Type = bot.LastFiredType;
                pos.Weapon.Level = bot.LastFiredLevel;
                uint age = world.CurrentTick > bot.FireTick ? world.CurrentTick - bot.FireTick : 0u;
                pos.Time = simNow - age;
                // The weapon packet conveys the FIRE-TIME position, so that's what the client now has.
                bot.LastSentPos = bot.FireShip.Coordinates1000;
                bot.LastSentRot = bot.FireShip.Rotation;
            }
            else
            {
                ref readonly ShipState ship = ref world.Canonical.Ships[bot.ShipSlot];
                pos = PhysicsAdapter.ToPositionPacket(in ship);
                pos.Time = simNow;
                bot.LastSentPos = ship.Coordinates1000;
                bot.LastSentRot = ship.Rotation;

                // The sim warped this ship since its last position went out. A real client says so with the
                // flash bit on its next position packet — which is also what makes the server relay that
                // packet to everyone, so nobody is left watching the bot where it used to be.
                if (bot.WarpFlashPending)
                {
                    pos.Status |= PlayerPositionStatus.Flash;
                    bot.WarpFlashPending = false;
                }
            }
            bot.LastSentTick = world.CurrentTick;
            pos.SetChecksum();
            _game.FakePosition(bot.Player, ref pos);
        }

        // OPS/DIAG: dumps the real-player packet-staleness distribution (how late arriving packets were) as a
        // CUMULATIVE curve over fixed time slices, then resets it. The CDF directly answers lane-placement: the
        // "<=Nt" value is the share of packets a deepest-lane-at-N would catch, and "beyond" is the share staler
        // than the current horizon (clamped — those risk dropping, so widen the horizon if it's non-trivial).
        // Current lanes are printed alongside so an op can line them up against the curve. Example read: if
        // "<=70t=96%" then a 70-tick horizon serves 96% and 4% are clamped; the steepest rise shows the mode.
        private void ReportStaleHistogram(ArenaData ad, Arena arena)
        {
            long total = 0;
            foreach (long c in ad.StaleBuckets)
                total += c;

            StringBuilder sb = new();
            sb.Append($"LANESTATS window={ad.StatsIntervalTicks / 6000}min total={total} slice={ad.StatsBucketTicks}t horizon={ad.Lanes[^1]}t lanes=[{string.Join(",", ad.Lanes)}]");
            if (total > 0)
            {
                long cum = 0;
                int full = ad.StaleBuckets.Length - 1; // all but the overflow bucket
                sb.Append(" cdf:");
                for (int i = 0; i < full; i++)
                {
                    cum += ad.StaleBuckets[i];
                    uint upper = (uint)(i + 1) * ad.StatsBucketTicks;
                    sb.Append($" <={upper}t={100.0 * cum / total:0.0}%");
                }
                long over = ad.StaleBuckets[full];
                sb.Append($" beyond={100.0 * over / total:0.0}%(clamped)");
            }
            _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena, sb.ToString());
            System.Array.Clear(ad.StaleBuckets);
        }

        // Gives the sim the zone's door and green seeds whenever they change (and once when the world is new).
        //
        // In the random door modes (Door:DoorMode -1 / -2) which doors are open is rolled from a seed, every
        // Door:DoorDelay ticks counted from the moment the seed was issued. Every client is sent that seed and
        // that moment by the Security module and works the doors out for itself; the server never says "door
        // 3 is open". So the sim has to be given the same two numbers, or it rolls different doors from the
        // ones on players' screens — and a bot flies through a door every player sees shut.
        //
        // The seed's moment is on the wall clock; the sim's tick for it comes from the same mapping as
        // real-player packets (see DrainPositionInputs): `now` is sim tick CurrentTick + TickDebt. The sim
        // takes the seeds as a command stamped with that tick, like every other fact it is fed: every
        // rollback lane applies them at the same tick, and if the sim has already gone past it (it always
        // has, by a pump or so) it rewinds and re-rolls the doors from there. A world built part-way through
        // the zone's seed minute is further behind than it can rewind; it catches the door schedule up
        // instead, and lands on the doors the players already see.
        //
        // The same seeds are handed over a second time when the sim has had to write ticks off (see
        // ArenaTimer_Pump). Writing off W ticks moves every wall-clock moment W ticks earlier on the sim's
        // clock, the seed's included, so the sim's doors would otherwise switch W ticks late until the next
        // seed came. That second hand-over has to go in as a catch-up rather than a rewind — a rewind would
        // replay the first hand-over too, at its old tick, straight after and undo it — so it waits until
        // the seed's tick is further back than the sim can rewind, which is at most the rollback horizon.
        // If catching up shuts a door on a bot, the sim throws the bot out of it (a WarpFlash, handled in
        // ProcessEvents like any other).
        // Runs on the arena thread (inside the pump).
        private void SyncSeeds(ArenaData ad, Arena arena, ReplayController world, ServerTick now)
        {
            ZoneSeeds? seeds = _seeds;
            if (seeds is null)
                return;

            int age = now - seeds.Timestamp; // ticks since the seeds took effect
            long seedTick = Math.Max(0, (long)world.CurrentTick + ad.TickDebt - age);

            bool isNew = !ReferenceEquals(seeds, ad.SeedsApplied);
            if (!isNew)
            {
                bool slipped = ad.SkippedTotal != ad.SeedsAppliedAtSkipped;
                if (!slipped || world.CurrentTick - seedTick <= world.MaxDelay)
                    return;
            }

            // Refused only when the sim's command buffer is full — the pump's health check says so, and
            // this is tried again next pump.
            if (!world.SetSeeds(unchecked((int)seeds.Green), unchecked((int)seeds.Door), (uint)seedTick))
                return;

            ad.SeedsApplied = seeds;
            ad.SeedsAppliedAtSkipped = ad.SkippedTotal;

            _logManager.LogA(LogLevel.Drivel, nameof(BotsModule), arena,
                $"Bot sim given the zone's seeds{(isNew ? "" : " again (the sim had skipped ticks)")}: green={seeds.Green:X}, door={seeds.Door:X}, timestamp={(uint)seeds.Timestamp:X} ({age} ticks ago).");
        }

        // Hands the sim the bricks the server's brick module has placed since the last pump, and lets go of the
        // ones that have expired. Runs on the arena thread (inside the pump).
        private void DrainBrickInputs(ArenaData ad, Arena arena, ReplayController world, ServerTick now)
        {
            uint simNow = world.CurrentTick;

            // Same clock mapping as real-player packets (see DrainPositionInputs): the wall-clock moment `now`
            // is sim tick simNow + TickDebt, so a brick stamped `age` ticks ago was laid at that minus age.
            long simTickOfNow = (long)simNow + ad.TickDebt;
            long oldestTick = (long)simNow - world.MaxDelay + 1; // the deepest lane can still take this tick
            int brickTime = world.Canonical.GameSettings.arenas.BrickTime;
            int fed = 0;

            while (ad.BrickInputs.TryDequeue(out BrickData brick))
            {
                // The brick module stamps each brick with the server time it was laid (clients start it from
                // that same stamp). It is usually a tick or two old by now; it can also be a tick or two in the
                // FUTURE (the module nudges the stamp forward to keep them unique), which simply queues it.
                int age = now - (ServerTick)brick.StartTime;
                long laidTick = simTickOfNow - age;

                // A brick laid longer ago than the sim can rewind to — in practice one that was already
                // standing when this arena's sim was created — goes in at the oldest tick there is, and the
                // command says how much of its life is already gone so it expires with the real one.
                long applyTick = Math.Max(laidTick, oldestTick);
                int alreadyOld = (int)(applyTick - laidTick);
                if (alreadyOld > brickTime)
                    continue; // it has already expired everywhere

                if (ad.ActiveBricks.Count >= world.Canonical.MaxBricks - 2)
                {
                    // The sim's brick table is full. More bricks standing at once than a client will show.
                    if (!ad.WarnedBricksFull)
                    {
                        ad.WarnedBricksFull = true;
                        _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena,
                            $"The bot sim's brick table is full ({ad.ActiveBricks.Count} bricks); further bricks are not being simulated, so bots will not collide with them.");
                    }
                    continue;
                }

                int externalId = PhysicsAdapter.BrickExternalId(in brick);
                int slot = world.GetOrAllocateBrickSlot(new ID(externalId));
                world.EnqueueCommand(PhysicsAdapter.BrickAddCommand(slot, (uint)applyTick, in brick, alreadyOld));

                // Bots plan around it straight away, with its real extent and its real expiry. (Fed from here
                // rather than from the sim's BrickPlaced event: a real player's brick arrives late, is applied
                // inside a rollback rebuild, and the sim does not re-announce what happens in there.)
                uint expireTick = (uint)(laidTick + brickTime + 1);
                ad.ActiveBricks.Add((externalId, expireTick));
                ad.Nav?.AddBrick(new TileCoord(brick.X1, brick.Y1), new TileCoord(brick.X2, brick.Y2), expireTick);
                fed++;
            }

            if (fed > 0)
                _logManager.LogA(LogLevel.Drivel, nameof(BotsModule), arena,
                    $"Gave the bot sim {fed} brick(s); it now tracks {ad.ActiveBricks.Count}.");

            // Let go of bricks that have expired. The sim removes the brick itself when its time is up; what
            // has to be released is the ID → slot mapping, or the allocator runs out of slots. (The controller
            // holds a released mapping for one more horizon, until no lane can still be asked about it.)
            for (int i = ad.ActiveBricks.Count - 1; i >= 0; i--)
            {
                if ((int)(simNow - ad.ActiveBricks[i].ExpireTick) <= 0)
                    continue;

                world.ReleaseBrickSlot(new ID(ad.ActiveBricks[i].ExternalId));
                ad.ActiveBricks[i] = ad.ActiveBricks[^1];
                ad.ActiveBricks.RemoveAt(ad.ActiveBricks.Count - 1);
                ad.WarnedBricksFull = false;
            }
        }

        // Tells the sim the new flag count of every bot the server's flag game reported a change for since the
        // last pump. As a command, so that it is part of the timeline every rollback rebuild replays.
        private void DrainFlagChanges(ArenaData ad, ReplayController world)
        {
            while (ad.FlagChanges.TryDequeue(out Player? player))
            {
                if (ad.CarryFlags is null)
                    continue;

                Bot? bot = FindBot(ad, player.Id);
                if (bot is null)
                    continue; // not one of ours, or already gone

                world.EnqueueCommand(PhysicsAdapter.FlagCountCommand(
                    bot.ShipSlot, world.CurrentTick + 1, ad.CarryFlags.GetFlagCount(player)));
            }
        }

        // Drains the mainloop's position feed into the sim. Runs on the arena thread (inside the pump), so
        // it is the correct place for the GetOrAllocateShipSlot / EnqueueCommand that touch the world; the
        // callback that produced these entries cannot do that from its own (possibly different) thread.
        private void DrainPositionInputs(ArenaData ad, ReplayController world, ServerTick now)
        {
            uint simNow = world.CurrentTick;

            // The sim clock follows the wall clock, trailing it by TickDebt ticks at this instant (the pump is
            // about to repay them). So the wall-clock moment `now` corresponds to sim tick simNow + TickDebt,
            // and a moment `age` ticks ago corresponds to simNow + TickDebt - age.
            long simTickOfNow = (long)simNow + ad.TickDebt;
            long oldestTick = (long)simNow - world.MaxDelay + 1; // the deepest lane can still take this tick

            while (ad.PositionInputs.TryDequeue(out PositionInput input))
            {
                // Rewind: the packet describes where the player was at packet.Time (a ServerTick synced to the
                // server clock), which is in the past by its network latency. Stamp the command at the SIM
                // tick that moment corresponds to. If the sim has not run that tick yet (a low-latency packet,
                // newer than the sim's current tick) it is simply queued and applied in order — no rewind. If
                // the sim is already past it, the command lands in the past and the controller rolls the
                // affected lanes forward, resolving the player's shots/position against where they ACTUALLY
                // were when they acted. `age` is in 10 ms ticks (= sim ticks) and is measured against the same
                // `now` the sim clock was just advanced to, so queue delay is accounted for.
                int age = now - input.Packet.Time;
                if (age < 0)
                    age = 0; // packet timestamped ahead of us (clock skew) — treat as current

                // How far behind the sim's CURRENT tick the command lands: what the rollback lanes see. Zero
                // or less means the sim has not reached it yet, i.e. it is on time.
                long target = simTickOfNow - age;
                int late = (int)Math.Max(0, (long)simNow - target);
                if (late > ad.MaxStaleTicks)
                    ad.MaxStaleTicks = late; // DIAG (temporary): for the REWIND line

                // Lateness histogram (for Bots:RewindLanes tuning): fixed StatsBucketTicks-wide slices from 0,
                // last bucket = overflow (later than the horizon). Fixed slices show the true distribution
                // SHAPE, so an op can place lanes on the data; the report turns it into a cumulative % (CDF),
                // which answers "a lane at N ticks would catch X% of packets". Reset by ReportStaleHistogram.
                int bucket = late / (int)ad.StatsBucketTicks;
                if (bucket >= ad.StaleBuckets.Length)
                    bucket = ad.StaleBuckets.Length - 1; // overflow (beyond the horizon)
                ad.StaleBuckets[bucket]++;

                if (target < oldestTick)
                    target = oldestTick; // older than the deepest lane — best-effort rewind to the horizon
                uint tick = (uint)target;

                int externalId = input.Player.Id;
                int slot = world.GetOrAllocateShipSlot(new ID(externalId));

                if (ad.AddedShipIds.Add(externalId))
                {
                    world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, tick, externalId, input.Ship, input.Freq, in input.Packet));
                    ad.PlayerById[externalId] = input.Player;
                }

                // Authoritative position override from the real client.
                world.EnqueueCommand(PhysicsAdapter.ShipPositionCommand(slot, tick, in input.Packet));

                // Feed their weapon fire so the engine can resolve hits on bots (physics owns damage). The
                // engine spawns the projectile from the command's position/rotation/velocity, so pass the
                // whole packet — those fields describe where the shot originated.
                if (input.Packet.Weapon.Type != WeaponCodes.Null)
                    world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(slot, tick, in input.Packet));
            }
        }

        // Physics is authoritative for weapons/damage: translate its combat events into server actions.
        // PlayerDied now carries the killer in SecondaryId (all sources: bullets, bombs, shrap, collisions),
        // so attribution is a direct lookup. WeaponFired tells us the exact weapon the sim spawned, which we
        // mirror to clients as the visual.
        private void ProcessEvents(ArenaData ad, Arena arena, ReplayController world)
        {
            PhysicsEventCollector? collector = ad.EventCollector;
            if (collector is null)
                return;

            ad.GoalsThisTick.Clear();

            foreach (EventSink.Entry e in collector.Events)
            {
                switch (e.Kind)
                {
                    case EventKind.WeaponFired:
                        // Remember what the sim actually spawned so the visual matches (type + level,
                        // post-promotion/upgrade/flagger-boost). PrimaryId is the firer.
                        Bot? shooter = FindBot(ad, e.PrimaryId.Int);
                        if (shooter is not null)
                        {
                            shooter.FiredThisTick = true;
                            shooter.LastFiredType = (WeaponCodes)e.IntA;
                            shooter.LastFiredLevel = (byte)e.IntB;
                        }
                        break;

                    case EventKind.RepelFired:
                        // A repel has no projectile: the engine applies the shove as an instant side effect
                        // and announces it with RepelFired, NOT WeaponFired. Mirror it to the visual here or
                        // Continuum renders the push with no repel burst. PrimaryId is the firer; the repel
                        // weapon field carries no level. (See RepelFunctions.Apply / EventKind.RepelFired.)
                        Bot? repeller = FindBot(ad, e.PrimaryId.Int);
                        if (repeller is not null)
                        {
                            repeller.FiredThisTick = true;
                            repeller.LastFiredType = WeaponCodes.Repel;
                            repeller.LastFiredLevel = 0;
                        }
                        break;

                    case EventKind.PlayerDied:
                        HandleDeath(ad, arena, e.PrimaryId.Int, e.SecondaryId.Int);
                        break;

                    case EventKind.WarpFlash:
                        // The sim put a ship somewhere else in one step: a door closed on it (or a brick was
                        // laid on it) and it was thrown to a random spot, or it respawned, or took a wormhole.
                        // For a bot, the route it was following starts from where it no longer is — drop it,
                        // so the next Think plans from where it is — and its next position packet has to say
                        // it warped (SendBot), as a real client's would.
                        Bot? warped = FindBot(ad, e.PrimaryId.Int);
                        if (warped is not null)
                        {
                            warped.Nav.Invalidate();
                            warped.WarpFlashPending = true;
                        }
                        break;

                    case EventKind.GoalScored:
                        // One goal per (ball, scorer) per heartbeat. The ball sits on the goal tile and
                        // re-emits GoalScored on every remaining sub-tick until ReconcileBallToSim removes it,
                        // which would otherwise credit the same goal ~PumpTicks times. Keyed by ball id so an
                        // arena with multiple balls still scores each independently.
                        long goalKey = ((long)e.PrimaryId.Int << 32) | (uint)e.SecondaryId.Int;
                        if (ad.GoalsThisTick.Add(goalKey))
                            HandleGoalScored(ad, arena, world, in e);
                        break;

                    case EventKind.BallFired:
                        // DIAG (temporary): pinpoint the ejection-location problem. Logs the ball's position
                        // (px, post-heartbeat) and velocity, and the firing ship id — so we can see whether
                        // the ball is far from the ship (mirror/sub-tick lag) or the velocity is oversized.
                        if (ad.SimBallSlot >= 0 && (uint)ad.SimBallSlot < (uint)world.Canonical.MaxBalls
                            && world.Canonical.BallSlotInUse[ad.SimBallSlot])
                        {
                            ref readonly SimBall bf = ref world.Canonical.Balls[ad.SimBallSlot];
                            // vel = sim ball velocity (Coordinates1000/tick, mirrored 1:1 to the wire as the
                            // ship-velocity unit). friction = the ball's per-tick decel model. If the ball
                            // travels ~2x a client's, compare vel magnitude to your Soccer:BallSpeed and the
                            // friction to Soccer:BallFriction — that says physics/settings vs a wiring scale.
                            _logManager.LogM(LogLevel.Info, nameof(BotsModule),
                                $"BALLFIRE by={e.SecondaryId.Int} ballPx=({bf.Coordinates1000.X / 1000},{bf.Coordinates1000.Y / 1000}) vel=({bf.Velocity.X},{bf.Velocity.Y}) friction={bf.BallFriction} maxVel={bf.BallMaxVelocity}");
                        }
                        break;

                    case EventKind.WeaponHit:
                        // DIAG (temporary): a weapon damaged a ship. PrimaryId=victim, SecondaryId=firer,
                        // IntA=WeaponTypes, IntB=damage in 1/1000 energy. The sim now emits this for BOMB-BLAST
                        // victims too (not just bullets), so a prox bomb that actually hurts a ship in our
                        // authoritative sim shows up here — if the bomb "eats" visually but no HIT line names
                        // the nearby ship, our blast didn't include it.
                        _logManager.LogM(LogLevel.Info, nameof(BotsModule),
                            $"HIT victim={e.PrimaryId.Int} firer={e.SecondaryId.Int} wpn={(WeaponTypes)e.IntA} dmg1000={e.IntB}");
                        break;

                    case EventKind.BombExploded:
                        // DIAG (temporary): a bomb detonated. NOTE the payload differs from WeaponHit —
                        // PrimaryId=firer, SecondaryId=bombId, IntA=weaponLevel, IntB=shrapCount. This only
                        // marks THAT a detonation happened and by whom; per-victim damage comes through the
                        // WeaponHit events above.
                        _logManager.LogM(LogLevel.Info, nameof(BotsModule),
                            $"BOMB detonated firer={e.PrimaryId.Int} bomb={e.SecondaryId.Int} level={e.IntA} shrap={e.IntB}");
                        break;

                    // (EventKind.BrickPlaced is deliberately not handled: bricks reach the nav overlay from
                    // DrainBrickInputs, with their full extent and real expiry, at the moment the server's brick
                    // module reports them — which also covers bricks that land inside a rollback rebuild.)

                    case EventKind.CanonicalRebuilt:
                        // DIAG (temporary): a late real-player command rewound the snapshot lanes (the rollback
                        // firing). Just COUNT them here — the honest rewind depth is the packet staleness tracked
                        // in DrainPositionInputs (MaxStaleTicks), NOT currentTick - fromTick, which overcounts by
                        // ~PumpTicks because currentTick has already advanced a full pump past the drain.
                        ad.RebuildCount++;
                        break;

                    case EventKind.WeaponAdded:
                    case EventKind.WeaponRemoved:
                    case EventKind.WeaponAdjusted:
                        // DIAG (temporary): what a rollback did to a weapon that was in flight. Counted for the
                        // REWIND line, split by whether a bot or a real player fired it (PrimaryId = the firer).
                        bool botFired = FindBot(ad, e.PrimaryId.Int) is not null;
                        int kindIndex = e.Kind == EventKind.WeaponAdded ? 0 : e.Kind == EventKind.WeaponRemoved ? 1 : 2;
                        ad.WeaponCorrections[kindIndex + (botFired ? 3 : 0)]++;
                        break;

                    case EventKind.PendingCommandsOverflow:
                        // The sim refused a command — its command buffer is full (Bots:MaxCommandsPerTick).
                        // Counted here, reported once a second by the pump's health check.
                        ad.RefusedCommands++;
                        break;

                    case EventKind.LanesRetuned:
                        // The controller re-seated its intermediate lanes to fit the lag it has been seeing
                        // (IntA = rebuilds it judged by). Keep our copy current — the staleness report prints
                        // it — and tell the op where the lanes went.
                        string lanesBefore = string.Join(",", ad.Lanes);
                        if (ad.Lanes.Length == world.SnapshotCount)
                            world.CopyLaneDelays(ad.Lanes);
                        _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                            $"Rollback lanes re-seated to [{string.Join(",", ad.Lanes)}] ticks (were [{lanesBefore}]), from {e.IntA} rewinds observed.");
                        break;
                }
            }

            collector.Clear();
        }

        private static Bot? FindBot(ArenaData ad, int playerId)
        {
            foreach (Bot b in ad.Bots)
                if (b.Player.Id == playerId)
                    return b;
            return null;
        }

        // victimId died; attackerId is the killer the engine stamped on PlayerDied (== victimId for a
        // suicide/environmental death, or an unresolved id that just falls through to self-attribution).
        private void HandleDeath(ArenaData ad, Arena arena, int victimId, int attackerId)
        {
            if (!ad.PlayerById.TryGetValue(victimId, out Player? victim))
                return;

            // Only DRIVE deaths for bots. A real client is authoritative over its own death (it dies from
            // its own client-side damage model); FakeKill-ing a real player from the sim would kill them
            // out from under their client. See the PvP-vs-humans TODO below.
            if (victim.Type != ClientType.Fake)
                return;

            Player? killer = null;
            if (attackerId != victimId)
                ad.PlayerById.TryGetValue(attackerId, out killer);

            // Flags the victim carries transfer to the killer (the server flag game is authoritative for
            // flags — FakeKill only fills the kill packet, it does NOT move flags). Get the count for the
            // packet, send it, THEN perform the transfer, since the transfer can end the flag game / send a
            // FlagReset and must follow the kill packet.
            short flagCount = 0;
            if (ad.CarryFlags is not null && killer is not null)
                flagCount = ad.CarryFlags.GetPlayerKillTransferCount(arena, victim, killer);

            // DIAG (temporary): confirm attribution + flag transfer.
            _logManager.LogM(LogLevel.Info, nameof(BotsModule),
                $"DEATH {victim.Name} killed by {(killer?.Name ?? "<self/env>")} flags={flagCount}");

            // The engine decided this ship died; tell the server/clients so scoring and the normal
            // death/respawn flow happen. FakeKill needs a killer — self-attribute when the death was
            // environmental / unattributed (attackerId == victim or an id we don't track).
            _game.FakeKill(killer ?? victim, victim, 0, flagCount);

            if (ad.CarryFlags is not null && killer is not null && flagCount > 0)
                ad.CarryFlags.TransferFlagsForPlayerKill(arena, victim, killer);

            // TODO(respawn): re-add the bot's ship to the sim at a spawn point after its death delay.
            // TODO(real players): a real client is normally authoritative over its own death. Driving
            // a real player's death purely from the server needs energy + death sync to feel right;
            // decide the policy (server-authoritative combat vs. advisory) before enabling PvP damage
            // against humans.
        }

        // Builds a bot's Mode-3 ball view. POSSESSION comes from the SERVER's authoritative ball (bd.Carrier),
        // so a bot's decisions never lag the sim's optimistic prediction. A loose ball's POSITION comes from the
        // sim (its flight, which matches the client's dead-reckoning); a carried ball sits on its carrier.
        // Positions are Coordinates1000, matching sim ship state.
        private static BallView BuildBallView(Bot bot, ReplayController world, int simSlot, in BallData server)
        {
            WorldState w = world.Canonical;
            ref readonly ShipState s = ref w.Ships[bot.ShipSlot];
            ushort myFreq = s.Frequency;

            BallPossession owner;
            int carrierId = 0;
            ushort carrierFreq = 0;
            int holdTicksLeft = 0;
            Point ballPos;
            Point ballVel = default;

            if (server.State == BallState.Carried && server.Carrier is not null)
            {
                carrierId = server.Carrier.Id;
                carrierFreq = (ushort)server.Freq;
                owner = server.Carrier == bot.Player ? BallPossession.Mine
                      : carrierFreq == myFreq ? BallPossession.Teammate
                      : BallPossession.Enemy;
                ballPos = new Point(server.X * 1000, server.Y * 1000);

                // Carry timer is a sim-side game-rule prediction — read it only when the sim agrees we hold it.
                if (owner == BallPossession.Mine
                    && simSlot >= 0 && (uint)simSlot < (uint)w.MaxBalls && w.BallSlotInUse[simSlot]
                    && w.TryGetBallCarrierSlot(simSlot, out int cs) && cs == bot.ShipSlot)
                {
                    holdTicksLeft = w.Ships[bot.ShipSlot].BallCarryTimeLeft;
                }
            }
            else if (server.State == BallState.OnMap)
            {
                owner = BallPossession.Loose;

                // Loose position/velocity from the sim's flight; fall back to the server's fire event if the
                // sim ball isn't tracked/loose yet (e.g. the tick it was just seeded).
                if (simSlot >= 0 && (uint)simSlot < (uint)w.MaxBalls && w.BallSlotInUse[simSlot]
                    && !w.TryGetBallCarrierSlot(simSlot, out _))
                {
                    ref readonly SimBall ball = ref w.Balls[simSlot];
                    ballPos = ball.Coordinates1000;
                    ballVel = ball.Velocity;
                }
                else
                {
                    ballPos = new Point(server.X * 1000, server.Y * 1000);
                    ballVel = new Point(server.XSpeed, server.YSpeed);
                }
            }
            else
            {
                return default; // not in play
            }

            // Offence: nearest goal THIS bot can score in.
            bool hasGoal = world.TryGetNearestGoal(s.Coordinates1000, myFreq, out Point goal);

            // Defence: when an enemy carries, the goal THAT enemy scores in — the one we guard.
            bool hasDefend = false;
            Point defendGoal = default;
            if (owner == BallPossession.Enemy)
                hasDefend = world.TryGetNearestGoal(ballPos, carrierFreq, out defendGoal);

            // Last toucher = the ball's current/most-recent carrier (server.Carrier persists on a loose ball as
            // its last shooter), so a bot can tell "this is my own live shot, don't chase it yet".
            int lastToucherId = server.Carrier?.Id ?? 0;

            return new BallView(true, owner, ballPos, ballVel, carrierId, hasGoal, goal, hasDefend, defendGoal, holdTicksLeft, lastToucherId);
        }

        // Forces the sim ball to the server's authoritative state (the server owns ownership; the sim only
        // predicts). Seeds the sim ball on first use / after a respawn, corrects a mispredicted carrier, and
        // starts a loose flight when the server says the ball was fired. A loose ball both already agree on is
        // left alone so the sim keeps simulating its flight — we do NOT reset a loose ball's position each tick.
        private void ReconcileBallToSim(ArenaData ad, Arena arena, ReplayController world)
        {
            if (ad.Balls is null || !ad.Balls.TryGetBallData(arena, 0, out BallData server))
                return;

            WorldState w = world.Canonical;
            int slot = world.GetOrAllocateBallSlot(new ID(SimBallExternalId));
            ad.SimBallSlot = slot;
            uint applyTick = world.CurrentTick + 1;

            bool simActive = (uint)slot < (uint)w.MaxBalls && w.BallSlotInUse[slot];
            bool simHeld = simActive && w.TryGetBallCarrierSlot(slot, out _);

            switch (server.State)
            {
                case BallState.Carried:
                    // Desired: held by the server's carrier. Sync unless the sim already holds it for that same
                    // player (this also attaches a ball a bot's pickup wasn't granted to the real winner).
                    if (server.Carrier is not null)
                    {
                        bool alreadyRight = simHeld
                            && w.TryGetBallCarrierSlot(slot, out int cs)
                            && w.Ships[cs].ID.Int == server.Carrier.Id;
                        if (!alreadyRight)
                        {
                            world.SyncBallState(new ID(SimBallExternalId), held: true, new ID(server.Carrier.Id),
                                default, default, (ushort)server.Freq, 0, applyTick);
                        }
                    }
                    break;

                case BallState.OnMap:
                    // Desired: loose. Only sync when the sim isn't already loose-and-active (it still predicts a
                    // hold, or has no ball yet) — otherwise leave the sim's flight untouched. Position = the
                    // server's fire origin (correct for a fresh fire; a stale loose ball is the skip case).
                    if (!(simActive && !simHeld))
                    {
                        Point pos = new(server.X * 1000, server.Y * 1000);
                        Point vel = new(server.XSpeed, server.YSpeed);
                        int ticksSinceFired = (int)(ServerTick.Now - server.Time);
                        ID lastHolder = server.Carrier is not null ? new ID(server.Carrier.Id) : ID.InvalidID;
                        world.SyncBallState(new ID(SimBallExternalId), held: false, lastHolder,
                            pos, vel, (ushort)server.Freq, ticksSinceFired, applyTick);
                    }
                    break;

                default: // Waiting / None — not in play. Drop the sim ball if it lingers (e.g. after a goal).
                    if (simActive)
                        world.EnqueueCommand(PhysicsAdapter.BallRemoveCommand(slot, applyTick));
                    break;
            }
        }

        // Keep the server (authoritative) and the sim (predictor) in agreement on ball ownership, then correct
        // the sim. STATE-based, not transition-based: transition detection missed a sim drop-and-re-grab within
        // one tick (no net carrier change) so the server never learned the bot held it — a persistent desync.
        // Here we compare the current states every call: if the sim predicts a bot holds a ball the server
        // hasn't granted, request the pickup; if the sim released a ball the server still has a bot holding,
        // request the shot; then SyncBallState the sim to the server's ruling (the "undo").
        private void SyncBallOwnership(ArenaData ad, Arena arena, ReplayController world)
        {
            if (ad.Balls is null || !ad.Balls.TryGetBallData(arena, 0, out BallData server))
                return;

            WorldState w = world.Canonical;
            int simCarrierShip = SimCarrierShipSlot(ad, world);

            if (simCarrierShip >= 0)
            {
                // Sim predicts a ship holds the ball. If it's one of our bots and the server hasn't granted the
                // ball to that bot, request the pickup (the server arbitrates; a rejection is undone below).
                Bot? bot = FindBotByShipSlot(ad, simCarrierShip);
                if (bot is not null && !(server.State == BallState.Carried && server.Carrier == bot.Player))
                    ad.Balls.TryPickupBall(arena, 0, bot.Player);
            }
            else if (server.State == BallState.Carried && server.Carrier is { Type: ClientType.Fake } carrier
                     && (uint)ad.SimBallSlot < (uint)w.MaxBalls && w.BallSlotInUse[ad.SimBallSlot])
            {
                // Sim shows the ball loose but the server still has a BOT holding it — the sim released it (kick
                // or carry-timer auto-throw). Tell the server, with the sim ball's fire position/velocity. Only
                // for the server's own carrier, so DoShootBall's "was carrying" check always passes.
                ref readonly SimBall ball = ref w.Balls[ad.SimBallSlot];
                ad.Balls.TryShootBall(arena, 0, carrier,
                    (short)(ball.Coordinates1000.X / 1000), (short)(ball.Coordinates1000.Y / 1000),
                    (short)ball.Velocity.X, (short)ball.Velocity.Y, ServerTick.Now);
            }

            ReconcileBallToSim(ad, arena, world);
        }

        // The ship slot carrying the sim ball, or -1 when the ball is loose / absent.
        private static int SimCarrierShipSlot(ArenaData ad, ReplayController world)
        {
            WorldState w = world.Canonical;
            return ad.SimBallSlot >= 0 && (uint)ad.SimBallSlot < (uint)w.MaxBalls
                && w.BallSlotInUse[ad.SimBallSlot] && w.TryGetBallCarrierSlot(ad.SimBallSlot, out int shipSlot)
                ? shipSlot : -1;
        }

        private static Bot? FindBotByShipSlot(ArenaData ad, int shipSlot)
        {
            foreach (Bot b in ad.Bots)
                if (b.ShipSlot == shipSlot)
                    return b;
            return null;
        }

        // Kicks the ball a bot carries toward its BallTarget (a goal), at the ship's SoccerBallSpeed setting.
        private void TryBotKick(ArenaData ad, ReplayController world, Bot bot, uint cmdTick, Point target)
        {
            WorldState w = world.Canonical;
            if (!w.TryGetBallCarrierSlot(ad.SimBallSlot, out int carrierSlot) || carrierSlot != bot.ShipSlot)
                return; // only the actual carrier can kick

            ref readonly ShipState s = ref w.Ships[bot.ShipSlot];
            double dx = target.X - s.Coordinates1000.X;
            double dy = target.Y - s.Coordinates1000.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double speed = s.ShipSettings.SoccerBallSpeed;
            double vx = s.Velocity.X, vy = s.Velocity.Y;

            // Lead the shot so the ball flies STRAIGHT at the target despite the ship's drift. The fire adds a
            // kick of magnitude SoccerBallSpeed to the ship's velocity; picking the kick direction so the
            // resultant (shipVel + kick) points at the target means solving |t*u - shipVel| = speed for t>0
            // (u = unit toward target). The ball then travels to the goal at speed t. If the ship is moving too
            // fast to fully compensate (disc < 0), fall back to a plain forward kick and accept some skew.
            Point vel;
            if (len <= 0)
            {
                vel = s.Velocity;
            }
            else
            {
                double ux = dx / len, uy = dy / len;
                double dDotV = ux * vx + uy * vy;
                double disc = dDotV * dDotV - (vx * vx + vy * vy) + speed * speed;
                double t = disc >= 0 ? dDotV + Math.Sqrt(disc) : -1;
                vel = t > 0
                    ? new Point((int)(ux * t), (int)(uy * t))
                    : new Point((int)(vx + ux * speed), (int)(vy + uy * speed));
            }
            world.EnqueueCommand(PhysicsAdapter.BallFireCommand(bot.ShipSlot, ad.SimBallSlot, cmdTick, s.Coordinates1000, vel, (short)s.Frequency));
        }

        // The sim detected the ball entering a goal. Credit the goal server-side (advisors + scoring callback
        // + respawn) via IBalls.TryReportGoal — but only for BOT scorers; a real player's goal is reported by
        // their own client. Then reset the sim ball so it doesn't sit on the goal re-emitting GoalScored each
        // tick (the library deliberately leaves it there for the host to remove).
        private void HandleGoalScored(ArenaData ad, Arena arena, ReplayController world, in EventSink.Entry e)
        {
            // Payload: SecondaryId = scorer, IntB = goal tile packed as (tileY << 10) | tileX.
            if (ad.Balls is not null
                && ad.PlayerById.TryGetValue(e.SecondaryId.Int, out Player? scorer)
                && scorer.Type == ClientType.Fake)
            {
                TileCoordinates goalTile = new((short)(e.IntB & 1023), (short)(e.IntB >> 10));
                ad.Balls.TryReportGoal(arena, 0, scorer, goalTile);
            }

            // Remove the goal-sitting sim ball so it stops re-emitting GoalScored; ReconcileBallToSim then
            // re-seeds (or clears) from the server's respawned/phasing ball.
            if (ad.SimBallSlot >= 0 && (uint)ad.SimBallSlot < (uint)world.Canonical.MaxBalls
                && world.Canonical.BallSlotInUse[ad.SimBallSlot])
            {
                world.EnqueueCommand(PhysicsAdapter.BallRemoveCommand(ad.SimBallSlot, world.CurrentTick + 1));
            }
        }

        #endregion

        #region Commands

        // Builds the arena's physics world (and nav) on first use. Deferred out of AttachModule so the
        // arena's client settings are loaded by the time we read them; otherwise the sim gets all-zero
        // ship settings and bots spawn frozen. Runs on the ARENA thread (from the spawn work items), which
        // is the single writer of ad.World/Nav/etc., so it needs no locks — the tick cannot run concurrently
        // with it. The settings/map reads it makes are thread-safe (no mainloop assertion).
        private void EnsureWorld(Arena arena, ArenaData ad)
        {
            if (ad.World is not null)
                return;

            ServerTick createdAt = ServerTick.Now;
            ad.World = _worldProvider.CreateWorld(arena, createdAt, ad.Lanes, ad.MaxCommandsPerTick);
            if (ad.World is not null)
            {
                // Start the sim clock: the world's tick corresponds to this wall-clock moment, with nothing owed.
                ad.LastPumpTime = createdAt;
                ad.TickDebt = 0;

                ad.EventCollector = new PhysicsEventCollector();
                ad.World.SetEventListener(ad.EventCollector);

                // DIAG (temporary): have the sim say what each rollback did to the weapons in flight (added /
                // removed / adjusted), so the REWIND line can count it. The server itself has no use for
                // these yet: there is no packet that takes a weapon back or moves one on a client, and a QS
                // client running the same sim raises them for itself. Remove with the rest of the REWIND diag.
                ad.World.EmitWeaponCorrectionEvents = true;

                // Let the controller re-seat its intermediate lanes from the lag it observes (unless an op
                // pinned them with Bots:RewindLanes, or turned re-seating off).
                bool retuning = ad.AutoTuneLanes && ad.World.EnableLaneAutoTune(in ad.LaneTune);
                _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                    $"Rollback lanes start at [{string.Join(",", ad.Lanes)}] ticks; " +
                    (retuning ? $"re-seated every {ad.LaneRetuneMinutes} min from observed lag." : "fixed (no re-seating)."));
                GridNavigation navGrid = GridNavigation.Build(ad.World.Canonical.Level, ShipRadiusTiles);
                navGrid.MaxSearchExpansions = ad.NavSearchLimit;
                ad.Nav = navGrid;

                // DIAG (temporary): if BlockedTiles is ~0, the nav grid has no walls (map tiles didn't reach
                // it) — which would make bots navigate straight through everything and pin on physical walls.
                NavStats ns = ad.Nav.Stats;
                _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                    $"NAV built: regions={ns.RegionCount} portals={ns.PortalCount} blockedTiles={ns.BlockedTiles} hasTileData={ad.World.Canonical.Level.HasTileData}");

                // Optional: the arena's carry-flag game and ball game (null if not enabled here). Held for
                // the arena's lifetime and released in DetachModule.
                ad.CarryFlags = arena.GetInterface<ICarryFlagGame>();
                ad.Balls = arena.GetInterface<IBalls>();
            }
        }

        private void Command_spawnbot(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            // Usage: ?spawnbot [orbit|straight|combat|ball [easy|medium|hard]] [freq] [name]. Optional brain
            // selector (then a difficulty for combat/ball), then an optional numeric freq (default 0), then
            // the name. "straight" = wall-collision probe; "combat" = Mode 4 fighter; "ball" = Mode 3 soccer
            // (retrieve/carry/score/defend). Spawn bots on different freqs to have them play/fight.
            ReadOnlySpan<char> parms = parameters.Trim();

            IBotBrain brain = new OrbitBrain();
            string brainLabel = "orbit";
            ReadOnlySpan<char> token = PeekToken(parms);
            if (token.Equals("straight", StringComparison.OrdinalIgnoreCase))
            {
                brain = new StraightBrain();
                brainLabel = "straight";
                parms = PopToken(parms);
            }
            else if (token.Equals("combat", StringComparison.OrdinalIgnoreCase))
            {
                parms = PopToken(parms);

                // Optional difficulty: easy | medium | hard (default medium).
                ReadOnlySpan<char> diff = PeekToken(parms);
                BotProfile profile = BotProfile.Medium;
                if (diff.Equals("easy", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Easy; parms = PopToken(parms); }
                else if (diff.Equals("medium", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Medium; parms = PopToken(parms); }
                else if (diff.Equals("hard", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Hard; parms = PopToken(parms); }

                brain = new CombatBrain(profile);
                brainLabel = $"combat/{profile.Name}";
            }
            else if (token.Equals("ball", StringComparison.OrdinalIgnoreCase))
            {
                parms = PopToken(parms);

                // Optional difficulty: easy | medium | hard (default medium).
                ReadOnlySpan<char> diff = PeekToken(parms);
                BotProfile profile = BotProfile.Medium;
                if (diff.Equals("easy", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Easy; parms = PopToken(parms); }
                else if (diff.Equals("medium", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Medium; parms = PopToken(parms); }
                else if (diff.Equals("hard", StringComparison.OrdinalIgnoreCase)) { profile = BotProfile.Hard; parms = PopToken(parms); }

                brain = new BallBrain(profile);
                brainLabel = $"ball/{profile.Name}";
            }
            else if (token.Equals("orbit", StringComparison.OrdinalIgnoreCase))
            {
                parms = PopToken(parms);
            }

            // Optional numeric freq (0-9999) as the next token; otherwise default 0.
            short freq = 0;
            if (short.TryParse(PeekToken(parms), out short parsedFreq) && parsedFreq >= 0 && parsedFreq <= 9999)
            {
                freq = parsedFreq;
                parms = PopToken(parms);
            }

            string name = parms.Trim().ToString();
            if (name.Length == 0)
                name = "Bot";

            const ShipType ship = ShipType.Warbird;

            // Create the fake in spectator mode, then change it into a real ship via the Game module.
            // CreateFakePlayer sets player.Ship as a raw field and sends the "entering" packet (so the
            // bot appears in the player list), but it never announces a ship to clients — so without
            // the ship change below, clients render no ship. SetShipAndFreq broadcasts the S2C
            // ship-change packet that makes clients draw the ship, and it only fires on an actual
            // change, which is why we must start in spec. (This mirrors how the Replay module and real
            // players get into a ship: enter as spec, then ship-change.)
            Player? botPlayer = _fake.CreateFakePlayer(name, arena, ShipType.Spec, freq);
            if (botPlayer is null)
            {
                _chat.SendMessage(player, "Bots: failed to create fake player.");
                return;
            }

            _game.SetShipAndFreq(botPlayer, ship, freq);

            // Spawn where the commanding player is, so it's immediately visible (fallback: map centre).
            short spawnX = player.Position.X != 0 ? player.Position.X : (short)(512 * 16);
            short spawnY = player.Position.Y != 0 ? player.Position.Y : (short)(512 * 16);

            // The world and bot list are owned by the arena's thread; build the world (if needed) and add
            // the bot there. CreateFakePlayer/SetShipAndFreq above are player-lifecycle work that belongs on
            // this (command) thread; only the simulation/bot-list mutation crosses over.
            Player caller = player;
            if (!_arenaLoop.QueueArenaWorkItem(arena, _ =>
            {
                EnsureWorld(arena, ad);
                if (ad.World is null)
                {
                    _chat.SendMessage(caller, "Bots: could not build a physics world (client settings unavailable for this arena).");
                    _fake.EndFaked(botPlayer);
                    return;
                }

                ReplayController world = ad.World;
                int slot = world.GetOrAllocateShipSlot(new ID(botPlayer.Id));
                C2S_PositionPacket spawn = new() { X = spawnX, Y = spawnY };
                world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, world.CurrentTick + 1, botPlayer.Id, ship, freq, in spawn));

                ad.AddedShipIds.Add(botPlayer.Id);
                ad.PlayerById[botPlayer.Id] = botPlayer;
                ad.Bots.Add(new Bot { Player = botPlayer, ShipSlot = slot, Brain = brain });

                _chat.SendMessage(caller, $"Bots: spawned '{name}' ({brainLabel}, freq {freq}).");
            }, arena))
            {
                _chat.SendMessage(player, "Bots: could not queue the spawn on the arena's thread.");
                _fake.EndFaked(botPlayer);
            }
        }

        // First whitespace-delimited token of a parameter span (empty if none).
        private static ReadOnlySpan<char> PeekToken(ReadOnlySpan<char> s)
        {
            s = s.TrimStart();
            int sp = s.IndexOf(' ');
            return sp < 0 ? s : s[..sp];
        }

        // The remainder after the first token (empty if none).
        private static ReadOnlySpan<char> PopToken(ReadOnlySpan<char> s)
        {
            s = s.TrimStart();
            int sp = s.IndexOf(' ');
            return sp < 0 ? default : s[(sp + 1)..].TrimStart();
        }

        private void Command_killbots(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            // The bot list and sim are owned by the arena's thread; remove there.
            Player caller = player;
            if (!_arenaLoop.QueueArenaWorkItem(arena, _ =>
            {
                int count = RemoveAllBots(ad);
                _chat.SendMessage(caller, $"Bots: removed {count} bot(s).");
            }, arena))
            {
                _chat.SendMessage(player, "Bots: could not queue the removal on the arena's thread.");
            }
        }

        /// <summary>
        /// Removes every bot in an arena. Must run on the arena's thread (it mutates the sim and bot list);
        /// callers on another thread route it through <see cref="IArenaLoop.QueueArenaWorkItem"/>.
        /// </summary>
        /// <param name="ad">The arena's data.</param>
        /// <returns>How many bots were removed.</returns>
        private int RemoveAllBots(ArenaData ad)
        {
            int count = ad.Bots.Count;

            foreach (Bot bot in ad.Bots)
            {
                ad.World?.EnqueueCommand(PhysicsAdapter.ShipRemoveCommand(bot.ShipSlot, ad.World.CurrentTick + 1));
                ad.PlayerById.Remove(bot.Player.Id);
                ad.AddedShipIds.Remove(bot.Player.Id);
                _fake.EndFaked(bot.Player);
            }

            ad.Bots.Clear();

            return count;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<bots per arena> [arena count] [combat] | stop",
            Description = """
                Spawns bots to generate load for performance measurement, and reports what it did.
                Bots are spread over a wide area of each map rather than spawned on top of the caller,
                because the cost being measured depends on how many of them are in view of each other.
                Give an arena count to spread the load over that many arenas; only arenas that are
                running and have this module attached are used. Add 'combat' to have them fight
                rather than orbit, which drives the weapon and kill paths at the cost of a noisier and
                less repeatable run. Use ?loadtest stop to remove them all.
                """)]
        private void Command_loadtest(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> parms = parameters.Trim();

            if (parms.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                Command_loadtest_Stop(player);
                return;
            }

            if (!int.TryParse(PeekToken(parms), out int botsPerArena) || botsPerArena <= 0)
            {
                _chat.SendMessage(player, "Bots: usage is ?loadtest <bots per arena> [arena count] [combat], or ?loadtest stop.");
                return;
            }

            parms = PopToken(parms);

            int arenaCount = 1;
            if (int.TryParse(PeekToken(parms), out int parsedArenaCount) && parsedArenaCount > 0)
            {
                arenaCount = parsedArenaCount;
                parms = PopToken(parms);
            }

            bool combat = PeekToken(parms).Equals("combat", StringComparison.OrdinalIgnoreCase);

            // Collect the arenas to load, starting with the caller's so that a plain "?loadtest 100"
            // does the obvious thing.
            List<Arena> targetArenas = new(arenaCount);

            if (player.Arena is not null && player.Arena.TryGetExtraData(_adKey, out _))
                targetArenas.Add(player.Arena);

            if (targetArenas.Count < arenaCount)
            {
                _arenaManager.Lock();

                try
                {
                    foreach (Arena candidate in _arenaManager.Arenas)
                    {
                        if (targetArenas.Count >= arenaCount)
                            break;

                        if (candidate.Status == ArenaState.Running
                            && !targetArenas.Contains(candidate)
                            && candidate.TryGetExtraData(_adKey, out _))
                        {
                            targetArenas.Add(candidate);
                        }
                    }
                }
                finally
                {
                    _arenaManager.Unlock();
                }
            }

            if (targetArenas.Count == 0)
            {
                _chat.SendMessage(player, "Bots: no arena available. Attach this module to an arena first (Modules:AttachModules).");
                return;
            }

            int spawned = 0;
            foreach (Arena arena in targetArenas)
            {
                spawned += SpawnLoadTestBots(arena, botsPerArena, combat);
            }

            _chat.SendMessage(player, $"Bots: spawned {spawned} {(combat ? "combat" : "orbit")} bot(s) across {targetArenas.Count} arena(s).");

            int requested = botsPerArena * targetArenas.Count;
            if (spawned < requested)
                _chat.SendMessage(player, $"Bots: wanted {requested} but the simulation ran out of ship slots. Spread the load over more arenas.");

            if (targetArenas.Count < arenaCount)
                _chat.SendMessage(player, $"Bots: wanted {arenaCount} arenas but only {targetArenas.Count} had this module attached.");

            _logManager.LogM(LogLevel.Info, nameof(BotsModule), $"Load test spawned {spawned} bot(s) across {targetArenas.Count} arena(s).");
        }

        private void Command_loadtest_Stop(Player player)
        {
            // Snapshot the attached arenas under the arena-manager lock, then act on each outside it — the
            // bot removal runs on each arena's own thread, so we must not hold the arena-manager lock across
            // the wait for it.
            List<Arena> arenas = new();
            _arenaManager.Lock();
            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                    if (arena.TryGetExtraData(_adKey, out _))
                        arenas.Add(arena);
            }
            finally
            {
                _arenaManager.Unlock();
            }

            int removed = 0;
            int arenasCleared = 0;

            foreach (Arena arena in arenas)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                    continue;

                int count = 0;
                if (!_arenaLoop.QueueArenaWorkItem(arena, _ => count = RemoveAllBots(ad), arena))
                    continue;

                _arenaLoop.WaitForArenaWorkItemDrain(arena); // so 'count' is populated before we read it
                if (count > 0)
                {
                    removed += count;
                    arenasCleared++;
                }
            }

            _chat.SendMessage(player, $"Bots: load test stopped, removed {removed} bot(s) from {arenasCleared} arena(s).");
            _logManager.LogM(LogLevel.Info, nameof(BotsModule), $"Load test stopped, removed {removed} bot(s) from {arenasCleared} arena(s).");
        }

        /// <summary>
        /// Spawns load test bots scattered across an arena's map.
        /// </summary>
        /// <remarks>
        /// Every bot gets an <see cref="OrbitBrain"/>. It thrusts while turning, which produces a
        /// tight loop riding on a persistent line of travel, and it bounces off whatever it meets, so the
        /// population keeps moving and spreading out for as long as the test runs. A brain that flies
        /// straight would pile every bot against a wall within seconds, which both stops resembling play
        /// and makes the numbers depend on the map's wall layout instead of the load.
        /// </remarks>
        /// <param name="arena">The arena to spawn in.</param>
        /// <param name="count">How many bots to spawn.</param>
        /// <param name="combat">
        /// Whether the bots should fight. Combat bots fire and get killed, which drives the weapon and
        /// kill paths that orbiting bots never touch, at the cost of a noisier and less repeatable run.
        /// </param>
        /// <returns>How many bots were actually spawned.</returns>
        private int SpawnLoadTestBots(Arena arena, int count, bool combat)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return 0;

            // Create the fake players on this (command) thread — that is player-lifecycle work, which does
            // not belong on the arena thread. Adding them to the sim and bot list is arena state, so that
            // part is queued to the arena's thread below.
            const ShipType ship = ShipType.Warbird;
            List<(Player player, short freq, IBotBrain brain)> pending = new(count);
            for (int i = 0; i < count; i++)
            {
                // Combat bots are split across two freqs so that they have someone to fight. Orbiting
                // bots all sit on freq 0, since freq only adds noise when nobody is shooting.
                short freq = combat ? (short)(i % 2) : (short)0;

                Player? botPlayer = _fake.CreateFakePlayer($"LoadBot{i:D4}", arena, ShipType.Spec, freq);
                if (botPlayer is null)
                    break;

                _game.SetShipAndFreq(botPlayer, ship, freq);
                IBotBrain brain = combat ? new CombatBrain(BotProfile.Medium) : new OrbitBrain();
                pending.Add((botPlayer, freq, brain));
            }

            if (pending.Count == 0)
                return 0;

            int spawned = 0;
            bool queued = _arenaLoop.QueueArenaWorkItem(arena, _ =>
            {
                EnsureWorld(arena, ad);
                ReplayController? world = ad.World;
                if (world is null)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena, "Load test could not create a physics world for this arena.");
                    foreach ((Player player, short _, IBotBrain _) in pending)
                        _fake.EndFaked(player);
                    return;
                }

                // Deliberately seeded from the arena name rather than the clock, so that repeating a load
                // test puts the bots in the same places. Comparing a measurement against an earlier run is
                // only meaningful if the spread of bots, and therefore how many are in view, is the same.
                uint random = (uint)arena.Name.GetHashCode() | 1u;

                for (int i = 0; i < pending.Count; i++)
                {
                    (Player botPlayer, short freq, IBotBrain brain) = pending[i];

                    // The simulation has a fixed number of ship slots (MaxShips) and throws once they are
                    // gone, so a request for more bots than the arena can hold ends the run here. End the
                    // fakes that did not get a sim slot so they do not linger as shipless players.
                    int slot;
                    try
                    {
                        slot = world.GetOrAllocateShipSlot(new ID(botPlayer.Id));
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                            $"Load test stopped at {spawned} bot(s): the simulation has no ship slots left. {ex.Message}");

                        for (int j = i; j < pending.Count; j++)
                            _fake.EndFaked(pending[j].player);

                        break;
                    }

                    GetScatteredSpawn(arena, ref random, out short spawnX, out short spawnY);
                    C2S_PositionPacket spawn = new() { X = spawnX, Y = spawnY };
                    world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, world.CurrentTick + 1, botPlayer.Id, ship, freq, in spawn));

                    ad.AddedShipIds.Add(botPlayer.Id);
                    ad.PlayerById[botPlayer.Id] = botPlayer;
                    ad.Bots.Add(new Bot { Player = botPlayer, ShipSlot = slot, Brain = brain });

                    spawned++;
                }
            }, arena);

            if (!queued)
            {
                foreach ((Player player, short _, IBotBrain _) in pending)
                    _fake.EndFaked(player);
                return 0;
            }

            _arenaLoop.WaitForArenaWorkItemDrain(arena); // so 'spawned' is populated before we read it
            return spawned;
        }

        /// <summary>
        /// Picks a spawn position on open ground, spread over the whole map.
        /// </summary>
        /// <remarks>
        /// Maps have enclosed areas, so a narrow spread would strand part of the population in them.
        /// Covering the full map means only a small share start enclosed and the rest disperse normally.
        /// </remarks>
        private void GetScatteredSpawn(Arena arena, ref uint random, out short x, out short y)
        {
            const int TileCount = 1024;      // maps are 1024 x 1024 tiles
            const int PixelsPerTile = 16;
            const int Margin = 8;            // keep clear of the map's solid border
            const int MaxAttempts = 64;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                short tileX = (short)(Margin + (NextRandom(ref random) % (TileCount - (Margin * 2))));
                short tileY = (short)(Margin + (NextRandom(ref random) % (TileCount - (Margin * 2))));

                // 0 means there is no tile there, i.e. open space.
                if ((byte)_mapData.GetTile(arena, new TileCoordinates(tileX, tileY)) == 0)
                {
                    x = (short)((tileX * PixelsPerTile) + (PixelsPerTile / 2));
                    y = (short)((tileY * PixelsPerTile) + (PixelsPerTile / 2));
                    return;
                }
            }

            // Every attempt hit something solid, which a real map should not manage. Fall back to the
            // centre rather than skipping the bot, so the requested load still gets generated.
            x = (short)(TileCount / 2 * PixelsPerTile);
            y = (short)(TileCount / 2 * PixelsPerTile);
        }

        /// <summary>
        /// Xorshift, so that a load test is reproducible without depending on the shared PRNG.
        /// </summary>
        private static uint NextRandom(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        #endregion

        #region Per-arena / per-bot data

        private sealed class Bot
        {
            public required Player Player;
            public required int ShipSlot;
            public required IBotBrain Brain;
            public readonly NavAgent Nav = new(); // per-bot path follower
            public BotDecision Decision; // current intent (held between Think updates, executed each sim-pump)
            public bool IsDead;          // sim ship is in its death/enter-delay window
            // Set when Think produces a fresh Decision; consumed on the next sim-pump so a one-shot action
            // (weapon fire, ball kick) happens ONCE per decision, not once per pump. The engine's fire delay
            // still governs the real cadence.
            public bool NewDecisionPending;

            // Weapon-visual pending: set by ProcessEvents when the sim emits a WeaponFired/RepelFired for this
            // bot (the weapon it actually SPAWNED), and CLEARED when the send that carries the weapon goes out.
            // A fire triggers an immediate send that same pump, so this lives ~one pump.
            public bool FiredThisTick;
            public WeaponCodes LastFiredType;
            public byte LastFiredLevel;

            // The exact ship state the sim SPAWNED this heartbeat's weapon from (position/facing/velocity),
            // captured at fire time. Broadcast verbatim in the weapon packet so a client re-simulating from it
            // spawns the weapon at the identical point/heading/velocity and stays deterministic. Only read when
            // FiredThisTick is set (where it was captured this heartbeat).
            public ShipState FireShip;
            // The sim tick the weapon was fired at, so the broadcast can backdate the packet Time to the true
            // fire moment (→ C2SLatency), telling clients how far to fast-forward a traveling weapon.
            public uint FireTick;

            // Adaptive position-send state (mirrors Continuum's sub_43C920): we send at the base rate while
            // the bot is moving/turning, back off (2x / 4x) while it holds still, and send IMMEDIATELY on a
            // fire. These track what was last broadcast so we can tell "has it moved since?".
            public uint LastSentTick;
            public Point LastSentPos;
            public byte LastSentRot;

            // Set when the sim warps this bot's ship (EventKind.WarpFlash), cleared by the position packet
            // that carries the flash bit for it. That packet is sent at once rather than at the usual rate.
            public bool WarpFlashPending;
        }

        // A real player's position packet, copied off the callback's thread for the heartbeat to apply to
        // the sim on the arena thread. Carries the ship/freq too, since the ship may need adding on first
        // sighting. The Player reference is used to key PlayerById (for kill attribution).
        private readonly struct PositionInput
        {
            public readonly Player Player;
            public readonly ShipType Ship;
            public readonly short Freq;
            public readonly C2S_PositionPacket Packet;

            public PositionInput(Player player, ShipType ship, short freq, in C2S_PositionPacket packet)
            {
                Player = player;
                Ship = ship;
                Freq = freq;
                Packet = packet;
            }
        }

        private sealed class ArenaData : IResettable
        {
            /// <summary>Real players' position packets, produced on the callback's thread and consumed by the
            /// heartbeat on the arena thread. Lock-free so the hot position path never contends.</summary>
            public readonly ConcurrentQueue<PositionInput> PositionInputs = new();

            /// <summary>Bricks the server's brick module has placed, waiting to be handed to the sim. Filled by
            /// <c>Callback_BricksPlaced</c> (any thread), drained by the pump.</summary>
            public readonly ConcurrentQueue<BrickData> BrickInputs = new();

            /// <summary>Bricks currently in the sim: the external ID each was given and the sim tick it expires
            /// at, so its ID → slot mapping can be released afterwards. Arena-thread owned.</summary>
            public readonly List<(int ExternalId, uint ExpireTick)> ActiveBricks = new();

            /// <summary>Set once the "brick table is full" warning has been logged, cleared when a brick
            /// expires, so a flood of bricks logs it once rather than per brick. Arena-thread owned.</summary>
            public bool WarnedBricksFull;

            /// <summary>Bots whose carried-flag count the server's flag game has just changed. Filled by the
            /// flag callbacks (any thread), drained by the pump, which sends the sim the new count.</summary>
            public readonly ConcurrentQueue<Player> FlagChanges = new();

            /// <summary>The arena's authoritative simulation, or null until a world can be built.</summary>
            public ReplayController? World;

            /// <summary>Buffers combat events emitted during <see cref="ReplayController.Tick"/>.</summary>
            public PhysicsEventCollector? EventCollector;

            /// <summary>The arena's navigation service, built alongside <see cref="World"/>.</summary>
            public INavigation? Nav;

            /// <summary>The arena's carry-flag game, if one is running — used to sync carried-flag counts
            /// into the sim (for the flagger weapon boost) and to transfer flags on a kill. Null when the
            /// arena has no flag game. Acquired in EnsureWorld, released in DetachModule.</summary>
            public ICarryFlagGame? CarryFlags;

            /// <summary>The arena's ball game (Mode 3), if present. The sim OWNS the ball (physics + goal
            /// detection); we mirror it to this for real clients and record goals via TryReportGoal. Null when
            /// the arena has no balls. Acquired in EnsureWorld, released in Detach.</summary>
            public IBalls? Balls;

            /// <summary>The sim ball slot once seeded from the server ball, else -1.</summary>
            public int SimBallSlot = -1;

            public readonly List<Bot> Bots = new();

            /// <summary>External IDs (player.Id) whose ship has been added to the sim.</summary>
            public readonly HashSet<int> AddedShipIds = new();

            /// <summary>External ID → <see cref="Player"/>, for both real players and bots in the sim.</summary>
            public readonly Dictionary<int, Player> PlayerById = new();

            /// <summary>DIAG (temporary): throttles the per-arena combat-state log. Arena-thread owned.</summary>
            public int DiagCounter;

            /// <summary>DIAG (temporary): rollback rebuilds (CanonicalRebuilt) counted since the last 1 Hz report,
            /// and the deepest REAL-PLAYER PACKET STALENESS (ticks, = how late the packet was when applied =
            /// ServerTick.Now - packet.Time at drain) seen in that window. Reset each report. Arena-thread owned.</summary>
            public int RebuildCount;
            public int MaxStaleTicks;

            /// <summary>DIAG (temporary): weapons the sim reported as added / removed / adjusted by rollbacks
            /// since the last 1 Hz report — [0..2] fired by real players, [3..5] fired by bots.</summary>
            public readonly int[] WeaponCorrections = new int[6];

            /// <summary>Commands the sim refused (PendingCommandsOverflow events) since the last once-a-second
            /// health check — bot steering or player input that never reached the sim, almost always because
            /// the command buffer is full. Warned about and reset by that check; RefusedLastSecond keeps the
            /// last count for the diagnostic line. Arena-thread owned.</summary>
            public int RefusedCommands;
            public int RefusedLastSecond;
            public uint LastHealthTick;

            /// <summary>Sim clock (see ArenaTimer_Pump). LastPumpTime = wall-clock time of the last pump;
            /// TickDebt = ticks the sim currently owes the wall clock; SkippedTicks = ticks written off
            /// because the sim could not keep up, since the last warning (SkippedTotal: since the world was
            /// built); LastOverloadWarnTick rate-limits that warning. Arena-thread owned.</summary>
            public ServerTick LastPumpTime;
            public int TickDebt;
            public int SkippedTicks;
            public int SkippedTotal;
            public uint LastOverloadWarnTick;

            /// <summary>The zone seeds this arena's sim was last given (see SyncSeeds), or null if none yet —
            /// compared by reference against the module's current set — and what SkippedTotal stood at then,
            /// so a later write-off of ticks is noticed and the seeds handed over again. Arena-thread owned.</summary>
            public ZoneSeeds? SeedsApplied;
            public int SeedsAppliedAtSkipped;

            /// <summary>The most tiles one bot path search may examine (<c>Bots:NavSearchLimit</c>). Read at
            /// AttachModule; applied to the nav grid when it is built.</summary>
            public int NavSearchLimit = GridNavigation.DefaultMaxSearchExpansions;

            /// <summary>The sim commands per tick the world's command buffer is sized for
            /// (<c>Bots:MaxCommandsPerTick</c>). Read at AttachModule; used when the world is built.</summary>
            public int MaxCommandsPerTick = DefaultMaxCommandsPerTick;

            /// <summary>DIAG/PERF (temporary): sim ticks run over the last report window (÷ PumpSamples =
            /// average ticks per pump).</summary>
            public long StepsSum;

            /// <summary>DIAG/PERF (temporary): per-pump wall-time over the last report window, in Stopwatch
            /// ticks — summed + worst + sample count. The key number the LoadClient test reads: how long the
            /// pump body (drain + think + sim + events + send) takes, and whether it fits the pump budget.</summary>
            public long PumpTimeSum;
            public long PumpTimeMax;
            public int PumpSamples;

            /// <summary>DIAG/PERF (temporary): where the pump's time went over the last report window, in
            /// Stopwatch ticks — draining inputs, the AI/nav Think, and the sim's own Tick calls. What is left
            /// of PumpTimeSum is steering, events and sends.</summary>
            public long SplitDrain, SplitThink, SplitSim;

            // ---- Decoupled cadences (the 3-way split), in sim ticks. Read from [Bots] config at AttachModule. ----
            /// <summary>Sim ticks advanced per sim-pump (= SimPumpInterval / 10 ms).</summary>
            public uint PumpTicks = 2;
            /// <summary>Re-decide (Think) every this many ticks.</summary>
            public uint ThinkTicks = 10;
            /// <summary>Broadcast bot positions every this many ticks (fires are sent immediately regardless).</summary>
            public uint SendTicks = 2;
            /// <summary>Snapshot lane delays (ticks) this arena's controller was built with. The deepest
            /// (<c>Lanes[^1]</c>) is the rewind horizon; shown in the histogram so an op can line the lanes up
            /// against the distribution.</summary>
            public uint[] Lanes = [0, 15, 30, 50, 70];
            /// <summary>Whether the controller should re-seat its intermediate lanes from observed lag (lanes
            /// auto-derived and <c>Bots:LaneRetuneInterval</c> &gt; 0), with what options, and the interval in
            /// minutes for the log. Read from config at AttachModule; applied when the world is built.</summary>
            public bool AutoTuneLanes;
            public LaneAutoTuneOptions LaneTune;
            public int LaneRetuneMinutes;
            /// <summary>Last tick Think / the 1 Hz diag / the staleness report ran at. Arena-thread owned.
            /// (Position SEND is now per-bot adaptive — see <see cref="Bot"/> — not a single arena cadence.)</summary>
            public uint LastThinkTick;
            public uint LastDiagTick;
            public uint LastStatsTick;
            /// <summary>Staleness histogram report interval in ticks (0 = off). Report every this many ticks.</summary>
            public uint StatsIntervalTicks;
            /// <summary>Staleness histogram slice WIDTH (ticks). Fixed-width slices from 0 to the horizon give the
            /// true staleness distribution independent of lane placement (so an op can decide WHERE to put a lane,
            /// not just which existing lane is busiest).</summary>
            public uint StatsBucketTicks = 5;
            /// <summary>Staleness histogram, by fixed <see cref="StatsBucketTicks"/>-wide slice: bucket i counts
            /// real-player packets whose staleness was in [i*W, (i+1)*W); the LAST bucket is the overflow (staler
            /// than the horizon → clamped/at risk of drops). Accumulated in DrainPositionInputs, reported as a
            /// cumulative distribution and reset by ReportStaleHistogram. Arena-thread owned.</summary>
            public long[] StaleBuckets = new long[16];

            /// <summary>(ball id, scorer id) goals already credited this heartbeat, so a ball resting on a goal
            /// tile — which re-emits GoalScored every sub-tick until removed — is only scored once. Cleared each
            /// ProcessEvents. Arena-thread owned.</summary>
            public readonly HashSet<long> GoalsThisTick = new();

            bool IResettable.TryReset()
            {
                while (PositionInputs.TryDequeue(out _)) { }
                while (BrickInputs.TryDequeue(out _)) { }
                while (FlagChanges.TryDequeue(out _)) { }
                ActiveBricks.Clear();
                WarnedBricksFull = false;
                World = null;
                EventCollector = null;
                Nav = null;
                SeedsApplied = null;
                SeedsAppliedAtSkipped = 0;
                CarryFlags = null; // released in DetachModule (needs the arena broker); nulled here for reuse
                Balls = null;      // released in DetachModule (needs the arena broker); nulled here for reuse
                SimBallSlot = -1;
                Bots.Clear();
                AddedShipIds.Clear();
                PlayerById.Clear();
                DiagCounter = 0;
                RebuildCount = 0;
                RefusedCommands = 0;
                RefusedLastSecond = 0;
                LastHealthTick = 0;
                LastPumpTime = default;
                TickDebt = 0;
                SkippedTicks = 0;
                SkippedTotal = 0;
                LastOverloadWarnTick = 0;
                StepsSum = 0;
                MaxStaleTicks = 0;
                PumpTimeSum = 0;
                PumpTimeMax = 0;
                PumpSamples = 0;
                LastThinkTick = 0;
                LastDiagTick = 0;
                LastStatsTick = 0;
                System.Array.Clear(StaleBuckets);
                GoalsThisTick.Clear();
                return true;
            }
        }

        #endregion
    }
}
