# Area: creatures and world spawns

Status: implemented on `feat/creatures` (PR into `claude/friendly-hamilton-cuz4j4`). WoW 1.12.1 (5875).

## What is implemented

**Content and schema** (`ArcaneCore.Kernel/WorldData/Creatures`, `ArcaneCore.Data/World/Creatures`)
- Immutable `CreatureContent` with templates, spawns, waypoint paths, model info and addons. It is loaded once at startup, and the world thread reads it without locks.
- The world schema v2 step (`CreatureDataModule`, `IDataModule`) adds:
  - `creature_template`: cmangos-classic column meanings, with per-level health/mana columns.
  - `creature_spawn`: cmangos/vmangos `creature`.
  - `creature_movement`: per-spawn waypoints.
  - `creature_model_info`: cmangos name; vmangos calls it `creature_display_info_addon`.
  - `creature_addon`.
- **Importer** (`CreatureDumpImporter` + `MySqlDumpReader`). It streams `mysqldump` files (CREATE TABLE / INSERT / REPLACE, escapes, NULL, comments, column lists) and maps columns **by name** from either source:
  - **cmangos classic-db** (mangos.sql): the columns map directly.
  - **vmangos world db**, with these rules:
    - Templates: the row with the highest `patch` ≤ 10 wins.
    - Spawns: only those whose `patch_min..patch_max` contains 10.
    - `creature_display_info_addon`: the newest `build` ≤ 5875.
    - Type flags come from `CreatureInfo::GetTypeFlags` (static flags). Unit flags come from the static flags (`ToggleUnitFlagsFromStaticFlags`).
    - Health, mana, armor and damage are `creature_classlevelstats` × the template multipliers (`InitStatsForLevel`). A missing class/level row is a warning.
  - `id2..id5` spawns are imported as `creature_spawn_entry` rows (wave-4 movement lane)
  - Mixed dialects are rejected.
  - `WriteAsync(db, replace)` inserts in batches. The dumps are GPL data and are never committed.

**Creature object** (`ArcaneCore.Game/Creatures/Creature.cs`): `Creature : Unit`, GUID `HighGuid.Unit | entry | spawn guid`, TYPEMASK_OBJECT|UNIT. Fields follow vmangos `InitEntry/UpdateEntry` and cmangos `SelectLevel`:
- Entry and scale.
- Display: `ChooseDisplayId` (weighted or equal chance; box 4 if none), then the 50 % other-gender swap.
- BYTES_0: class, gender from model info, power type. Mana if the creature has mana; otherwise energy for rogues and rage (max 1000) for everyone else.
- Level is urand(min, max). Health and mana interpolate linearly between the template's min- and max-level values.
- Faction, NPC flags, unit flags (+ PLUS_MOB for rank > 0), dynamic flags, attack times, damage, armor, base health/mana.
- Bounding radius and combat reach are scale × model info.
- MOD_CAST_SPEED 1.0, sheath melee, the auras flag byte.
- Addon: mount, stand state, sheath state, emote state.
- Speeds: 2.5 × speed_walk and 7 × speed_run.

**Map system** (`CreatureMapSystem`, attached through the new `IMapUpdater` hook):
- **Grids:** spawns are bucketed by vmangos grid (64 × 64 grids of 533.33333 yd, `ComputeGridPair`). The integrated map grid lifecycle owns loading and unloading (`World:Maps:GridUnload`, `GridCleanUpDelayMs`, and `GridActivationDistance`). Dead spawns keep their respawn time across unloads.
- **Visibility:** the same rule players use (`Map.IsWithinVisibilityDistance`: 100 yd + grey distance + radii, 2D).
  - Entering range sends a create block. CREATE_OBJECT is used for grid loads and respawns; CREATE_OBJECT2 only for runtime adds (`SpawnTemporary`), as in vmangos `Map::Add` → `SetIsNewObject`.
  - Leaving range sends an out-of-range block; corpse removal or despawn uses the map's SMSG_DESTROY_OBJECT path. Creatures share the map object and observer indexes, so movement, health and other value changes reach viewers once.
- **Life cycle** (vmangos `Creature::SetDeathState` / `Update`):
  - `KillCreature`: health 0, NPC flags cleared, target cleared, movement stopped (stop packet).
  - Respawn time = death + urand(spawntimesecsmin, max).
  - Corpse decay per rank: 300 / 600 / 1200 / 3600 / 900 s for normal / elite / rare elite / boss / rare, or the template's `CorpseDecay`.
  - The corpse is also removed when a database spawn's respawn time comes first.
  - Dead creatures are invisible and back at their home position. Respawn re-initializes the fields and restarts movement.
  - Temporary creatures do not respawn.
- **Movement:**
  - Straight splines are interpolated on the map clock. SMSG_MONSTER_MOVE is sent to observers (layout below).
  - A client that receives a moving creature's create block gets a catch-up move for the rest of the path on the next tick.
  - Generators: **Idle**; **Random** (vmangos `RandomMovementGenerator`: first move after 1 s, wander steps 50 ms apart, then a urand(4,10) s pause and a new step count of urand(0, wander ≤ 1 ? 2 : 8), walk unless ALWAYS_RUN); **Waypoint** (vmangos `WaypointMovementGenerator`: nodes in point order, wait on arrival, facing angle when orientation ≠ 100 and the node has a delay, loop).

**World daemon** (`ArcaneCore.World/Creatures`):
- `CreatureWorldFeature` loads content at start and attaches systems.
- CMSG_CREATURE_QUERY → SMSG_CREATURE_QUERY_RESPONSE.
- GM commands `.creature add <entry>`, `.creature info`, `.creature kill`, `.creature respawn`, `.creature delete` (temporary creatures only).

## Packets

