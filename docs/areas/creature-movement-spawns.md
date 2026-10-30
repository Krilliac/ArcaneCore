# Creature movement, spawns and respawn (wave 4 lane "creature-movement-spawns")

Fidelity work on how creatures move, how their splines reach the client and how spawns live, die and come back. Everything is
checked against the real references (vmangos primary, mangos-classic, wow_messages, classic-db) and every non-retail
behaviour sits behind `Creatures:*` configuration that defaults to retail. Nothing here copies reference code or data.

Companion docs: `docs/areas/creatures.md` (the base creature system), `docs/areas/creature-ai.md` (AI, evade, leash).

## Delivered

### 1. SMSG_MONSTER_MOVE offsets and walk/run mode (slice `monster-move-and-walk-mode`)

* Intermediate spline points are now written as `destination - point` (11/11/10-bit quarter-yard pack), not
  `middle - point`: vmangos `Movement/spline/packet_builder.cpp:77-111` (`offset = destination - real_path[i]`),
  mangos-classic `Movement/packet_builder.cpp:98-125` (`destination - pathPoint[i]`).
  Each axis is rounded to the nearest quarter yard, halves away from zero (`CreatureMovePackets.PackXYZ`, vmangos
  `ByteBuffer::appendPackXYZ` `lroundf(v * 4)`, ByteBuffer.h:482-489); it was truncated toward zero, an error of up to 0.25 yd that always
  pulled the point toward the destination's height (now at most 0.125 yd and unbiased).
* An offset under 0.25 yd on every axis is nudged on z (+0.51 when z is below zero, else +0.26): "the client freezes when it
  gets a zero offset" (`packet_builder.cpp:99-106`). mangos-classic drops such points instead (`:104-107`); vmangos is followed
  because the packet's point count then stays equal to the spline's.
* Every spline launch syncs the creature's walk mode: a run spline clears `MOVEFLAG_WALK_MODE`, a walk spline sets it, and a
  *change* sends `SMSG_SPLINE_MOVE_SET_RUN_MODE` (0x30D) / `SMSG_SPLINE_MOVE_SET_WALK_MODE` (0x30E), body = packed guid, to
  the observers before the monster move (vmangos `MoveSplineInit.cpp:109-112`, `:173-176`; wow_messages
  `world/movement/smsg/smsg_spline_move_set_walk_mode.wowm`). A repeat of the same mode sends nothing.
* `Unit.Relocate` keeps `WalkMode` (it is server-decided for creatures and every spline step relocates the creature).
* Config: `Creatures:Movement:MonsterMoveOffsetBase` = `Destination` (retail, default) | `Midpoint` (legacy, rollback only).

Verification: unit tests derive the expected words by hand from the reference layout and never use the production
unpacker as the oracle (`MonsterMovePacketFidelityTests`). **Unit-verified against reference bytes only; no 1.12.1 client has
confirmed multi-point splines or the toggle packets.**

Limits: `MOVEFLAG_SPLINE_ENABLED | FORWARD` are not maintained on the creature's movement block (the create block clears
`SplineEnabled` anyway, `UpdateBlockWriter.cs`); a late observer still gets a one-tick catch-up move instead of vmangos'
live spline inside the create block (`packet_builder.cpp:152-200`).

### Interaction pause (lane `vendor-hates-you`, 2026-10-19)

A player talking to a creature stops it: `Creature.PauseOutOfCombatMovement` (vmangos `Creature::PauseOutOfCombatMovement`,
`Movement/MotionMaster.cpp:947-969`) does nothing in combat; when the current generator is the random wander or a waypoint path it stops
the spline where the creature stands and holds the next leg for `NPC_MOVEMENT_PAUSE_TIME`, 180 s (`Objects/CreatureDefines.h:222`;
`AddPauseTime` keeps a longer wait already running, `RandomMovementGenerator.h:51-55`, `WaypointMovementGenerator.h:89-93`). A waypoint
walker stopped mid-leg sets off for the same node afterwards. When a generator above the waypoint path ends (an evade's way home, a
distraction, a scripted point) the path resumes at once as vmangos `Reset` → `StartMoveNow` does (`WaypointMovementGenerator.cpp:108-125`,
`.h:105-109`): the pause, or what is left of a node delay, is dropped, and a creature that had arrived at a node goes on to the next one. Chase, follow, point, formation-slot and other movement is left alone. The
NPC handlers call it through `QuestNpcServices.PauseInteractionMovement` after the interaction check passes: gossip hello and questgiver
hello, gossip option select, the vendor list and battlemaster hello (`Handlers/NPCHandler.cpp:357-358,395-396`,
`Handlers/QuestHandler.cpp:93-95`, `Handlers/ItemHandler.cpp:716-718`, `Handlers/BattleGroundHandler.cpp:50-52`). A template with the
vmangos-dialect `NO_MOVEMENT_PAUSE` extra flag (0x20, `CreatureDefines.h:162`) keeps walking; the bit means RUN_DURING_WANDER in the
cmangos dialect, so cmangos-imported creatures always pause. Before this a walking vendor left `INTERACTION_DISTANCE` while its window was
open and the next sale got `SELL_ERR_CANT_FIND_VENDOR`, "That merchant doesn't like you." (live report 2026-10-19; see
[vendors and trainers](vendors-trainers.md)). Tests: `Npc/VendorMovementPauseTests.cs`.

### 2. Random wander parity and generator type numbers (slice `random-wander`)

* `RandomMovementGenerator` moved to `Movement/RandomMovementGenerator.cs` (no behaviour change to its timing: 1 s first move, 50 ms
  steps, urand(4,10) s pauses, urand(0, wander<=1 ? 2 : 8) steps; `Movement/RandomMovementGenerator.cpp:56-76`).
* Legs run only for ALWAYS_RUN (`:53`), now read through the dialect table (`Template.Behaviour`), not raw `ExtraFlags & 0x40`: the
  same bit is meaningless in the cmangos dialect, so cmangos-imported creatures walk. The raw constant `Creature.ExtraFlagAlwaysRun`
  is gone; waypoint legs use the same decoded flag.
