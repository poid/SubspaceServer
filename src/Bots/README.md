# Bots — server-side AI players

A plugin that spawns **fake players driven by the QS physics engine**, so people can play
against server-side bots without any external bot programs. Because the bots live inside the
server they add no network traffic and have full, authoritative knowledge of every player.

Status: **scaffold**. The pipeline is wired end-to-end and the solution builds, but no bot
actually simulates yet — the physics world can't be built until the settings/map/collision
bridges in [`PhysicsWorldProvider`](PhysicsWorldProvider.cs) are implemented (see *Remaining
work* below).

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
| [`IBotBrain.cs`](IBotBrain.cs) / [`Brains/OrbitBrain.cs`](Brains/OrbitBrain.cs) | Pluggable AI; the example brain just flies in a loop. |

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
   <module type="SS.Bots.BotsModule, SS.Bots" path="bin/modules/Bots/SS.Bots.dll" />
   ```
3. Attach per arena — in the arena's conf, add `Bots` to `Modules:AttachModules`.
4. In-game: `?spawnbot <name>` and `?killbots`.

Until `PhysicsWorldProvider` is implemented, `?spawnbot` reports that no physics world exists and
nothing simulates.

## Remaining work

1. **`PhysicsWorldProvider.CreateWorld`** — build the three inputs `ReplayController.Configure`
   needs, all of which the server already has:
   - `LevelArray` from the arena map (`IMapData` tile data),
   - `CollisionArenaSettings` from arena settings,
   - `GameSettings` (ship/arena/prize) parsed from the arena's Continuum client settings — without
     this, ships get zero thrust/recharge and won't move or fight.
2. **Velocity scale** — confirm the wire-`XSpeed`/`YSpeed` ↔ engine-velocity factor
   (`PhysicsAdapter.VelocityScale`, currently a placeholder) against the QS packet-ingestion code.
   The position (`1000`×pixels) scale is confirmed; velocity is not.
3. **Respawn** — re-add a bot's ship to the sim at a spawn point after its death delay.
4. **Real-player death policy** — a real client is normally authoritative over its own death.
   Decide whether server-side (physics) combat may kill humans, and if so sync energy/death so it
   feels right, before enabling PvP damage against real players.
5. **Energy / bounty** — emit these (via `ExtraPositionData`) so clients see bot health.
6. **A real brain** — replace `OrbitBrain` with AI that reads `world.Canonical.Ships`.
