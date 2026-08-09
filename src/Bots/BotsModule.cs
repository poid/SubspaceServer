using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using QS.Networking.Protocols.Common; // ID
using QS.Physics.Legacy;              // ReplayController, PhysicsCommand, CommandKind, EventKind, EventSink
using QS.Physics.Legacy.Structs;      // ShipState
using SS.Bots.Ai;
using SS.Bots.Navigation;
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
    /// Each attached arena gets its own authoritative <see cref="ReplayController"/> world.
    /// The data flow, all on the mainloop thread, is:
    /// </para>
    /// <list type="number">
    ///   <item>Real players' position packets are mirrored into the world via
    ///         <see cref="PlayerPositionPacketCallback"/> (position override + their weapon fire),
    ///         so the simulation — and every bot — has full knowledge of the arena.</item>
    ///   <item>On a 10 Hz timer: each bot's <see cref="IBotBrain"/> decides, its intent is turned
    ///         into physics commands, the world is stepped, and combat is resolved from the
    ///         engine's event stream (the physics engine — not the server — is authoritative for
    ///         weapons and damage).</item>
    ///   <item>Each bot's resulting motion is broadcast to real clients through
    ///         <see cref="IGame.FakePosition"/>, exactly like a human player's movement.</item>
    /// </list>
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
        private readonly IMainloopTimer _mainloopTimer;
        private readonly ILogManager _logManager;
        private readonly ICommandManager _commandManager;
        private readonly IChat _chat;
        private readonly IMapData _mapData;
        private readonly IClientSettings _clientSettings;

        private IPhysicsWorldProvider _worldProvider = null!;
        private ArenaDataKey<ArenaData> _adKey;
        private int _diagCounter; // DIAG (temporary): throttles the combat-state log

        /// <summary>Emit cadence — 10 Hz, matching a human client's position-send rate. Continuum
        /// interpolates between snapshots, so we do not emit at the simulation's internal rate.</summary>
        private const int TickIntervalMs = 100;

        /// <summary>Physics ticks advanced per emit. Assumes a 100 Hz simulation against the 100 ms
        /// emit above. TODO: confirm the engine's internal tick rate and adjust to match.</summary>
        private const uint StepsPerTick = 10;

        /// <summary>How far to inflate walls when building the nav grid. TODO: derive from ship radius.</summary>
        private const int ShipRadiusTiles = 1;

        /// <summary>Door cycle length fed to the nav wait-cost model. TODO: source from arena settings (DoorDelay).</summary>
        private const uint DefaultDoorDelayTicks = 300;

        /// <summary>Brick lifetime used when marking brick tiles blocked. TODO: source real BrickTime + span from the brick entity.</summary>
        private const uint DefaultBrickTicks = 1500;

        /// <summary>External id for the arena's single simulated ball (ball ID space is separate from ships).</summary>
        private const int SimBallExternalId = 0xBA11;

        // (ball id, scorer id) goals already credited this heartbeat, so a ball resting on a goal tile — which
        // re-emits GoalScored every sub-tick until removed — is only scored once. Cleared each ProcessEvents.
        private readonly HashSet<long> _goalsThisTick = new();

        public BotsModule(
            IArenaManager arenaManager,
            IFake fake,
            IGame game,
            IMainloopTimer mainloopTimer,
            ILogManager logManager,
            ICommandManager commandManager,
            IChat chat,
            IMapData mapData,
            IClientSettings clientSettings)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _fake = fake ?? throw new ArgumentNullException(nameof(fake));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        }

        #region Module life-cycle

        bool IModule.Load(IComponentBroker broker)
        {
            _worldProvider = new PhysicsWorldProvider(_mapData, _clientSettings, _logManager);
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

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
            PlayerPositionPacketCallback.Register(arena, Callback_PlayerPosition);
            _mainloopTimer.SetTimer<Arena>(MainloopTimer_Tick, TickIntervalMs, TickIntervalMs, arena, arena);
            return true;
        }

        bool IArenaAttachableModule.DetachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            _mainloopTimer.ClearTimer<Arena>(MainloopTimer_Tick, arena);
            PlayerPositionPacketCallback.Unregister(arena, Callback_PlayerPosition);

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

        // Mirror every real player's motion into the arena's simulation so the world model — and
        // therefore every bot — reflects reality. Bots are excluded (they are driven by the sim,
        // not fed back into it).
        private void Callback_PlayerPosition(Player player, ref readonly C2S_PositionPacket positionPacket, ref readonly ExtraPositionData extra, bool hasExtraPositionData)
        {
            if (player.Type == ClientType.Fake)
                return;

            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return;

            ReplayController world = ad.World;
            uint tick = world.CurrentTick + 1; // commands must target a future tick (engine drops tick <= current)
            int externalId = player.Id;
            int slot = world.GetOrAllocateShipSlot(new ID(externalId));

            if (ad.AddedShipIds.Add(externalId))
            {
                world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, tick, externalId, player.Ship, player.Freq, in positionPacket));
                ad.PlayerById[externalId] = player;
            }

            // Authoritative position override from the real client.
            world.EnqueueCommand(PhysicsAdapter.ShipPositionCommand(slot, tick, in positionPacket));

            // Feed their weapon fire so the engine can resolve hits on bots (physics owns damage). The
            // engine spawns the projectile from the command's position/rotation/velocity, so pass the whole
            // packet — those fields describe where the shot originated.
            if (positionPacket.Weapon.Type != WeaponCodes.Null)
                world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(slot, tick, in positionPacket));
        }

        #endregion

        #region Bot heartbeat

        private bool MainloopTimer_Tick(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return true; // keep the timer alive; nothing to simulate yet

            ReplayController world = ad.World;

            // 0. Refresh the nav overlay with the engine's current door state (and drop stale bricks),
            //    so path queries route through open doors and around live bricks.
            if (ad.Nav is not null)
            {
                ad.Nav.UpdateDoors(world.Canonical.DoorOpenBitmask, world.Canonical.LastDoorSwitchTick, DefaultDoorDelayTicks);
                ad.Nav.PruneExpiredBricks(world.CurrentTick);
            }

            // Ball (Mode 3): the SERVER owns ownership; the sim only PREDICTS. Reconcile the sim to the
            // server's authoritative ball first (seeds it, and corrects last heartbeat's misprediction), then
            // read the server state the bots perceive and decide from (possession comes from the server, so a
            // bot never acts on a wrong prediction).
            ReconcileBallToSim(ad, arena, world);
            BallData serverBall = default;
            bool haveBall = ad.Balls is not null
                && ad.Balls.TryGetBallData(arena, 0, out serverBall)
                && serverBall.State is BallState.OnMap or BallState.Carried;

            // 1. Each bot decides once this heartbeat (10 Hz); the decision drives the whole sim window.
            //    A dead / respawning ship (in its death or enter-delay window) takes no action — the brain
            //    is skipped and the decision zeroed, so a corpse neither steers nor fires (the engine drops
            //    a dead ship's fire commands anyway, but we must also stop rendering the phantom shots).
            foreach (Bot bot in ad.Bots)
            {
                bot.FiredThisTick = false; // set from this heartbeat's WeaponFired events (for the visual)

                // Sync the bot's server-side carried-flag count into the sim ship so the engine's flagger
                // logic (weapon-level boost, fire cost) applies. The sim has no flag ENTITIES for the bot,
                // so this only feeds the count — the server flag game stays authoritative for gameplay.
                if (ad.CarryFlags is not null)
                    world.Canonical.Ships[bot.ShipSlot].FlagCount = (uint)ad.CarryFlags.GetFlagCount(bot.Player);

                ref readonly ShipState s = ref world.Canonical.Ships[bot.ShipSlot];
                bot.IsDead = s.DeathRemainingTimer > 0 || s.EnterDelayRemaining > 0;
                if (bot.IsDead)
                {
                    bot.Decision = default;
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
            }

            // 2. Advance the sim one tick at a time, steering each bot every tick. SteerShip re-reads
            //    the ship's current rotation and turns toward the target via TurnLeft/TurnRight (rate-
            //    limited, so no overshoot) while thrusting via ThrustOnly every tick — so bots turn at
            //    the real rotation rate AND accelerate fully, flying like real ships. Weapon fire is
            //    issued once. Stepping singly (vs Tick(StepsPerTick)) also lets the event collector see
            //    every tick's combat events.
            for (uint sub = 0; sub < StepsPerTick; sub++)
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

                    // Fire once per decision (on the first sub-tick); the engine's fire delay governs cadence.
                    // Spawn it from the bot's live sim state so the bullet leaves the ship in its facing
                    // direction (the engine reads the spawn point/heading/velocity off the command).
                    if (sub == 0 && d.Fire != WeaponCodes.Null)
                        world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(
                            bot.ShipSlot, cmdTick, d.Fire, d.FireLevel, in world.Canonical.Ships[bot.ShipSlot]));

                    // Kick the ball once if the bot decided to and actually carries it in the sim.
                    if (sub == 0 && d.BallAction == BallIntent.Fire && ad.SimBallSlot >= 0)
                        TryBotKick(ad, world, bot, cmdTick, d.BallTarget);
                }
                world.Tick(1);

                // Keep the server (authoritative) and sim (predictor) agreeing on ball ownership each sub-tick:
                // request the pickup/shot the sim predicts, then reconcile the sim to the server's ruling (the
                // "undo" when it isn't granted). Landing at the next 10ms sub-tick, not the next heartbeat.
                SyncBallOwnership(ad, arena, world);
            }

            // 3. Resolve combat + ball events from the physics event stream (accumulated across the sub-ticks).
            ProcessEvents(ad, arena, world);

            // 3b. Final reconcile: pull in any server ball change since the sub-ticks (e.g. a real player's
            //     pickup/fire packet that landed on the mainloop) so the sim ends the heartbeat matching truth.
            ReconcileBallToSim(ad, arena, world);

            // 4. Broadcast each bot's resulting motion through the server's normal path.
            foreach (Bot bot in ad.Bots)
            {
                ShipState ship = world.Canonical.Ships[bot.ShipSlot];
                C2S_PositionPacket pos = PhysicsAdapter.ToPositionPacket(in ship);

                // Surface the bot's weapon fire to clients so they see the shot (the physics engine owns the
                // actual damage). We render exactly what the sim SPAWNED this heartbeat (captured from the
                // WeaponFired event), not what the brain intended — so the visual weapon type and level match
                // the sim's own computation, including bullet→bounce / bomb→prox promotion, gun/bomb upgrades,
                // and the flagger weapon-level boost. A dropped fire (cooldown/safe zone) simply doesn't set it.
                if (bot.FiredThisTick)
                {
                    pos.Weapon.Type = bot.LastFiredType;
                    pos.Weapon.Level = bot.LastFiredLevel;
                }

                pos.Time = ServerTick.Now;
                pos.SetChecksum();
                _game.FakePosition(bot.Player, ref pos);
            }

            // DIAG (temporary): once/sec, one line PER bot so we see the actual combatants (freq, target
            // freq, fire state) and how close enemy bullets get. fq==tgtFq would mean teammates (no hits).
            // nWpn is the nearest ENEMY bullet in px: near the ~18px hit box with no WeaponHit => collision
            // scan; never close => bullets never reach the ship (spawn/travel/aim).
            if ((++_diagCounter % 10) == 0)
            {
                foreach (Bot bot in ad.Bots)
                {
                    ID id = new(bot.Player.Id);
                    ref readonly ShipState s = ref world.Canonical.Ships[bot.ShipSlot];
                    bool hasTgt = world.TryGetClosestEnemy(id, out EntityLocation tgt);
                    int distTiles = hasTgt ? (int)(System.Math.Sqrt(tgt.DistanceSquared) / 16000) : -1;
                    bool hasWpn = world.TryGetClosestThreat(id, out EntityLocation wpn);
                    long wpnPx = hasWpn ? (long)(System.Math.Sqrt(wpn.DistanceSquared) / 1000) : -1;
                    // rr = current rotation rate the engine uses; ir = the arena's InitialRotation setting.
                    // If rr/ir are far below your zone's Rotation setting, the sim is reading a low value
                    // (settings/parse); if they match it but turning still looks slow, it's the rotation
                    // rate calibration or StepsPerTick, not the value.
                    _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                        $"CBT {bot.Player.Name} fq={s.Frequency} tile=({s.Coordinates1000.X / 16000},{s.Coordinates1000.Y / 16000}) rot={s.Rotation}->{bot.Decision.Rotation} thr={bot.Decision.ThrustOn} rev={bot.Decision.ThrustReverse} vel=({s.Velocity.X},{s.Velocity.Y}) rad={s.Radius} rr={s.RotationRateCurrent} nav={bot.Nav.LastResult}/{bot.Nav.LastPathLen} ball={bot.Decision.BallAction} tgtFq={(hasTgt ? tgt.Frequency : -1)} dist={distTiles}t");

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

        // Physics is authoritative for weapons/damage: translate its combat events into server actions.
        // PlayerDied now carries the killer in SecondaryId (all sources: bullets, bombs, shrap, collisions),
        // so attribution is a direct lookup. WeaponFired tells us the exact weapon the sim spawned, which we
        // mirror to clients as the visual.
        private void ProcessEvents(ArenaData ad, Arena arena, ReplayController world)
        {
            PhysicsEventCollector? collector = ad.EventCollector;
            if (collector is null)
                return;

            uint currentTick = world.CurrentTick;
            _goalsThisTick.Clear();

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

                    case EventKind.PlayerDied:
                        HandleDeath(ad, arena, e.PrimaryId.Int, e.SecondaryId.Int);
                        break;

                    case EventKind.GoalScored:
                        // One goal per (ball, scorer) per heartbeat. The ball sits on the goal tile and
                        // re-emits GoalScored on every remaining sub-tick until ReconcileBallToSim removes it,
                        // which would otherwise credit the same goal ~StepsPerTick times. Keyed by ball id so an
                        // arena with multiple balls still scores each independently.
                        long goalKey = ((long)e.PrimaryId.Int << 32) | (uint)e.SecondaryId.Int;
                        if (_goalsThisTick.Add(goalKey))
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

                    case EventKind.BrickPlaced:
                        // IntA/IntB = brick tile. TODO: a brick is a wall span — read the full
                        // start/end and real expiry from world.Canonical.Bricks instead of one tile.
                        TileCoord brick = new((short)e.IntA, (short)e.IntB);
                        ad.Nav?.AddBrick(brick, brick, currentTick + DefaultBrickTicks);
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
        // ship settings and bots spawn frozen.
        private void EnsureWorld(Arena arena, ArenaData ad)
        {
            if (ad.World is not null)
                return;

            ad.World = _worldProvider.CreateWorld(arena, ServerTick.Now);
            if (ad.World is not null)
            {
                ad.EventCollector = new PhysicsEventCollector();
                ad.World.SetEventListener(ad.EventCollector);
                ad.Nav = GridNavigation.Build(ad.World.Canonical.Level, ShipRadiusTiles);

                // DIAG (temporary): if BlockedTiles is ~0, the nav grid has no walls (map tiles didn't reach
                // it) — which would make bots navigate straight through everything and pin on physical walls.
                Navigation.NavStats ns = ad.Nav.Stats;
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

            EnsureWorld(arena, ad);
            if (ad.World is null)
            {
                _chat.SendMessage(player, "Bots: could not build a physics world (client settings unavailable for this arena).");
                return;
            }

            // Usage: ?spawnbot [orbit|straight|combat|ball [easy|medium|hard]] [freq] [name]. Optional brain
            // selector (then a difficulty for combat/ball), then an optional numeric freq (default 0), then
            // the name. "straight" = wall-collision probe; "combat" = Mode 4 fighter; "ball" = Mode 3 soccer
            // (retrieve/carry/score/defend). Spawn bots on different freqs to have them play/fight.
            ReadOnlySpan<char> parms = parameters.Trim();

            IBotBrain brain = new Brains.OrbitBrain();
            string brainLabel = "orbit";
            ReadOnlySpan<char> token = PeekToken(parms);
            if (token.Equals("straight", StringComparison.OrdinalIgnoreCase))
            {
                brain = new Brains.StraightBrain();
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

                brain = new Brains.CombatBrain(profile);
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

                brain = new Brains.BallBrain(profile);
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

            ReplayController world = ad.World;
            int slot = world.GetOrAllocateShipSlot(new ID(botPlayer.Id));

            // Spawn where the commanding player is, so it's immediately visible (fallback: map centre).
            short spawnX = player.Position.X != 0 ? player.Position.X : (short)(512 * 16);
            short spawnY = player.Position.Y != 0 ? player.Position.Y : (short)(512 * 16);
            C2S_PositionPacket spawn = new() { X = spawnX, Y = spawnY };
            world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, world.CurrentTick + 1, botPlayer.Id, ship, freq, in spawn));

            ad.AddedShipIds.Add(botPlayer.Id);
            ad.PlayerById[botPlayer.Id] = botPlayer;
            ad.Bots.Add(new Bot { Player = botPlayer, ShipSlot = slot, Brain = brain });

            _chat.SendMessage(player, $"Bots: spawned '{name}' ({brainLabel}, freq {freq}).");
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

            int count = RemoveAllBots(ad);

            _chat.SendMessage(player, $"Bots: removed {count} bot(s).");
        }

        /// <summary>
        /// Removes every bot in an arena.
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
            int removed = 0;
            int arenasCleared = 0;

            _arenaManager.Lock();

            try
            {
                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.Bots.Count == 0)
                        continue;

                    removed += RemoveAllBots(ad);
                    arenasCleared++;
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            _chat.SendMessage(player, $"Bots: load test stopped, removed {removed} bot(s) from {arenasCleared} arena(s).");
            _logManager.LogM(LogLevel.Info, nameof(BotsModule), $"Load test stopped, removed {removed} bot(s) from {arenasCleared} arena(s).");
        }

        /// <summary>
        /// Spawns load test bots scattered across an arena's map.
        /// </summary>
        /// <remarks>
        /// Every bot gets an <see cref="Brains.OrbitBrain"/>. It thrusts while turning, which produces a
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

            EnsureWorld(arena, ad);

            ReplayController? world = ad.World;
            if (world is null)
            {
                _logManager.LogA(LogLevel.Warn, nameof(BotsModule), arena, "Load test could not create a physics world for this arena.");
                return 0;
            }

            // Deliberately seeded from the arena name rather than the clock, so that repeating a load
            // test puts the bots in the same places. Comparing a measurement against an earlier run is
            // only meaningful if the spread of bots, and therefore how many are in view, is the same.
            uint random = (uint)arena.Name.GetHashCode() | 1u;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                const ShipType ship = ShipType.Warbird;

                // Combat bots are split across two freqs so that they have someone to fight. Orbiting
                // bots all sit on freq 0, since freq only adds noise when nobody is shooting.
                short freq = combat ? (short)(i % 2) : (short)0;

                Player? botPlayer = _fake.CreateFakePlayer($"LoadBot{i:D4}", arena, ShipType.Spec, freq);
                if (botPlayer is null)
                    break;

                _game.SetShipAndFreq(botPlayer, ship, freq);

                // The simulation has a fixed number of ship slots (MaxShips) and throws once they are
                // gone, so a request for more bots than the arena can hold would otherwise abort the
                // whole command part way and leave this bot behind as a player with no ship.
                int slot;
                try
                {
                    slot = world.GetOrAllocateShipSlot(new ID(botPlayer.Id));
                }
                catch (Exception ex)
                {
                    _fake.EndFaked(botPlayer);

                    _logManager.LogA(LogLevel.Info, nameof(BotsModule), arena,
                        $"Load test stopped at {spawned} bot(s): the simulation has no ship slots left. {ex.Message}");

                    break;
                }

                GetScatteredSpawn(arena, ref random, out short spawnX, out short spawnY);
                C2S_PositionPacket spawn = new() { X = spawnX, Y = spawnY };
                world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, world.CurrentTick + 1, botPlayer.Id, ship, freq, in spawn));

                ad.AddedShipIds.Add(botPlayer.Id);
                ad.PlayerById[botPlayer.Id] = botPlayer;

                IBotBrain brain = combat ? new Brains.CombatBrain(BotProfile.Medium) : new Brains.OrbitBrain();

                ad.Bots.Add(new Bot { Player = botPlayer, ShipSlot = slot, Brain = brain });

                spawned++;
            }

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
            public readonly Navigation.NavAgent Nav = new(); // per-bot path follower
            public BotDecision Decision; // this heartbeat's intent, executed across the sim window
            public bool IsDead;          // sim ship is in its death/enter-delay window this heartbeat

            // The weapon the sim actually SPAWNED this heartbeat (from the WeaponFired event), used to
            // render a client-accurate visual. FiredThisTick is reset at the top of every heartbeat.
            public bool FiredThisTick;
            public WeaponCodes LastFiredType;
            public byte LastFiredLevel;
        }

        private sealed class ArenaData : IResettable
        {
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

            bool IResettable.TryReset()
            {
                World = null;
                EventCollector = null;
                Nav = null;
                CarryFlags = null; // released in DetachModule (needs the arena broker); nulled here for reuse
                Balls = null;      // released in DetachModule (needs the arena broker); nulled here for reuse
                SimBallSlot = -1;
                Bots.Clear();
                AddedShipIds.Clear();
                PlayerById.Clear();
                return true;
            }
        }

        #endregion
    }
}
