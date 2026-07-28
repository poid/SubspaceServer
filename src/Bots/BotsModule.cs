using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using QS.Networking.Protocols.Common; // ID
using QS.Physics.Legacy;              // ReplayController, PhysicsCommand, CommandKind, EventKind, EventSink
using QS.Physics.Legacy.Structs;      // ShipState
using SS.Bots.Navigation;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

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

        private IPhysicsWorldProvider _worldProvider = null!;
        private ArenaDataKey<ArenaData> _adKey;

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

        public BotsModule(
            IArenaManager arenaManager,
            IFake fake,
            IGame game,
            IMainloopTimer mainloopTimer,
            ILogManager logManager,
            ICommandManager commandManager,
            IChat chat)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _fake = fake ?? throw new ArgumentNullException(nameof(fake));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        }

        #region Module life-cycle

        bool IModule.Load(IComponentBroker broker)
        {
            _worldProvider = new PhysicsWorldProvider(_logManager);
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            _commandManager.AddCommand("spawnbot", Command_spawnbot);
            _commandManager.AddCommand("killbots", Command_killbots);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _commandManager.RemoveCommand("spawnbot", Command_spawnbot);
            _commandManager.RemoveCommand("killbots", Command_killbots);

            _arenaManager.FreeArenaData(ref _adKey);
            return true;
        }

        bool IArenaAttachableModule.AttachModule(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ad.World = _worldProvider.CreateWorld(arena, ServerTick.Now);
            if (ad.World is not null)
            {
                ad.EventCollector = new PhysicsEventCollector();
                ad.World.SetEventListener(ad.EventCollector);

                // Build the static navigation substrate from the same map the sim uses. One-time,
                // per arena; door/brick state is fed in each tick / on events below.
                ad.Nav = GridNavigation.Build(ad.World.Canonical.Level, ShipRadiusTiles);
            }

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
            uint tick = world.CurrentTick;
            int externalId = player.Id;
            int slot = world.GetOrAllocateShipSlot(new ID(externalId));

            if (ad.AddedShipIds.Add(externalId))
            {
                world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, tick, externalId, player.Ship, player.Freq, in positionPacket));
                ad.PlayerById[externalId] = player;
            }

            // Authoritative position override from the real client.
            world.EnqueueCommand(PhysicsAdapter.ShipPositionCommand(slot, tick, in positionPacket));

            // Feed their weapon fire so the engine can resolve hits on bots (physics owns damage).
            WeaponCodes fired = positionPacket.Weapon.Type;
            if (fired != WeaponCodes.Null)
                world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(slot, tick, fired, positionPacket.Weapon.Level));
        }

        #endregion

        #region Bot heartbeat

        private bool MainloopTimer_Tick(Arena arena)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad) || ad.World is null)
                return true; // keep the timer alive; nothing to simulate yet

            ReplayController world = ad.World;
            uint tick = world.CurrentTick;

            // 0. Refresh the nav overlay with the engine's current door state (and drop stale bricks),
            //    so this tick's path queries route through open doors and around live bricks.
            if (ad.Nav is not null)
            {
                ad.Nav.UpdateDoors(world.Canonical.DoorOpenBitmask, world.Canonical.LastDoorSwitchTick, DefaultDoorDelayTicks);
                ad.Nav.PruneExpiredBricks(tick);
            }

            // 1. Each bot decides; translate intent into physics commands.
            foreach (Bot bot in ad.Bots)
            {
                BotContext context = new()
                {
                    World = world,
                    Navigation = ad.Nav!, // non-null whenever World is (built together in AttachModule)
                    ShipSlot = bot.ShipSlot,
                    CurrentTick = tick,
                };

                BotDecision decision = bot.Brain.Think(in context);
                world.EnqueueCommand(PhysicsAdapter.ThrustInputCommand(bot.ShipSlot, tick, in decision));
                if (decision.Fire != WeaponCodes.Null)
                    world.EnqueueCommand(PhysicsAdapter.WeaponFireCommand(bot.ShipSlot, tick, decision.Fire, decision.FireLevel));
            }

            // 2. Advance the authoritative simulation (fills the event collector).
            world.Tick(StepsPerTick);

            // 3. Resolve combat from the physics event stream, and reflect bricks into the nav overlay.
            ProcessEvents(ad, tick);

            // 4. Broadcast each bot's resulting motion through the server's normal path.
            foreach (Bot bot in ad.Bots)
            {
                ShipState ship = world.Canonical.Ships[bot.ShipSlot];
                C2S_PositionPacket pos = PhysicsAdapter.ToPositionPacket(in ship);
                pos.Time = ServerTick.Now;
                pos.SetChecksum();
                _game.FakePosition(bot.Player, ref pos);
            }

            return true;
        }

        // Physics is authoritative for weapons/damage: translate its combat events into server
        // actions. WeaponHit/BombExploded carry (victim, attacker); PlayerDied carries only the
        // victim, so we attribute the kill to the last ship that damaged them.
        private void ProcessEvents(ArenaData ad, uint currentTick)
        {
            PhysicsEventCollector? collector = ad.EventCollector;
            if (collector is null)
                return;

            foreach (EventSink.Entry e in collector.Events)
            {
                switch (e.Kind)
                {
                    case EventKind.WeaponHit:
                    case EventKind.BombExploded:
                        ad.LastDamagerByVictim[e.PrimaryId.Int] = e.SecondaryId.Int;
                        break;

                    case EventKind.PlayerDied:
                        HandleDeath(ad, e.PrimaryId.Int);
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

        private void HandleDeath(ArenaData ad, int victimId)
        {
            if (!ad.PlayerById.TryGetValue(victimId, out Player? victim))
                return;

            Player? killer = null;
            if (ad.LastDamagerByVictim.TryGetValue(victimId, out int killerId))
                ad.PlayerById.TryGetValue(killerId, out killer);
            ad.LastDamagerByVictim.Remove(victimId);

            // The engine decided this ship died; tell the server/clients so scoring and the normal
            // death/respawn flow happen. FakeKill needs a killer — self-attribute if we couldn't
            // determine one (e.g. death by wall/wormhole).
            _game.FakeKill(killer ?? victim, victim, 0, 0);

            // TODO(respawn): re-add the bot's ship to the sim at a spawn point after its death delay.
            // TODO(real players): a real client is normally authoritative over its own death. Driving
            // a real player's death purely from the server needs energy + death sync to feel right;
            // decide the policy (server-authoritative combat vs. advisory) before enabling PvP damage
            // against humans.
        }

        #endregion

        #region Commands

        private void Command_spawnbot(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (ad.World is null)
            {
                _chat.SendMessage(player, "Bots: no physics world for this arena (see IPhysicsWorldProvider). Cannot spawn.");
                return;
            }

            string name = parameters.Trim().ToString();
            if (name.Length == 0)
                name = "Bot";

            // TODO: parse ship/freq from parameters. Default to a Warbird on freq 0.
            const ShipType ship = ShipType.Warbird;
            const short freq = 0;

            Player? botPlayer = _fake.CreateFakePlayer(name, arena, ship, freq);
            if (botPlayer is null)
            {
                _chat.SendMessage(player, "Bots: failed to create fake player.");
                return;
            }

            ReplayController world = ad.World;
            int slot = world.GetOrAllocateShipSlot(new ID(botPlayer.Id));

            // TODO: choose a real spawn location from arena settings/map instead of map centre.
            C2S_PositionPacket spawn = new() { X = 512 * 16, Y = 512 * 16 };
            world.EnqueueCommand(PhysicsAdapter.ShipAddCommand(slot, world.CurrentTick, botPlayer.Id, ship, freq, in spawn));

            ad.AddedShipIds.Add(botPlayer.Id);
            ad.PlayerById[botPlayer.Id] = botPlayer;
            ad.Bots.Add(new Bot { Player = botPlayer, ShipSlot = slot, Brain = new Brains.OrbitBrain() });

            _chat.SendMessage(player, $"Bots: spawned '{name}'.");
        }

        private void Command_killbots(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            int count = ad.Bots.Count;
            foreach (Bot bot in ad.Bots)
            {
                ad.World?.EnqueueCommand(PhysicsAdapter.ShipRemoveCommand(bot.ShipSlot, ad.World.CurrentTick));
                ad.PlayerById.Remove(bot.Player.Id);
                ad.AddedShipIds.Remove(bot.Player.Id);
                _fake.EndFaked(bot.Player);
            }
            ad.Bots.Clear();

            _chat.SendMessage(player, $"Bots: removed {count} bot(s).");
        }

        #endregion

        #region Per-arena / per-bot data

        private sealed class Bot
        {
            public required Player Player;
            public required int ShipSlot;
            public required IBotBrain Brain;
        }

        private sealed class ArenaData : IResettable
        {
            /// <summary>The arena's authoritative simulation, or null until a world can be built.</summary>
            public ReplayController? World;

            /// <summary>Buffers combat events emitted during <see cref="ReplayController.Tick"/>.</summary>
            public PhysicsEventCollector? EventCollector;

            /// <summary>The arena's navigation service, built alongside <see cref="World"/>.</summary>
            public INavigation? Nav;

            public readonly List<Bot> Bots = new();

            /// <summary>External IDs (player.Id) whose ship has been added to the sim.</summary>
            public readonly HashSet<int> AddedShipIds = new();

            /// <summary>External ID → <see cref="Player"/>, for both real players and bots in the sim.</summary>
            public readonly Dictionary<int, Player> PlayerById = new();

            /// <summary>Victim external ID → last attacker external ID, for kill attribution.</summary>
            public readonly Dictionary<int, int> LastDamagerByVictim = new();

            bool IResettable.TryReset()
            {
                World = null;
                EventCollector = null;
                Nav = null;
                Bots.Clear();
                AddedShipIds.Clear();
                PlayerById.Clear();
                LastDamagerByVictim.Clear();
                return true;
            }
        }

        #endregion
    }
}
