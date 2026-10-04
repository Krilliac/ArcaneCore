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

Limits: no navmesh random point and no steep-slope exclusion (vmangos `MOVE_PATHFINDING | MOVE_EXCLUDE_STEEP_SLOPES`; the pathfinder
has no random-point query, so the point is uniform over the disc at the height provider's Z); no flying circle path (`:28-44`: needs
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
  plain flight it does not own the flag: interrupting it or clearing the stack (evade) leaves `Fleeing` as the auras set it, and the hook
  starts the flight again while the flag holds.
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

Limits: the Home leg still goes straight when the pathfinder finds no path (vmangos teleports with `NearTeleportTo`,
`HomeMovementGenerator.cpp:71-72`; no creature teleport primitive exists to reuse); `RemoveAurasAtReset`, the low-health aura-state
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
* `ScriptId` and `Comment` are not stored: no creature-movement script engine exists. The importer reports "N waypoint node(s) carry
  a ScriptId" (668 nodes of 182 scripts in classic-db) instead of dropping them silently.
* `ContentTableSpecs`/the content importer CLI count and report the new table.

Generator (`Movement/WaypointMovementGenerator.cs`, moved out of `CreatureMovement.cs`):

* Evade goes to the **last reached node** (`GetResetPosition`, `WaypointMovementGenerator.cpp:292-303`), or to the spawn point when no
  node was reached yet (`GetRespawnCoord`); it used to go to where the fight began. The generator then resumes the same leg.
  A reset position carries no orientation (vmangos sets facing only for the spawn point, `HomeMovementGenerator.cpp:54-65`): the home
  leg ends facing its travel direction.
* The AI is told of every arrival with the node's point id (`MovementInform(WAYPOINT, node)`, `:158-160`) before the delay starts.
* Legs walk unless the template has ALWAYS_RUN (`:240`). The per-node `Run` column (an ArcaneCore addition in neither classic-db nor
  vmangos) is ignored unless `Creatures:Movement:HonorWaypointRunColumn=true` (default false, retail).
* Legs go through the map's pathfinder (`:235 MOVE_PATHFINDING`; straight without navmeshes).
* Gates (`:249-272`): a stunned/rooted/confused/fleeing creature starts no leg and its timers stand still; a casting creature stops and
  sets off for the same node again when the cast ends.

Verification: `WaypointGeneratorTests` (map clock, explicit diffs, no wall time), `CreatureMovementTemplateTests` (importer, content,
upgrade from the previous schema version on every provider the machine has; **only SQLite ran locally, MariaDB/PostgreSQL run on hosted
CI**: the step is one `CREATE TABLE` with a composite integer key and float columns, no raw SQL).

Limits (not delivered, no stubs): node script execution, wander at a node (`wander_distance`), sub-paths (`path_id`), non-repeating
paths and `SetNextWaypoint`, the 30 s pause while a player talks to the creature, `creature_movement_special`, vmangos-dialect
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
147 in map 90 ...) are resolved by `creature_spawn_entry` and spawn now; the other **568 are resolved by cmangos spawn groups
(`spawn_group_entry`) and still do not spawn** (creature groups are not implemented). `RealClassicDbDump_...` pins these figures.

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
* The world side (`CreatureRespawnFeature`, `CreatureRespawnQueue`): reads answer from memory, writes are queued to one consumer off the world
  thread, in order, retried three times; the creature systems save what is still dead at shutdown (`StopAsync` after the world stopped) and the
  queue drains. Inactive without a store or with `Creatures:Respawn:Persist=false`.

Verification: `RespawnPersistenceTests` (fake persistence and clock), `CreatureRespawnQueueTests` (ordering, copy semantics, retry, drain), and
`CreatureRespawnStoreTests` (round trip, upsert replace, delete, expired and orphaned-instance purge, instance delete, upgrade from the previous
version with a re-run) in `AvailableProviders` theories: **only SQLite ran on this machine; MariaDB and PostgreSQL run on hosted CI.** The store
was written for their semantics (non-transactional MariaDB DDL: the step is one `CREATE TABLE`; PostgreSQL identifier folding: explicit
lower-case names; no `REPLACE INTO`; no advisory locks), but nothing here proves it until CI runs.