- **SMSG_CREATURE_QUERY_RESPONSE** (vmangos `CreatureQueryResponse::AppendBodyTo`, gtker 1.12): u32 entry, name, 3 empty cstrings, subname, u32 type flags, type, family, rank, u32 0, pet spell list id, display id[0], u8 civilian, u8 racial leader. Not found: u32 `entry | 0x80000000`. The gtker test vector (entry 69 "Thing") is a known-answer test.
- **SMSG_MONSTER_MOVE** (vmangos `MoveSplineInit::Launch` / `PacketBuilder::WriteMonsterMove`):
  - Packed GUID, start xyz, u32 spline id, u8 type, [facing], u32 flags (minus `Mask_No_Monster_Move`; Runmode 0x100 when running), u32 duration, u32 count 1, destination xyz.
  - Stop form: packed GUID, xyz, spline id, u8 1.
- **CMSG_CREATURE_QUERY**: u32 entry, u64 guid.

## References

- **vmangos:** `Creature.cpp` (InitEntry, UpdateEntry, SelectLevel, SetDeathState, Update, Respawn, RemoveCorpse, IsVisibleInGridForPlayer, ChooseDisplayId), `ObjectMgr.cpp` (GetCreatureDisplayInfoRandomGender, LoadCreatureClassLevelStats), `CreatureDefines.h` (static flags, GetTypeFlags), `GridDefines.h`, `Map.cpp` (Map::Add), `Object.cpp` (CREATE_OBJECT2, GetRandomPoint), `RandomMovementGenerator.cpp/.h`, `WaypointMovementGenerator.cpp`, `MoveSplineInit.cpp`, `MoveSplineFlag.h`, `packet_builder.cpp`, `QueryHandler.cpp`, `QueryPackets.cpp`, `Unit.cpp` (GetCreatePowers), `World.cpp` (corpse decay defaults).
- **cmangos-classic:** `Creature.cpp` (SelectLevel, old-style health), `QueryHandler.cpp`, `World.cpp`, `sql/base/mangos.sql` (column names).
- **gtker/wow_messages:** `smsg_creature_query_response.wowm`, `smsg_monster_move.wowm`.

## Reference discrepancies

1. **Query response trailer:** vmangos and gtker send u8 civilian + u8 racial leader; cmangos-classic sends a u16 civilian (same length). The two-u8 form is used.
2. **Monster-move stop:** vmangos ends the stop packet after the type byte. gtker models flags/duration/points as always present. Servers win, so the short form is used.
3. **Cells per grid:** vmangos `MAX_NUMBER_OF_CELLS` is 16, cmangos 8. This area only buckets by grid. The cell index is left to the grid/map area.
4. **Creature stats:** cmangos stores per-level health/mana in the template. vmangos computes them from `creature_classlevelstats` × multipliers. The schema follows cmangos and the importer converts vmangos.
5. **Corpse decay defaults:** vmangos and cmangos agree (300/900/600/1200/3600).
6. **Random destination:** vmangos asks the navmesh (`GetWalkRandomPosition`). Without navmeshes, the point is uniform over the wander disc at the height provider's Z (or the spawn Z).
7. **Create while moving:** vmangos writes the live spline into the create block (`PacketBuilder::WriteCreate`). The M5 create block never advertises a spline, so a catch-up SMSG_MONSTER_MOVE follows one tick later.

## Real-client acceptance steps

1. Import a world DB:
   - Load a cmangos classic-db or vmangos dump through `CreatureDumpImporter` into the world database. The current entry point is `WriteAsync`; an importer CLI is still to be done (see below).
   - Or insert rows by hand, e.g. a template 299 "Young Wolf" (display 903) and a spawn near Northshire at (-8940, -132, 83.5), map 0.
2. Start realmd and worldd, then log in a human with a 1.12.1 client.
3. The creature appears with its model and name. Hovering shows its level and faction colour (the client queried CMSG_CREATURE_QUERY).
4. A spawn with MovementType 1 wanders within its spawndist. A spawn with creature_movement rows walks its path and pauses at nodes with WaitTime.
5. Walk more than 100 yd away and the creature disappears. Come back and it reappears.
6. As GM:
   - `.creature add 299` spawns a temporary wolf at your feet.
   - Select a creature and run `.creature kill`. It dies and its corpse lies there; after the decay time it vanishes, and after spawntimesecs it respawns at home.
   - `.creature respawn` respawns it at once. `.creature info` shows its state.

## What's left

- Respawn times are not persisted (`creature_respawn`). They live in memory and survive grid unloads but not restarts.
- No terrain or navmesh Z (seam: `ICreatureHeightProvider`) and no line-of-sight.
- Not driven yet:
  - Equipment / virtual items (items area)
  - Auras from the addon
  - Gossip, vendors, trainers (NPC area)
  - AI and aggro (combat)
  - Movement scripts
  - Wander at waypoint nodes
  - Entry-based waypoint paths (`creature_movement_template`): done in the wave-4 movement lane, see `creature-movement-spawns.md`
  - Creature groups / formations
  - Linked spawns
  - Game-event spawns
  - `id2..id5` alternatives: done in the wave-4 movement lane (`creature-movement-spawns.md`); cmangos spawn groups are not
- Flying and swimming random movement, run speed on waypoints (vmangos `UNIT_STATE_RUNNING`), CREATURE_FLAG_EXTRA_INVISIBLE, and GM visibility of dead creatures.
- An importer CLI (`tools/ArcaneCore.ContentImporter creatures <dump.sql…> [--replace]`). The library API is ready; the tool needs a slnx line, so it was left for the lead.
- Visibility is evaluated every tick for every player against the creatures of nearby grids. Fine at current scales; a cell-indexed visit (grid/map area) will replace it.
