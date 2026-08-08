# Bots — server-side AI players

A plugin that spawns **fake players driven by the QS physics engine**, so people can play
against server-side bots without any external bot programs. Because the bots live inside the
server they add no network traffic and have full, authoritative knowledge of every player.

Status: **runnable, pending runtime validation**. The pipeline is wired end-to-end, the solution
builds, and [`PhysicsWorldProvider`](PhysicsWorldProvider.cs) now builds a real, configured physics
world for an arena (map + client settings + collision settings). What remains is to actually run it —
spawn a bot in a live arena and confirm it moves with correct physics — plus the real brains.

## How it works

Everything runs on the mainloop thread. Per attached arena:

```
real players ──position packets──▶ Callback_PlayerPosition ──▶ EnqueueCommand(ShipPositionUpdate
                                                                 + WeaponFire)
                                                                        │
                          (every 100 ms, IMainloopTimer)               ▼
   IBotBrain.Think ──▶ EnqueueCommand(ShipThrustInput / WeaponFire) ──▶ World.Tick()
                                                                        │
                          combat events (PlayerDied / WeaponHit) ◀──────┤
                                    │                                    │
                          FakeKill  ▼                        read Canonical.Ships[slot]
                                                                        │
                                            ToPositionPacket ──▶ IGame.FakePosition ──▶ clients
```

- **Combat authority is the physics engine, not the server.** Real players' weapon fire (carried
  in their position packets) is fed into the sim, the engine resolves hits/damage/deaths, and the
  module translates `PlayerDied` events into `IGame.FakeKill`. The server's own weapon/damage path
  is deliberately not used.
- **Positions** are mirrored in via `PlayerPositionPacketCallback` and emitted out via
  `IGame.FakePosition` — the same mechanism the Replay module uses.

### Files

| File | Role |
|---|---|
| [`BotsModule.cs`](BotsModule.cs) | Lifecycle, per-arena world, the real-player feed, the tick loop, combat-event handling, `?spawnbot` / `?killbots`. |
| [`PhysicsAdapter.cs`](PhysicsAdapter.cs) | The one seam: converts between `SS.Packets.Game` (pixels, `WeaponCodes`, `ShipType`) and QS (`1000`-coords, `WeaponTypes`, `ShipTypes`). |
| [`IPhysicsWorldProvider.cs`](IPhysicsWorldProvider.cs) / [`PhysicsWorldProvider.cs`](PhysicsWorldProvider.cs) | Builds a configured `ReplayController` for an arena. **The main unimplemented seam.** |
| [`PhysicsEventCollector.cs`](PhysicsEventCollector.cs) | Buffers physics events during `Tick` for post-tick handling. |
| [`IBotBrain.cs`](IBotBrain.cs) / [`Brains/OrbitBrain.cs`](Brains/OrbitBrain.cs) | Pluggable AI (`Think(in BotContext)`); the example brain just flies in a loop. |
| [`Navigation/INavigation.cs`](Navigation/INavigation.cs) | The navigation service **shape** — path/LOS/firing-position/region queries + the door/brick overlay feed. |
| [`Navigation/GridNavigation.cs`](Navigation/GridNavigation.cs) | Implementation: inflated walkability grid, region/portal decomposition, grid A* with door-wait cost + brick overlay, LOS. |
| [`Brains/NavSeekBrain.cs`](Brains/NavSeekBrain.cs) | Example of a brain consuming `INavigation`: path to a goal and steer the waypoints. |

## Navigation

`INavigation` is the one interface all four gameplay modes go through; they differ only in which
goal they ask for. It is a two-part model:

- **Static substrate** (built once per arena from `LevelArray`, since map geometry never changes):
  a walkability grid with walls inflated by ship radius, flood-filled into **regions separated by
  doors**, with doors as **portals** between regions.
- **Dynamic overlay** (pushed each tick by the module from the physics world): **door open/closed
  state** — a closed-but-cycling door is priced as a *waitable gate* (high-cost passable edge), not
  a wall — and **player-dropped bricks** as temporary fully-blocking tiles.

Queries: `TryFindPath` (global planner), `HasLineOfSight` / `TryFindFiringPosition` (combat and
passing want a firing position, not the target's tile), and `GetRegion` / `AreConnected` (door-gated
connectivity answered at region granularity).