Limits: battleground maps are not excluded (vmangos never stores them, `MapPersistentStateMgr.cpp:86-88`; no battleground lane yet, the
exclusion belongs in `CreatureRespawnQueue.Save`); creature pools/linking that share dormant state are not implemented; the instance reset
path outside `EfInstanceStore.DeleteInstanceAsync` (an in-memory-only reset) is the instances area's; a crash between a death and its queued write
loses that one write (same window as vmangos' asynchronous character-database queue).

## Not done in this lane (recorded, not stubbed)

* **Spawn flags** (`RANDOM_RESPAWN_TIME` x urand(90,110)/100, `DYNAMIC_RESPAWN_TIME`, `DEAD`, `DISABLED`, `ACTIVE`, `EVADE_OUT_HOME_AREA`, ...; `ObjectDefines.h:127-134`) and
  the config-driven dynamic respawn formula (`Creature.cpp:2703-2783`, off by default in vmangos): no column carries them yet.
* **Creature groups, formations, linking, pools, patrol** (`CreatureGroups.cpp`, `CreatureLinkingMgr`, `PoolManager`): data is mostly cmangos-shaped
  (`spawn_group*`, 568 entry-0 spawns resolve through `spawn_group_entry` and still do not spawn) and needs a translator. No importer, schema or
  behaviour was started.
* **Interaction pause** (`Creature::PauseOutOfCombatMovement`) touches the NPC and quest handlers owned by other lanes.
* **Stuck/unreachable evade** belongs to the threat-and-aggro lane (`Creature.cpp:~998-1043` sits beside its leash code); leash radius, 3 s leash checks and
  `NO_LEASH_EVADE` already exist on the base.
* **Home-leg teleport fallback** when no path exists (`HomeMovementGenerator.cpp:71-72`; no creature teleport primitive exists to reuse), `RemoveAurasAtReset`,
  addon reload on arrival.
* **Node scripts, wander at nodes, sub-paths, non-repeating paths** (`WaypointMovementGenerator.cpp:128-242`), navmesh random wander points and flying
  wander circles, the spline in the create block (`packet_builder.cpp:152-200`), a GM `.wp show`/`.creature movement` inspection command.
* **Real-client verification** of the destination-relative spline offsets and the walk/run toggle packets. Both references agree, so the retail layout is the
  default, but the only oracle in the tests is bytes derived by hand from the references.

## References used

vmangos: `Movement/spline/packet_builder.cpp`, `Movement/MoveSplineInit.cpp`, `Movement/MotionMaster.h`, `Movement/RandomMovementGenerator.cpp`,
`Movement/WaypointMovementGenerator.cpp`, `Movement/WaypointManager.{h,cpp}`, `Movement/HomeMovementGenerator.cpp`, `AI/CreatureAI.cpp`, `Objects/Creature.cpp`
(`:817-884`, `:1087-1161`, `:1318-1343`, `:1925-2004`, `:2242-2264`, `:2785-2794`, `:3305-3401`), `Maps/Map.cpp:1310-1365`, `Maps/MapPersistentStateMgr.cpp:80-101`,
`sql/characters.sql:472-480`, `mangosd.conf.dist.in:397,1468-1478,1526,1537-1542`. mangos-classic: `Movement/packet_builder.cpp:60-125`,
`MotionGenerators/RandomMovementGenerator.cpp`, `MotionGenerators/WaypointManager.{h,cpp}`, `MotionGenerators/WaypointMovementGenerator.cpp:35-80`,
`Entities/Creature.cpp:1595-1660,636-655,3025-3055`, `Globals/ObjectMgr.cpp:1826-1869`, `World/World.cpp:457`. wow_messages: `smsg_spline_move_set_walk_mode.wowm`.
classic-db z2815: the `creature`, `creature_movement`, `creature_movement_template`, `creature_spawn_entry`, `spawn_group_*` tables (figures pinned by the
`RealClassicDb_*` tests, which run when `ARCANECORE_CLASSICDB_DUMP` points at the dump). Nothing was copied into the repository.