* cmangos RUN_DURING_WANDER (0x20 in the cmangos dialect only; vmangos' 0x20 is NO_MOVEMENT_PAUSE): a per-leg draw
  `urand(0,99) < Creatures:Movement:RunDuringWanderChancePercent` (default 15; cmangos `RandomMovementGenerator.cpp:135-136`).
* `GetResetPosition` (`:131-144`): the creature's own position when within the wander distance of its spawn point, else the spawn
  point. Evade already consults the default generator (`CreatureMapSystem.Evade.cs`), so a wanderer evading from inside its disc no
  longer runs back to the spawn point.
* UpdateAsync gates (`:113-128`): stunned/rooted/confused/fleeing zero the move timer and start no leg; casting stops the creature and
  freezes the timer.
* `MovementGeneratorType` numbers follow `Movement/MotionMaster.h:36-59` (Confused 5, Chase 6, Home 7, Point 9, Fleeing 10, Follow 15); the
  values are internal (never persisted or sent; grep of casts found none).

* **Wander and flee points are chosen on the navmesh** (lane `w44-path-mesh-points`). `NavMeshPathfinder` implements `IMeshPointQuery`
  (`Maps/Collision/CollisionContracts.cs`; reached through `map.Collision.MeshPoints`, which is null for a map without a navmesh).
  `TryGetRandomPointAround` meets the contract of Detour's `findRandomPointAroundCircle` but is this port's own algorithm: it gathers the
  polygons reachable from the one under the centre across edges that touch the circle (loaded tiles only, at most 256), draws a point
  uniformly over the disc (two `Random` draws per attempt), and accepts it when one of those polygons contains it; a point over a hole or
  over an unconnected polygon is drawn again, up to 16 times, then the answer is the start position. Drawing over the disc rather than over
  the polygons (as Detour does, with a rejection step that is also this port's addition) keeps a 1-3 yd wander radius on 12 yd polygons spread
  over the disc instead of piling creatures up at the spawn. `Raycast` is Detour's `raycast` along the mesh surface
  (`NavMeshQuery.TryRandomPointAround` / `NavMeshQuery.Raycast`). Both take the creature's `Random` (a seed fixes the result), build no
  collection per call (per-thread scratch lists; `NavMeshPointQueryTests` measures the allocation), read no tile, never throw, and answer
  "unavailable" (no mesh for the map, tile not loaded, centre more than 3 yd from a polygon) without drawing any random numbers.
  Wander (`RandomMovementGenerator`) takes the point from `ICreatureMover.TryGetRandomMeshPoint` (around the spawn point, within the wander
  distance, steep slopes excluded as vmangos `MOVE_EXCLUDE_STEEP_SLOPES`) and walks to it with `PlanPath` + `MovePath`, whose search keeps
  off steep slopes too (`MoveSplineInit.h:129-130`; confused and flee legs the same, see [collision-pathing.md](collision-pathing.md)), so a townsfolk or
  guard no longer cuts through a house. That path search counts against the per-tick creature path budget; on exhaustion the leg waits
  1-1.5 s (4-10 s in a crowded wander pass, more than 256 wandering creatures in the update) and tries again (see "No path, no straight line" below). Flee (`FleeingMovementGenerator.MoveAway`) casts the leg
  with `ICreatureMover.TryRaycastMesh`, capped at 30 yd (`MeshFleeCap`), so it ends where the mesh ends or a steep slope or wall begins
  (vmangos `Map::GetWalkHitPosition` from `FleeingMovementGenerator.cpp:108`); a leg that would be shorter than 1 yd (a wall right in
  front) is not issued: another direction is drawn, up to 4 times, and if all are blocked the creature stays and tries again after its
  pause. When the query is unavailable both keep the previous point choice (the floor-height point, terrain or a static model) and send it through `PlanPath`, which is the straight line only where there is no mesh data (`Direct`, see "No path, no straight line" below). Off the mesh a wander point whose ground is more than 7 yd from the spawn's z keeps the spawn's z, and a wander leg whose path ends more than 7 yd below or above both the creature and the spawn is skipped like a leg without a path (`MaxFallbackLegDropYards`: a cave or city floor under the terrain); flee has no such bound. Apart from that both keep the order
  of the random draws, so meshless maps and unloaded tiles behave as before. What a creature does when the mesh has no path is
  in "No path, no straight line" below.
  Where this differs from vmangos `Map::GetWalkRandomPosition` (`Map.cpp:3332`): vmangos takes Detour's random point only as a
  direction, walks `maxRadius * rand` along it, raycasts, steps 0.5 yd back along the hit normal, snaps to the closest point on the last
  polygon and lifts Z to the vmap height when that is higher; this port uses the sampled on-mesh point itself (at the mesh height, no vmap
  lift, no step back from walls). Tests: `Collision/NavMeshPointQueryTests.cs` (synthetic tiles; the raycast is compared with DotRecast on
  seeded mazes), `Collision/NavMeshPointQueryRealTerrainTests.cs` (Goldshire inn, `ARCANECORE_TEST_TERRAIN_DIR`),
  `Creatures/MeshPointMovementTests.cs` (a wander circle and a flee line that contain a house-sized hole).

Limits: wander and flee choose on the mesh only where a navmesh tile is loaded (vmangos the same), and only for walkers: the query excludes nothing but steep slopes, so a swimming creature (which vmangos filters by `moveAllowedFlags`) has no water-only choice and may pick shore or water points alike; no flying circle path (`:28-44`: needs
the Flying spline flag plumbed through `ICreatureMover.MovePath`); no timed random / pause-time API (nothing consumes it until waypoint
node wander exists); vmangos' `Interrupt`/`Finalize` walk-mode reset (`:83-93`) is not sent separately because every spline launch
already syncs the mode.

### Fear, confuse and polymorph movement (lane `L1-fear-confuse-movement`)

* **Hook.** Each alive creature tick reads `UnitFlags.Fleeing` / `UnitFlags.Confused` (set from the live auras by `CcState.RefreshFear`)
  before the motion update (`CreatureMapSystem.SyncCrowdControlMovement`): two flag tests when neither is set. A flag with no generator
  pushes one on the `MotionMaster` stack (`SyncCrowdControl`); a generator with no flag is removed and the generator beneath resumes
  (chase, follow, random, waypoint). The spell system never calls into the map. Both flags: fear wins, confuse starts when it ends.
* **Fear** (`CrowdControlFleeingMovementGenerator`, `Type = Fleeing`) wraps the existing `FleeingMovementGenerator` with no duration:
  legs run away from the fear source (the caster of the latest fear aura, remembered by `CcAuraHandlers` in `CcState.FearSource`; a
  creature that feared itself, or whose caster left the world, flees in a random direction), 0.5-1 s pause between legs. Unlike the
  plain flight it does not own the flag: starting, resuming, interrupting it or clearing the stack (evade) leaves `Fleeing` as the auras
  had it (a point pushed after the aura ended, before the hook ran, must not stick the flag back on), and the hook starts the flight again
  while the flag holds. Removed while buried under a pushed generator, it leaves that generator's spline running.
* **Confuse / Polymorph** (`ConfusedMovementGenerator`, new `MovementGeneratorType.Confused = 5`): a Polymorph carries a ModConfuse
  aura, so it staggers like any confuse. A walk to a random point within 10 yd of where it was confused, a new point every 800-1500 ms
  even mid-leg (mangosserver `MotionGenerators/ConfusedMovementGenerator.cpp`: `STAGGER_RADIUS`, `STAGGER_INTERVAL_*`, `MOVE_WALK`). The
  anchor survives an interruption.
* **Rooted / stunned.** Both generators stop the spline and wait while the creature is stunned or has `MovementFlags.Root`. A creature's
  root flag is now set and cleared by `CcState.RefreshRoot` from its root and stun auras (it was players only); this also stops
  random and waypoint movement of rooted creatures through `CreatureMovementGates`. `ApplyCombatMovement` no longer pushes a chase over a
  confused creature.
* Tests: `tests/ArcaneCore.Game.Tests/Creatures/FearConfuseMovementTests.cs` (synthetic map, no navmesh, flags set directly plus the aura
  side through `SpellTestKit`).

Limits: no `SMSG_FORCE_MOVE_ROOT`-style packet is sent to observers for a rooted creature, and whether the 1.12.1 client needs one is
UNVERIFIED (the server only stops the spline and sends the stop packet); a fear aura that lands while a critter's own timed flee is on
top is absorbed by it (the plain flight clears the flag when it ends); a stale fear source (left the map after the flight started) is not
re-read each leg; the stagger point has no navmesh reachability test; the 10 yd stagger radius is the mangosserver reference value and
was not compared with a 1.12.1 client capture (UNVERIFIED). Fear on players is not a creature generator and is not covered here.

### 3. Respawn delay and corpse decay (slice `respawn-core`)

* The respawn delay `urand(spawntimesecsmin, spawntimesecsmax)` is drawn **once per creature object** when it is created from its
  spawn row and reused at every death, as vmangos does (`m_respawnDelay = data->GetRandomRespawnTime()`, `Objects/Creature.cpp:1963`;
  `SetDeathState` reads it, `:2246`). A grid unload/reload creates a new object and so draws again, as in vmangos. Before this change
  every death drew afresh. `Creatures:Respawn:DrawDelayAtLoad=false` restores the old behaviour.
* Corpse decay is by rank only (`Creature.cpp:1326-1343`: Corpse.Decay.NORMAL/RARE/ELITE/RAREELITE/WORLDBOSS). A template's
  `CorpseDecay` column is a cmangos concept (14 templates in classic-db) and is ignored unless
  `Creatures:Respawn:HonorTemplateCorpseDecay=true`.
* `Creature.OnAllLootRemoved` is vmangos `AllLootRemovedFromCorpse` (`Creature.cpp:3355-3401`): skinned corpse 0; else
  `Rate.Corpse.Decay.Looted` x corpse delay, or (retail default 0, `mangosd.conf.dist.in:1542`, cmangos `World.cpp:457`) a third of the
  respawn delay; a respawn delay above the corpse delay always takes the looted delay, a shorter one only when it is shorter than the
  time left; a respawn time that has already passed removes the corpse at once. `LootOptions.LootedCorpseDecayRate` now defaults to 0
  (it was 0.5, which is not a vmangos or cmangos value); the loot service's one call site delegates to this method.

Not delivered here (documented limits): spawn flags (`RANDOM_RESPAWN_TIME` x urand(90,110)/100, `DYNAMIC_RESPAWN_TIME`, `DEAD`,
`DISABLED`, ... `ObjectDefines.h:127-134`) because ArcaneCore's spawn rows carry no flags column and importing one needs the path-data
schema module (slice `waypoint-path-data`, not done); the config-driven dynamic respawn formula (`Creature.cpp:2703-2783`, off by
default in vmangos: `DynamicRespawn.Range=-1`); `ForcedDespawn`; persistence of respawn timers across restarts (`creature_respawn`,
needs a Characters schema module).

### 4. Evade no longer heals (slice `evade-home-health`)

* vmangos `CreatureAI::EnterEvadeMode` (`AI/CreatureAI.cpp:323-346`) never sets health or mana. The creature, now out of combat, gets
  them back through `Creature::RegenerateAll`: a third of the maximum every 5 s (`Objects/Creature.cpp:1087-1100`, `:1127-1160`,
  `:1122` for mana; ArcaneCore's `MapCombat.UpdateCreatureRegen` already implements that cadence). `EnterEvadeMode` used to set both
  to the maximum at once.
* `Creatures:Movement:EvadeRestoresFullHealth=true` restores the old instant reset (not retail).

Limits: a Home leg whose mesh path is missing or partial teleports home (see "No path, no straight line" below); `RemoveAurasAtReset`, the low-health aura-state
reset (`:39-42`) and `LoadCreatureAddon(true)` on arrival (`:94`) are not done (aura lane / addon reload).

### 5. Waypoint paths by entry, and the waypoint generator (slice `waypoint-path-data` + `waypoint-generator-core`)

Data (World schema version **23**, `CreatureMovementTemplateDataModule.Version`; allocated as 21 in the lane, renumbered at wave-4 integration):

* New table `creature_movement_template` (`Entry, PathId, Point, X, Y, Z, Orientation, WaitTimeMs`, key `(Entry, PathId, Point)`),
  imported from the classic-db layout (`Entry, PathId, Point, PositionX/Y/Z, Orientation, WaitTime`) and loaded into
  `CreatureContent` with the other definitions (so `.reload` swaps it with them). classic-db has 15,402 such rows on 544 paths of
  479 entries, and **319 of its 2,898 waypoint spawns (11.0 percent) have no `creature_movement` rows of their own**: they idled
  before and now walk their entry's path.
* `CreatureContent.ResolveWaypointPath(spawnGuid, entry)` is mangos-classic `WaypointManager::GetDefaultPath`
  (`MotionGenerators/WaypointManager.h:69-93`) and vmangos `Movement/WaypointManager.h:77-93`: the spawn's own rows win, else the
  entry's default path (PathId 0). Other path ids (51 entries use them) are stored and readable (`GetEntryWaypoints`) but only a
  script could select them. A summoned creature whose template has `MovementType 2` takes the entry path (case 2a of the header comment).
* Nodes are ordered by point id and never renumbered (ten classic-db paths have gaps).
* World schema 45 stores `ScriptId` on spawn and entry path nodes; reaching one starts its `dbscripts_on_creature_movement` steps
  with the map's DB-script scheduler. `Comment` is not stored. The importer counts scripted nodes and requires matching script rows.
* `ContentTableSpecs`/the content importer CLI count and report the new table.

Generator (`Movement/WaypointMovementGenerator.cs`, moved out of `CreatureMovement.cs`):

* Evade goes to the **last reached node** (`GetResetPosition`, `WaypointMovementGenerator.cpp:292-303`), or to the spawn point when no
  node was reached yet (`GetRespawnCoord`); it used to go to where the fight began. The generator then resumes at once (`Reset` →
  `StartMoveNow`, `WaypointMovementGenerator.cpp:108-125`, `.h:105-109`): the rest of a node delay is dropped; a creature interrupted
  mid-leg heads for the same node again, one that had arrived goes on to the next node.
  A reset position carries no orientation (vmangos sets facing only for the spawn point, `HomeMovementGenerator.cpp:54-65`): the home
  leg ends facing its travel direction.
* The AI is told of every arrival with the node's point id (`MovementInform(WAYPOINT, node)`, `:158-160`) before the delay starts.
* Legs walk unless the template has ALWAYS_RUN (`:240`). The per-node `Run` column (an ArcaneCore addition in neither classic-db nor
  vmangos) is ignored unless `Creatures:Movement:HonorWaypointRunColumn=true` (default false, retail).
* Legs go through the map's pathfinder (`:235 MOVE_PATHFINDING`; straight without navmeshes).
* Gates (`:249-272`): a stunned/rooted/confused/fleeing creature starts no leg and its timers stand still; a casting creature stops and
  sets off for the same node again when the cast ends.
* **No path at all** (neither the spawn's rows nor an entry path): both references leave the creature standing (vmangos
  `WaypointMovementGenerator::LoadPath`, `:47-52`). `Creatures:Movement:MissingWaypointPathFallback` (default `Random`, a deliberate
  deviation; `Idle` is retail) makes it wander within its spawn's wander distance (5 yd when the spawn has none) instead. The world
  logs the affected spawns once at load (`CreatureContent.FindWaypointSpawnsWithoutPath`: the count, the fallback and the first 20
  guids); the per-creature message is debug only. Against classic-db z2815, 7 of the 2,898 waypoint spawns have no path once
  `creature_movement_template` is imported (a database without that table reports all 326 spawns that lack their own rows).

Verification: `WaypointGeneratorTests` (map clock, explicit diffs, no wall time), `CreatureMovementTemplateTests` (importer, content,
upgrade from the previous schema version on every provider the machine has; **only SQLite ran locally, MariaDB/PostgreSQL run on hosted
CI**: the step is one `CREATE TABLE` with a composite integer key and float columns, no raw SQL).

Limits (not delivered, no stubs): node script execution, wander at a node (`wander_distance`), sub-paths (`path_id`), non-repeating
paths and `SetNextWaypoint`, `creature_movement_special`, vmangos-dialect
columns of `creature_movement` (`wander_distance`, `path_id`, `script_id`), cmangos `waypoint_path`/spawn-group formations (163
formation paths in classic-db), creature groups and linking. vmangos numbers nodes from 0 and treats the first reached node as "none"
(`m_lastReachedWaypoint`); this lane keeps the data's 1-based ids (see the generator comment).

### 6. Alternate spawn entries (slice `spawn-alternate-entries`)

Data (World schema version **24**, `CreatureSpawnEntryDataModule.Version`; allocated as 22 in the lane, renumbered at wave-4 integration): new table `creature_spawn_entry`
(`SpawnGuid, Entry`, key `(SpawnGuid, Entry)`). The importer reads the cmangos table and the vmangos `id`, `id2` ... `id5` columns
(a spawn with any non-zero `id2..id5` gets one row per non-zero id, `id` included; this replaces the earlier "id2 is reported, not
imported" warning). `CreatureContent.GetSpawnEntries(spawnGuid)` returns them ascending and distinct. It is spawn data, not part of the
swappable definitions.

Behaviour (cmangos `Creature::LoadFromDB` / `ResetEntry`, `Entities/Creature.cpp:1640-1660`, `636-655`; vmangos `Creature.cpp:830-841`,
`:1936-1944`):

* A spawn with rows becomes one of them, uniformly among the entries that have a `creature_template` (cmangos skips a row without one
  when it loads the table, `ObjectMgr.cpp:1853-1858`), when its grid loads **and again at every respawn**.
* The object keeps its GUID (the entry part is the one chosen at creation, as in both references); `InitializeFields` rewrites the unit
  fields from the new template, the AI is created afresh when the entry changed (vmangos CSTATE_INIT_AI_ON_RESPAWN, cmangos `AIM_Initialize`), and the
  client sees the new `OBJECT_FIELD_ENTRY` in the create block of the respawn (the corpse was removed, so it is a fresh object for the client).
* A spawn already loaded (it walked out of its home grid before the grid unloaded) is found under any entry its GUID may carry, so a
  re-loaded grid never adds a second object.
* A spawn none of whose entries has a template is skipped with one warning. Hot reload (`.reload creature_template`) counts such a
  spawn as orphaned only when none of its entries has a template any more.
* `Creatures:Respawn:AlternateEntries=false` ignores the rows (a spawn with `id = 0` then never spawns, as before).

Scale: of classic-db's 66,310 spawns, **2,802 have `id = 0`**. 2,234 of them (3.4 percent: 1,121 on map 0, 309 on map 1, 147 in map 209,
147 in map 90 ...) are resolved by `creature_spawn_entry`. 566 of the other 568 are resolved by cmangos spawn groups (`spawn_group_entry`,
world 44, 2026-10-08; [content import](content-import.md), "Spawn groups"). The last two have no entry in cmangos either.
`RealClassicDbDump_...` and `SpawnGroupDataTests` pin these figures.

Differences from the references, on purpose: cmangos uses `creature.id` when it is not 0 and rolls only at respawn; vmangos rolls at
load as well. One rule serves both dialects: a spawn that has rows always chooses among them (the 46 classic-db spawns with both an `id`
and rows lose nothing: the `id` is normally one of the rows). vmangos group entry limits (`creature_groups_entry_limit`) are not applied. Only SQLite ran locally for the store
tests; MariaDB/PostgreSQL run on hosted CI (one `CREATE TABLE`, composite integer key).

### 7. Durable respawn timers (slice `respawn-persistence`)

A restart no longer brings every dead rare and boss back to life (classic-db: 757 of 765 rares have a spawn-time range, bosses are mostly
604,800 s).

Data (Characters schema version **24**, `CreatureRespawnDataModule.Version`; allocated as 21 in the lane, renumbered at wave-4 integration): table `creature_respawn`
(`instance_id, spawn_guid, map_id, respawn_time` unix seconds, key `(instance_id, spawn_guid)`; vmangos `sql/characters.sql:472-480`
keys by guid and instance). `EfCreatureRespawnStore` upserts by EF update-or-insert (no `REPLACE INTO`, which PostgreSQL lacks), one
transaction per batch, explicit lower-case column names. `LoadAsync(now)` deletes (does not just skip) rows whose time has passed
(vmangos `MapPersistentStateMgr.cpp:1070-1100`) and rows of a dungeon instance whose `instance` row is gone (the loot-state guard
against a reused instance id). `EfInstanceStore.DeleteInstanceAsync` deletes the instance's rows with it. The module implements
`ICharacterDataCleanup` as a deliberate no-op (the rows are keyed by spawn and instance, never by character).

Behaviour (`CreatureMapSystem.RespawnPersistence.cs`, `ICreatureRespawnPersistence`/`IRespawnClock` seams, injected wall clock):

* A database spawn that dies saves `now + respawn delay` at once (vmangos `SaveRespawnTimeImmediately = 1`, `mangosd.conf.dist.in:397`;
  `Creature::SetDeathState`, `:2262-2263`); a world boss is saved at death whatever the option says. With
  `Creatures:Respawn:SaveImmediately=false` a normal creature is saved when it leaves the map (grid unload, instance unload) or at shutdown
  (`Map.cpp:1319-1322`): a respawn time still in the future is saved as it is, corpse or not; only a passed respawn time with a corpse left saves `now + delay + corpse time left`
  (`Creature::SaveRespawnTime`, `:2785-2794`).
* At load a pending time makes the creature dead for what is left (`Creature.cpp:1972-1989`); an expired one spawns it alive and deletes the row
  (`:1984-1989`). The row is also deleted when the creature respawns, naturally or by hand.
* Battleground maps keep their respawn times in memory only, as vmangos does (`MapPersistentState::SaveCreatureRespawnTime`,
  `Maps/MapPersistentStateMgr.cpp:84-86`: "BGs/Arenas always reset at server restart/unload"): nothing is saved there, and rows an older build left
  for a battleground instance id (ids are reused after a restart) are deleted when the instance's creature system starts, so a new match starts
  alive. A creature that will never come back (`Creature.RespawnNeverSeconds`, a battleground's RESPAWN_STOP) is never saved on any map: it has no
  respawn time, and "now + never" would be a row the load never deletes.
* The world side (`CreatureRespawnFeature`, `CreatureRespawnQueue`): reads answer from memory, writes are queued to one consumer off the world
  thread, in order, retried three times; the creature systems save what is still dead at shutdown (`StopAsync` after the world stopped) and the
  queue drains. Inactive without a store or with `Creatures:Respawn:Persist=false`.

Verification: `RespawnPersistenceTests` (fake persistence and clock), `CreatureRespawnQueueTests` (ordering, copy semantics, retry, drain), and
`CreatureRespawnStoreTests` (round trip, upsert replace, delete, expired and orphaned-instance purge, instance delete, upgrade from the previous
version with a re-run) in `AvailableProviders` theories: **only SQLite ran on this machine; MariaDB and PostgreSQL run on hosted CI.** The store
was written for their semantics (non-transactional MariaDB DDL: the step is one `CREATE TABLE`; PostgreSQL identifier folding: explicit
lower-case names; no `REPLACE INTO`; no advisory locks), but nothing here proves it until CI runs.

Limits: creature pools/linking that share dormant state are not implemented; the instance reset
path outside `EfInstanceStore.DeleteInstanceAsync` (an in-memory-only reset) is the instances area's; a crash between a death and its queued write
loses that one write (same window as vmangos' asynchronous character-database queue).

### 8. Population-scaled respawn (`World:Respawn:Dynamic`)

Why: a start zone with hundreds of players (Valley of Trials: about 258 bots against 64 boar spawns on a 120 s respawn) caps kill-quest
throughput at the respawn timer. Off by default (`Enabled = false`), which is retail 1.12.1. ArcaneCore's own design; the idea (not the
formula or the count) is TrinityCore's/AzerothCore's dynamic respawn rate. `Creatures:Respawn:DynamicRate` (TrinityCore zone-rate scaling,
`CreatureMapSystem.ScaleRespawnDelay`) stays as it was while this is off; with `Enabled` on, this replaces it so they never compound.

- **Formula:** `delay = base / (1 + ScalePerPlayer * max(0, players - PlayerThreshold))`, rounded up to a whole second, never below
  `base * (1 - MaxReduction)` or `MinimumSeconds`, never above `base`. A base at or under `MinimumSeconds` is left alone.
  Pure code: `DynamicRespawnOptions.ScaleDelay`.
- **Who counts:** players (not in GM mode: ArcaneCore has no GM invisibility state, so GM mode is the exclusion; ghosts do count) of the same map within `Radius` yards (2D) of the spawn's home position, counted once at death
  (`Map.CountPlayersWithin`: one grid query over the few 33-yard cells the circle touches plus an exact distance test, a reused scratch list,
  no allocation in steady state, no per-tick work). `CountBots = false` skips playerbots (`IPlayerSession.IsPlayerbot`). A radius was chosen
  over a zone count because contention is local to the camp, and a zone count needs either a walk over every player of the map per death
  (what `ScaleRespawnDelay` does) or counters kept on every zone change.
- **Creatures** (`CreatureMapSystem.OnCreatureDied`, `ScheduledRespawnDelay`): database spawns on open-world maps. Normal rank only, plus
  elite, rare-elite and rare with `IncludeElitesAndRares`; world bosses never. Never scaled: dungeons, raids, battlegrounds and every
  instance copy; summons; spawn-group, pool and script-only members; creatures with a `creature_linking` master/slave event; a delay a
  script (`RespawnDelayOnceSeconds`) or a battleground (`RespawnDelayOverrideSeconds`) chose.
- **Game objects** (`GameObjectMapSystem.Despawn`, `ScaleRespawnDelayMs`), with `IncludeGameObjects`: herb and mineral nodes (a chest whose
  lock has a herbalism or mining skill case) and quest objects (a chest with a quest or quest loot, a goober with a quest). The delay is scaled both when the object despawns and when a pool brings a member in (`IPoolHost.SpawnMember`), counting the players around the member that will spawn. This is deliberately asymmetric with creatures: creature pools hold rare spots and stay unscaled, game object pools are the rotating herb and ore nodes whose contention this solves. Never: other
  objects, spawn-group members, durable instance chests. Object respawn times are stored in `gameobject_respawn` (characters schema 68,
  `GameObjectRespawnDataModule`, vmangos `MapPersistentState::SaveGORespawnTime`): saved when the object despawns, restored when its grid or
  instance map loads, deleted when it respawns or its instance is removed. Not stored: negative `spawntimesecs`, pool and spawn-group members,
  battleground maps, and durable instance chests (their consumed state is in `loot_state`). The persistence follows the creature feature
  (`GameObjectRespawnFeature`, `GameObjectRespawnQueue`), without a `Persist` switch: it is on whenever the characters database is present.
- **Persistence:** `RespawnAtMs` is set to the scaled time before `SaveRespawnOnDeath`, so `creature_respawn` stores the scaled time and a
  restart does not reset it. No schema change.
- **DynamicRate:** when `World:Respawn:Dynamic:Enabled` is on, `Creatures:Respawn:DynamicRate` no longer applies to any creature, including the ones this feature excludes (elites when `IncludeElitesAndRares` is off, pool and spawn-group members, scripted delays); with it off, `DynamicRate` runs exactly as before.
- **Live:** every key is in `WorldConfigKeys` (`.reload config`, validated: out-of-range values are refused; a bad start-up value turns the
  scaling off and logs a warning). A death uses the values in force at that moment.
- **Corpses:** a corpse is removed when its respawn time is reached. The code has no separate corpse-decay minimum, so `MinimumSeconds` (default 30) is the floor: a scaled respawn is never earlier than 30 s after death by default. Lowering it below the time players need to loot cuts loot short.
- **GM:** `.spawninfo dynamic` (GameMaster) prints whether it is on, the players counted at your position, the multiplier, and example delays.
- **Not done:** a respawn already scheduled is not re-evaluated when more players arrive later (the count is taken once, at death).

### 9. No path, no straight line (lane `w44-path-no-straight`)

Every creature path is `CreatureMapSystem.PlanPath`, which says how the search ended (`PathOutcome`: `Mesh`, `Direct`, `Partial`, `NoPath`,
`Deferred`) instead of quietly substituting a straight line. `Direct` is the pathfinder's answer for a map with no mesh, an endpoint on a tile
that is not loaded, or pathing off (vmangos `PathFinder.cpp:86-105`, `NORMAL | NOT_USING_PATH`); that straight line is legitimate. The old
comment that "vmangos goes straight on NOPATH" described only the authored-route case below.

| Motion | `NoPath` | `Deferred` (per-update search budget spent) |
|---|---|---|
| Chase, follow, pet follow | no move; `IsReachable` false; re-asked every 100 ms; the unreachable-target timer evades (3 s soft, 24 s home) (`TargetedMovementGenerator.cpp:188-189`) | keeps its spline, re-paths next update (as before) |
| Partial path (`Incomplete`) | walked to the point nearest the target, then stands; unreachable as before | - |
| Flee / fear, confused | waits 1-1.5 s, then a new point (`FleeingMovementGenerator.cpp:60-63`, `FearMovementGenerator.cpp:43-46`; confused: ArcaneCore's choice, vmangos ConfusedMovementGenerator.cpp:72-86 checks no path) | retries next update |
| Home / evade | teleport home (`NearTeleportTo`, `HomeMovementGenerator.cpp:58-71`); a partial path teleports too; `Direct` runs | retries; teleports after 3 s |
| Random wander | the leg is skipped, a new point 1-1.5 s later, doubled for each further leg in a row without a path (at most 16-24 s, `RandomMovementGenerator.NoPathRetryDelay`) | skips mesh/height sampling when `HasPathBudget` is false and no path job can be queued, and retries 1-1.5 s later (4-10 s in a crowded wander pass); a leg queued on the path-job pool asks again after 100 ms |
| Waypoint, point, formation, scripted `MoveTo` (authored) | straight line, as vmangos (`MoveSplineInit::Move(PathFinder*)` takes the shortcut of a NOPATH finder); counted in `DirectFallbackPaths` | retries next update (scripted moves wait in a per-map queue served first in the next update) |
| Charge (spell) | a NON-triggered cast fails with SPELL_FAILED_NOPATH when the caster is on the ground (not in water, within 1.6 yd of the floor) and the path to the target is NoPath or Incomplete (vmangos Spell.cpp:5909-5934); both the caster's own position and the target are located by `FindWalkPoly`, so a caster up to 5 yd beside the mesh passes and one on a floor more than 10 yd above the mesh fails SPELL_FAILED_NOPATH. Unlike vmangos, which runs CheckCast for triggered casts too but tests NOPATH only under strict, a triggered charge skips the check; it then makes no move on NoPath (PointMovementGenerator.cpp:237-238). A caster in water, or a swimmer charging a target in water, is exempt from the check AND gets the straight line from `ChargePath` (vmangos' swimming shortcut, PathFinder.cpp:197-201): both read `ForcedMovement.ChargeSwims`, so a cast that passes always launches a spline. A whole path that ends short of the contact point (target 0.5-7 yd off the mesh) is walked to its end, then straight on only with line of sight. The path uses the caster's locomotion (player mover, creature InhabitType) | not budgeted |

A spent budget never walks a creature straight. `CreatureMapSystem.UpdateCreatures` runs non-`Random` movement generators (`Chase`, `Home`, `Fleeing`, `Confused`, `Waypoint`, `Point`, `Follow`, `Formation`) in pass 1 before idle `Random` wanderers in pass 2 so combat and evade return paths claim the per-tick path budget first, and `Random` / `Fleeing` / `Confused` verify `ICreaturePathQuery.HasPathBudget` before sampling navmesh points or floor height. `CreatureMapSystem.MoveTo` (scripts) now paths; `MoveStraight` is the raw spline.
Observability: `CreatureMapSystem.NoPathPaths / DirectFallbackPaths / DeferredPathSearches / HomePathTeleports` and the process-wide
`CreaturePathStats` (the slow-map-update line prints the per-map counts; metrics `arcanecore.creature.path.*`, docs/ops/metrics.md).
Every creature search carries the creature's `PathMover` (`InhabitType`), so a flier crosses a hole in the mesh as a flying shortcut instead of standing on NoPath. A return home uses `PathOptions.EndTolerance` = 7 yd (vmangos `farFromPoly`, PathFinder.cpp:209-235), which searches both the creature's own position and its spawn point with `FindWalkPoly`: from up to 5 yd beside the mesh (within 7 yd 3D) and to a spawn point up to 5 yd off the mesh whose polygon the corridor reaches it runs home; from more than 7 yd from its polygon (`Incomplete`), more than 10 yd above the mesh, or more than 3 yd under a polygon (`NoPath`), or when the corridor does not reach the spawn's polygon, it teleports home (`HomeMovementGenerator.cpp:58-71`). The shared classification (0.1 yd) is unchanged for other callers.
A pet swims and never flies whatever its template says (vmangos Creature.h:166-167: CanSwim = IsPet || water bit, CanFly = !IsPet && air bit); `PathMover.FromInhabit(inhabit, isPet)` and the per-creature option tables (indexed by InhabitType bits plus a pet bit) follow that. Side effect of giving creatures their own mover: a creature with InhabitType 1 (ground only) cannot path through water polygons, so a target standing in deep water is unreachable for it (it stands, then evades); only swimmers and pets reach it. The end-tolerance search (for both start and end) is vmangos `FindWalkPoly`: 5 yd around and 10 up and down, a polygon more than 3 above the point refused, 3D distance past 7 yd is partial.
Not copied: vmangos' pet closed-door `CutPathWithDynamicLoS` and `UNIT_STATE_ALLOW_INCOMPLETE_PATH`; the in-LOS stop of a partial chase
(the creature keeps walking the partial path). Maps without a mesh: `WorldCollision.InstallFallback` has no terrain-step `IPathfinder` to
install (only the seam exists), so they keep the straight line.
* **Spline smoothing, lateral variation and continuous orientation tracking (`CreatureSplineSmoothing`, `CreatureSpline.PositionAndOrientationAt`)**:
  Multi-corner `PathOutcome.Mesh` paths returned by `CreatureMapSystem.PlanPath`, except a non-home path of an out-of-combat creature whose current motion is `Random` (a wander leg), are post-processed by `CreatureSplineSmoothing.VaryAndSmooth`:
  1. **Per-creature lateral corridor variation**: on non-home paths `>= 12` yd of an out-of-combat creature with `>= 2` corners, interior corners receive a deterministic lateral offset (`±0.85` yd seeded from `creature.Guid.Counter`, tapered to `0` at start and destination) when `IMeshPointQuery.Raycast` and floor height (`<= 0.85` yd delta) confirm the shifted vertex is walkable.
  2. **Quadratic Bezier corner-cutting**: interior turns `>= 15°` with arms `>= 2` yd are replaced with 3 quadratic Bezier points (`u = 0.20, 0.50, 0.80`, arm `min(3.5, 0.38 x the shorter leg)` yd; a corner whose arm would be under `1.5` yd is not cut) validated by `IMeshPointQuery.Raycast` and height lookup, capped at `32` spline points and spaced `>= 1.1` yd apart.
  3. **Ground snap** (`GroundHeight.DrapePath`, `CreatureMapSystem.FinishPlannedPath`, docs/areas/collision-pathing.md "Ground snap"): after the above, every finished path of every outcome (mesh, partial, straight shortcut; a no-path `FindPath` straight line too) has its points put on the floor (terrain or model, +0.05 yd) and is sampled every `1` yd, keeping a sample only where the chord of the points kept before it would miss the ground by more than `0.11` yd where the floor is terrain and `0.40` yd where it is a model or the mesh (WMO ramps). The drape's time counts in the per-update path budget, and a finished path-job result is applied only while that budget has room (otherwise it waits for the next update, oldest creature first, and is not lost). Walkers and swimmers only: a flier keeps the mesh's heights, a water-only creature stays in its water. This replaced the raise-only `DrapeCornersOverMeshFloor` of the wander and the chase/home generators.
  4. **Live segment orientation & corner blending**: `Creature.AdvanceSpline` and `StopSpline` update `creature.Orientation` at every tick via `CreatureSpline.PositionAndOrientationAt` (which blends heading across interior corners within a 1.25-yd window and finishes on `EndOrientation` when `FinalOrientation` is null).
Tests: `Creatures/NoStraightPathTests.cs`, `Creatures/CreatureSplineSmoothingTests.cs`, plus the updated `UnreachableTargetTests`, `CreaturePathBudgetTests`, `ChargeEffectTests`.

## Not done in this lane (recorded, not stubbed)

* **Spawn flags** (`RANDOM_RESPAWN_TIME` x urand(90,110)/100, `DYNAMIC_RESPAWN_TIME`, `DEAD`, `DISABLED`, `ACTIVE`, `EVADE_OUT_HOME_AREA`, ...; `ObjectDefines.h:127-134`) and
  the config-driven dynamic respawn formula (`Creature.cpp:2703-2783`, off by default in vmangos): no column carries them yet.
* **Formations, linking, pools, patrol** (`CreatureGroups.cpp`, `CreatureLinkingMgr`, `PoolManager`): cmangos spawn groups are imported
  and run (member count, entry choice, aggro and respawn together, and since 2026-10-09 their 164 formations: the leader walks the
  formation's `waypoint_path`, linear or looping, and the others hold their slot; [content import](content-import.md), "Spawn groups"),
  and so are the pools ("Pools": max_limit members, rolled again when one despawns, nested pools). Creature linking is not implemented.
* **Stuck/unreachable evade** belongs to the threat-and-aggro lane (`Creature.cpp:~998-1043` sits beside its leash code); leash radius, 3 s leash checks and
  `NO_LEASH_EVADE` already exist on the base.
* ~~Home-leg teleport fallback~~ (done, see "No path" below); `RemoveAurasAtReset`,
  addon reload on arrival.
* **Wander at nodes, sub-paths, non-repeating paths** (`WaypointMovementGenerator.cpp:128-242`; node scripts run since world 45), flying
  wander circles, the spline in the create block (`packet_builder.cpp:152-200`), a GM `.wp show`/`.creature movement` inspection command.
* **Real-client verification** of the destination-relative spline offsets and the walk/run toggle packets. Both references agree, so the retail layout is the
  default, but the only oracle in the tests is bytes derived by hand from the references.

## References used

Active-cell reactivation (2026-10-11): a skipped creature's spline excludes inactive map time. Position interpolation, completion,
observer catch-up packets and stopping all use that same adjusted time. Returning players no longer make paused patrols complete
their current leg and launch the next waypoint path in a single burst. This follows vmangos `Unit::Update` / `UpdateSplineMovement`
(`Objects/Unit.cpp:322-323,10632-10637`): the spline receives the active tick delta, not the elapsed time since the creature's last visit.
Corpse decay and absolute respawn deadlines retain their separate clocks. Active objects, formations and camera viewers still take
the existing always-update path. Tests and performance limits are in the [reactivation report](../integration/active-cell-reactivation-20261011.md).

vmangos: `Movement/spline/packet_builder.cpp`, `Movement/MoveSplineInit.cpp`, `Movement/MotionMaster.h`, `Movement/RandomMovementGenerator.cpp`,
`Movement/WaypointMovementGenerator.cpp`, `Movement/WaypointManager.{h,cpp}`, `Movement/HomeMovementGenerator.cpp`, `AI/CreatureAI.cpp`, `Objects/Creature.cpp`
(`:817-884`, `:1087-1161`, `:1318-1343`, `:1925-2004`, `:2242-2264`, `:2785-2794`, `:3305-3401`), `Maps/Map.cpp:1310-1365`, `Maps/MapPersistentStateMgr.cpp:80-101`,
`sql/characters.sql:472-480`, `mangosd.conf.dist.in:397,1468-1478,1526,1537-1542`. mangos-classic: `Movement/packet_builder.cpp:60-125`,
`MotionGenerators/RandomMovementGenerator.cpp`, `MotionGenerators/WaypointManager.{h,cpp}`, `MotionGenerators/WaypointMovementGenerator.cpp:35-80`,
`Entities/Creature.cpp:1595-1660,636-655,3025-3055`, `Globals/ObjectMgr.cpp:1826-1869`, `World/World.cpp:457`. wow_messages: `smsg_spline_move_set_walk_mode.wowm`.
classic-db z2815: the `creature`, `creature_movement`, `creature_movement_template`, `creature_spawn_entry`, `spawn_group_*` tables (figures pinned by the
`RealClassicDb_*` tests, which run when `ARCANECORE_CLASSICDB_DUMP` points at the dump). Nothing was copied into the repository.