The current implementation uses grid A*; the interface is shaped so the internals can move to **Jump
Point Search** (faster on these uniform-cost grids) and **D\* Lite** (incremental replanning as
doors/bricks change) with no caller change. `GridNavigation` depends only on a `LevelArray`, so it is
**independently testable right now** — you can build one from a hand-made level and exercise
pathfinding without the settings bridge being done.

## Build & layout requirement

This project references the physics engine by **project reference across repositories**. It
assumes the `QS` repo sits beside `SubspaceServer`:

```
Subspace Related/
├── SubspaceServer/        ← this repo
└── QS/
    ├── Physics/Physics Legacy Library/
    └── Libraries/...      ← Game Common Protocol Library, etc.
```

The reference is `..\..\..\QS\Physics\Physics Legacy Library\Physics Legacy Library.csproj`. If
the `QS` repo is missing or moved, the solution build breaks. Alternatives if that coupling is
unwanted: reference pre-built DLLs, or publish the physics engine as a NuGet package.

**Dependency footprint:** the physics engine depends on the QS *Game Common Protocol Library*,
which transitively pulls the QS networking/encryption/zlib/json stack into the plugin folder
(`Zone/bin/modules/Bots/`). These load in the plugin's isolated `AssemblyLoadContext`, so they
don't clash with the host, but it is heavier than a pure physics dependency. Trimming would mean
the engine depending only on the protocol *structs*, not the full networking library.

## Enabling it

1. Build the solution (requires the `QS` repo as above).
2. Load the module — add to `Zone/conf/Modules.config`:
   ```xml
   <module type="SS.Bots.BotsModule" path="bin/modules/Bots/SS.Bots.dll" />
   ```
3. Attach per arena — in the arena's conf, add `Bots` to `Modules:AttachModules`.
4. Grant the command capabilities — the commands are capability-gated like every server command, so
   add `cmd_spawnbot` and `cmd_killbots` to the appropriate group file in `conf/groupdef.dir/`
   (e.g. `sysop`, alongside `cmd_makefake`). Without this, the commands are silently denied even
   though the module loaded. A public command `?foo` needs `cmd_foo`; a private/targeted one needs
   `privcmd_foo`.
5. In-game: `?spawnbot <name>` and `?killbots`.

`?spawnbot` will report "no physics world for this arena" only if the client settings can't be read
(e.g. the arena has none); otherwise a configured world is built on attach and bots simulate.

## Remaining work

1. **Runtime validation of the world bridge** — `PhysicsWorldProvider` is implemented (map →
   `LevelArray`, raw client settings → `GameSettings`, `CollisionArenaSettings` from the parsed arena
   settings). It builds and the 1428-byte settings layout matches by construction, but it hasn't been
   run yet: spawn a bot in a live arena and confirm it moves with correct physics (thrust/recharge),
   which is also the first real test that the server's `S2C_ClientSettings` bytes parse correctly in
   the QS `GameSettings`. Getting the raw bytes needed one small Core addition,
   `IClientSettings.GetClientSettingsData`.
2. **Velocity scale** — confirm the wire-`XSpeed`/`YSpeed` ↔ engine-velocity factor
   (`PhysicsAdapter.VelocityScale`, currently a placeholder) against the QS packet-ingestion code.
   The position (`1000`×pixels) scale is confirmed; velocity is not.
3. **Respawn** — re-add a bot's ship to the sim at a spawn point after its death delay.
4. **Real-player death policy** — a real client is normally authoritative over its own death.
   Decide whether server-side (physics) combat may kill humans, and if so sync energy/death so it
   feels right, before enabling PvP damage against real players.
5. **Energy / bounty** — emit these (via `ExtraPositionData`) so clients see bot health.
6. **Navigation upgrades** — the graph is wired but has known follow-ups: JPS + D\* Lite behind the
   existing `INavigation` methods; precomputed region-adjacency so `AreConnected` doesn't rescan door
   tiles; real brick span/expiry from `world.Canonical.Bricks`; and `DoorDelay`/`BrickTime`/ship
   radius sourced from arena settings instead of the placeholder constants in `BotsModule`.
7. **Real brains** — the four modes (defend stationary flags, retrieve/defend moveable flags, ball,
   team/solo combat) built on `INavigation` + steering. They share `NavSeekBrain`'s path-following;
   each adds goal selection (defended point, moving flag/ball, firing position behind an enemy).
8. **Steering** — replace `NavSeekBrain`'s minimal face-and-thrust with arrive/braking behaviour and
   physics look-ahead (clone + step the sim a few ticks to reject a thrust that would hit a wall).
