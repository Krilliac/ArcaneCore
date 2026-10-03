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
* `MovementGeneratorType` numbers follow `Movement/MotionMaster.h:36-59` (Chase 6, Home 7, Point 9, Fleeing 10, Follow 15); the
  values are internal (never persisted or sent; grep of casts found none).

Limits: no navmesh random point and no steep-slope exclusion (vmangos `MOVE_PATHFINDING | MOVE_EXCLUDE_STEEP_SLOPES`; the pathfinder
has no random-point query, so the point is uniform over the disc at the height provider's Z); no flying circle path (`:28-44`: needs
the Flying spline flag plumbed through `ICreatureMover.MovePath`); no timed random / pause-time API (nothing consumes it until waypoint
node wander exists); vmangos' `Interrupt`/`Finalize` walk-mode reset (`:83-93`) is not sent separately because every spline launch
already syncs the mode.

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

Data (World schema version **21**, `CreatureMovementTemplateDataModule.Version`; the integrator renumbers):

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
