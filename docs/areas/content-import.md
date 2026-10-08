# Area: content import (classic-db / vmangos dumps to the world database)

Status: first six slices of the unified importer (core, CLI, items and quests, kill reputation, new-character content, locations), branch `claude/vw-content-import-full`. WoW 1.12.1 (5875). Data source of record:
the cmaNGOS **classic-db** Full_DB snapshot (`ClassicDB_1_12_1_z2815.sql.gz`, "Melting Pot v2", core z2815). vmangos
dumps are a second accepted layout. The dumps are GPL-3 data with Blizzard copyright material in them (classic-db
`COPYRIGHT.md`): they are read from wherever the developer keeps them, never committed, and the importer refuses to write a
database or report inside a git work tree unless git ignores the path.

## Delivered

### Totem spell CLI integration (2026-10-04; no schema change)

`plan`, `import`, dry runs and JSON reports now include the totem spell source tables. The existing
`TotemSpellDumpImporter` runs inside the CLI's shared transaction and writes the existing world-version-15
`totem_spell` table. `--replace` replaces the table; without it, a duplicate target key fails the entire
import and restores all earlier importer writes. Repeating an import with `--replace` produces the same mappings.

- For classic-db, candidates come from summon-totem effects 74 and 87–90 in `spell_template`, or
  `creature_template.AIName = TotemAI`. An explicit `creature_template.SpellList` (`spell_list_id`
  is accepted as an alias) takes precedence and selects the lowest-position positive spell in
  `creature_spell_list`. Otherwise the first nonzero `spell1..spell10` of default-set (`setId = 0`)
  `creature_template_spells` is used. Later source rows replace earlier mappings, including zero values.
- For vmangos, `creature_template.totem_spell_id` supplies the spell directly, including templates without
  `TotemAI` in `AIName`. The highest template patch at or below 10 wins, matching the creature importer.
  A direct field of zero stays zero and does not fall back to cmangos spell-list data.
- Missing spell mappings are counted as `totem_creatures_without_spell`. Summoned creatures without a
  mapping produce an advisory warning containing their entries: some, such as Sentry Totem, intentionally
  have no spell. No source data or generated database is committed. Report `specVersion` is now 2.
- `verify` counts `totem_spell` and rejects references to missing creature templates. Once spell DBC
  content is imported, it also rejects missing spell references. With no imported spells it prints a note
  that the spell check could not run; use `import-dbc` before starting the server.

Synthetic CLI tests exercise persistence, repeat replacement, dry runs, schema refusal, vmangos direct
fields, reference checks and rollback of earlier creature writes after a totem collision. Library tests
exercise the caller's savepoint/transaction boundary and outer rollback. Real dump and non-SQLite provider
checks remain environment-dependent; this continuation has no client DBCs or real dump available.

The compatibility mapper does not reproduce cmangos list availability rolls, targeting validation, or
skipping invalid spell IDs using DBC content during resolution. `verify` detects missing spell IDs once
spell DBCs have been imported.

References: cmangos core `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`,
`src/game/Entities/Totem.cpp:171–176` (first creature-spell-list entry),
`Creature.cpp:609–612` (explicit list precedence), `Globals/ObjectMgr.cpp:1032–1110,9757–9816`
(list positions and nonzero legacy slots);
vmangos core `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `src/game/Objects/Totem.cpp:220–222`
(`totem_spell_id`). Both source trees were consulted as behavioral references; no GPL source or dump rows
were copied into the implementation.

### `import-core-min` (library, `ArcaneCore.Data/Content/Import/{Sources,Spec,Core}`, no schema change)

- **Sources.** `DumpFiles.OpenText` reads `.sql` or `.sql.gz`, recognising gzip by its magic number (`1F 8B`), not by name.
  `DumpFiles.Describe` records name, size and SHA-256 (never a directory). `ChainedTextReader` presents several dumps as one
  stream so the existing importers see one `CREATE TABLE` registry across files (the real dump uses column-less `INSERT`s).
  `MySqlDumpReader` gained, additively, `NewTableRegistry()`, a constructor taking a shared registry, and
  `UnappliedStatements`: counts of the `UPDATE`/`DELETE`/`ALTER`/`TRUNCATE` statements it reads past (a dump is a snapshot;
  classic-db `Updates/` files are not replayed).
- **Spec** (`ContentTableSpecs`). For each table the existing importers read: key columns (no implicit 0), the columns the
  importers actually read, and dialect signatures. A drift test (`TableSpecDriftTests`) checks every mapped column really
  changes what the importers write and that unread columns do not, for the creature, spawn, movement, model, addon,
  game object spawn and loot tables.
- **Scanner** (`ContentScanner`). Runs over every input before any write and aborts with `ImportSchemaException`, naming table
  and column, when: a key column is missing (a renamed `Entry` made `CreatureDumpImporter` read every key as 0 and keep one
  Entry-0 template), a key is NULL/empty, rows collapse to at most one distinct key, a table matches no dialect or two. Repeated
  keys among distinct ones are counted (`duplicates`), not fatal (later rows replace earlier ones).
- **Dialect per table** (`ContentDialect`: `CMangosClassic`, `CMangosHead`, `VMangos`, `CMangos`), from signature columns:
  `creature_template` classic `{ModelId1, MinLevel}` (z2815 dump), head `{DisplayId1, DisplayIdProbability1, MinLevel}`
  (mangos-classic `sql/base/mangos.sql`, added by `sql/updates/mangos/z2823_01_mangos_displayid_probability.sql`), vmangos
  `{level_min, display_id1}` (vmangos `src/game/ObjectMgr.cpp:1190`); `creature`/`gameobject` cmangos `spawnMask`
  (`sql/base/mangos.sql:722` creature, `:1935` gameobject; without `patch_min`) vs vmangos `patch_min` (`ObjectMgr.cpp:2319-2330` LoadCreatures, `:2527-2537` LoadGameobjects query); loot tables cmangos
  `condition_id` without `patch_min` vs vmangos `patch_min`+`patch_max` (`src/game/LootMgr.cpp:105`).
- **Report** (`ContentImportReport`, JSON): tool, report version, command, dry-run flag, input names/sizes/SHA-256,
  `db_version` text, per-table dialect, rows, distinct keys, duplicates and mapped/unmapped columns, unapplied statements,
  rows written and skipped, warnings, and the license notice. No source rows, no host paths.
- **`ImportTransaction`.** One transaction around several importers: a later failure or cancellation restores every earlier
  importer's rows. The importers keep their own contract unchanged (they treat it as a caller transaction and use savepoints);
  a caller's own transaction is protected by a savepoint and never committed or rolled back by it. Verified on SQLite; the
  MariaDB/PostgreSQL variants of the same tests run when `ARCANECORE_TEST_MARIADB` / `ARCANECORE_TEST_POSTGRES` are set (they
  were not set in the verification run, see Limits).

### `import-cli-min` (`tools/ArcaneCore.ContentImporter`, logic in `ArcaneCore.Data/Content/Import/Cli`)

`arcane-content-importer <command>`; `--help` prints the full usage.

| Command | What it does |
|---|---|
| `plan <dump>...` | reads the dumps and prints, per table an importer reads, dialect, rows, keys, duplicates, mapped and not-imported columns; then the tables no importer reads yet and the statements not applied. Writes no database. |
| `import <dump>...` | the scan above, then `CreatureDumpImporter`, `GameObjectLootDumpImporter` and `ItemQuestDumpImporter` inside one `ImportTransaction`. `--replace` empties the importers' tables first; without it a key conflict fails and nothing changes. `--dry-run` reads and counts, writes nothing. `--dbc-dir <DBFilesClient>` also reads `Lock.dbc` (game object locks). `--quest-xp derived|none` (default derived, see below). |
| `import-dbc <dir>` | the five spell DBCs through `SpellDbcImporter` / `EfSpellContentStore` (what `arcane-spell-import` does). |
| `verify` | table counts and spawns whose template does not exist (entry 0 is reported as a note, see Limits). |

Targets: `--database <file>` (SQLite) or `--provider sqlite|mariadb|mysql|postgresql --connection-string <cs>` (or the
`ARCANECORE_CONTENT_CONNECTION` variable, to keep the password off the command line). The target is created at the current
world schema (`WorldDbContext.Schema`) without the dev seeds. Connection strings are never printed unredacted
(`ConnectionStringRedactor`); driver errors are scrubbed of the password.

Exit codes: 0 ok; 1 unreadable input (missing/malformed dump or DBC); 2 usage (unknown command/flag/value); 3 source schema
or dialect (`ImportSchemaException`, or a table not in the `--dialect` asked for); 4 database error, nothing changed;
5 path inside a git work tree and not git-ignored (`git check-ignore`; fail closed when git cannot be run); 6 `verify`
found missing references.

### `items-quests-content` (`Import/Core/RowMapper.cs`, `Import/Mappers/ItemQuestDumpImporter.cs`; no schema change)

Imports the tables that already had a schema and store but no importer: `item_template` -> `ItemTemplateRow`,
`quest_template` -> `QuestTemplate`, `creature_questrelation` / `creature_involvedrelation` -> the relation rows, and
`playercreateinfo_item` -> `PlayerCreateInfoItemRow`.

- **`RowMapper<T>`** maps a dump row onto a row class by name: a column matches a public settable property when the names are
  equal ignoring case and underscores (`stat_type1` = `StatType1`, `spellppmRate_1` = `SpellPpmRate1`, vmangos `display_id` =
  `DisplayId`), with explicit aliases only for the names that really differ (cmangos `RangedModRange`->`RangeMod`,
  `itemset`->`SetId`, `LanguageID`->`PageLanguage`, `area`->`AreaBound`, `Map`->`MapBound`). `TableSpec.IsMapped` asks the mapper,
  so `plan`'s mapped/not-imported column lists for these tables cannot drift from what is imported. Integers outside their
  type clamp and every clamped column is reported with a count and the first value (`MapDiagnostics`); text that is not a
  number is an `ImportSchemaException`.
- **Dialects.** cmangos z2815 and vmangos (`displayid` vs `display_id`+`patch`; quests `RewMoneyMaxLevel` without `patch`/`RewXP`
  vs `patch`+`RewXP`; relations `id,quest` vs `patch_min/patch_max`). vmangos `item_template` and `quest_template` take the row
  with the highest `patch` not above 10 (`ObjectMgr.cpp:3815-3821` LoadItemPrototypes, `:5523` LoadQuests); relations apply
  `patch_min..patch_max` (`:9172-9178`). A relation to a quest that does not exist is skipped with a warning (`:9199-9203`); one
  to a missing creature is kept, as vmangos only logs it (`:9249-9261`), and `verify` notes it.
- **Item `Material` -1.** classic-db has `Material` -1 (consumables) in a signed column. cmangos reads it into a `uint32`
  (`src/game/Entities/ItemPrototype.h:487`) and the query response sends those bits, so the importer keeps the two's-complement
  bits (0xFFFFFFFF) instead of clamping to 0 (`RowMapper` option `signedAsUnsigned`). A first real-data run clamped 20+ values;
  that is how it was found.
- **Quest XP (deliberate deviation, switch `--quest-xp derived|none`, default derived).** classic-db has no `RewXP`; cmangos
  derives XP at run time from `RewMoneyMaxLevel` and the quest level (`src/game/Quests/QuestDef.cpp:171-205`: /0.6 for levels
  1-60, /1.2 at 61, /2.4 at 62, /3.6 at 63, /4.8 at 64, /6.0 from 65 and for -1 through the `uint32` cast; level 0 none). The
  importer stores `ceil` of that full XP in `RewXP` and the game applies its grey-level reduction to the stored integer, so
  an XP reward reduced for a grey quest can differ from cmangos' float-then-ceil by 1 XP (full-XP rewards are exact). A
  source with a `RewXP` column (vmangos) is never derived. `none` leaves `RewXP` 0 (no quest XP).
  The game's `Quest.XpValue` also clamps a negative quest level to 0 where cmangos wraps it to above 65; one quest in
  z2815 has level -1. Left as is (Game-side change, outside this lane's files).
- **`playercreateinfo_item`** is mapped (race, class, itemid, amount) but classic-db z2815 has no rows in it: cmangos builds the
  starting outfit from `CharStartOutfit.dbc` (`src/game/Entities/Player.cpp:857-900`) and only adds `playercreateinfo_item`
  rows on top (`Globals/ObjectMgr.cpp:3157`). New characters therefore still get no starting items from classic-db.

### `onkill-reputation` (`Data/Reputation/CreatureOnKillReputationWorldModule.cs`, `Import/Mappers/OnKillReputationDumpImporter.cs`; World schema step)

Kill reputation was a hole: `IReputationOnKillSource` (Kernel `CharacterReputation.cs`) is what `ReputationFeature` resolves
from DI, and nothing registered it, so no creature ever granted reputation. This slice adds the table, the source and the
importer.

- **Schema.** `CreatureOnKillReputationWorldModule`, one new `IDataModule` file, `Version = 14` (renumbered by the 2026-10-03 vanilla-wave integration: World 11 player stats, 12 creature behaviour, 13 conditions, 14 this step; it was 11 on the lane branch; the single constant the integrator renumbers; `IntegratedSchemaTests` and the module
  test refer to the constant). Table `creature_onkill_reputation`, key `CreatureId`; the module registers
  `EfReputationOnKillSource` (scoped). No `ICharacterDataCleanup`: the world schema holds no per-character rows.
- **By-name mapping.** vmangos selects `IsTeamAward` before `MaxStanding` (`ObjectMgr.cpp:8897-8899`) and classic-db stores
  `MaxStanding` first, so reading by position swaps them; the importer maps by name and a test pins the order independence.
  vmangos rows take the highest `patch` not above 10 (`:8902`).
- **Not done at import time.** vmangos skips rows whose creature has no template or whose faction is not in Faction.dbc
  (`:8935-8957`); the importer has neither set. `verify` notes creatures without a template (none in z2815); the reputation
  service ignores unknown factions at kill time.
- Verified: 470 rows imported from z2815 (all with templates), and the world daemon logged "Loaded 0 factions and 470 kill
  reputation entries" (the factions need `Faction.dbc`, which this machine does not have, so reputation gains were not
  exercised end to end).

### `player-create-content` (`Import/Mappers/PlayerCreateDumpImporter.cs`; no schema change)

New-character content that already had tables and consumers but no importer, plus the level-stats file the progression
feature reads:

- **`playercreateinfo`** -> `player_create_info` (`PlayerCreateInfoRow`: start map, zone, x/y/z/orientation; vmangos
  `LoadPlayerInfo` select, `ObjectMgr.cpp:4525`). `--replace` replaces the 40 dev-seed rows of `WorldDbInitializer`
  (which seeds only an empty table) with the real ones; the seeds differ from classic-db for Night Elf, Undead and Gnome starts.
- **`playercreateinfo_spell`** -> `PlayerCreateSpellRow` (the spells a new character knows; 1,497 rows) and
  **`spell_target_position`** -> `SpellTargetPositionRow` (fixed teleport destinations; 353). vmangos keeps only rows whose
  `build_min..build_max` contains 5875 (`ObjectMgr.cpp:4679`, `Spells/SpellMgr.cpp:52`); classic-db has no build columns.
- **Level stats file.** `import --level-stats-file <path>` joins `player_levelstats` (race, class, level, str, agi, sta, inte,
  spi; vmangos `:4898`) with `player_classlevelstats` (base health and mana; `:4801`) into the text form of
  `PlayerLevelStatsTable` (`race,class,level,basehp,basemana,str,agi,sta,int,spi`), which `Progression:LevelStatsPath`
  loads (`World/Progression/ProgressionFeature.cs`); a World test parses a generated file with that parser. Without the
  file, level-ups change level and XP but no base values. A row with no class values is left out and reported.
- **Class masks.** `class-masks <dump>... --class-mask-file <path>` reads `spell_affect` (entry, effectId, 64-bit `SpellFamilyMask`) and
  writes `spell effect 0xMASK` lines that `Spells:Mods:ClassMaskFile` loads ([spell-mods](spell-mods.md)); the spell DBC reads these
  masks as 32 bits and about 11% of the classic rows need more. It writes no database; `--dry-run` writes no file. vmangos' own mask
  corrections (migration `20240926142033`) are not applied.
- **Proc conditions.** `proc-events <dump>... --database <file>` (or `--provider`/`--connection-string`) replaces `spell_proc_event` (world 39)
  with the dump's rows for build 5875 in one transaction, by column name for both the classic-db and the vmangos layout; vmangos rows whose
  build range misses 5875 are dropped and counted. `--cooldown-unit seconds` reads classic-db dumps before z2829, whose `Cooldown` is in
  seconds; `--dry-run` parses and counts without a database. Two kept rows for one spell are an error. See [procs](procs.md).
- **Refresh an existing world.** `refresh <dump>... --database <file> [--dbc-dir <dir>]` brings a world that an older importer built up
  to the tables the current world reads, and touches nothing else (creatures, objects, items, quests, loot and NPC services stay as they
  are). Inside one transaction it empties and refills, only when the inputs carry them: `world_safe_locs` and `game_graveyard_zone`
  (`WorldSafeLocs.dbc` in `--dbc-dir` adds the ids the dump lacks; a dump row keeps its facing, which the DBC does not have), the four
  battleground tables, `exploration_basexp` and `game_weather`, `areatrigger_tavern` (`AreaTriggerTavernDumpImporter`), `transports`
  (`TransportDumpImporter`; a cmangos row has no build and gets 0), `spell_proc_event` (build 5875; `--cooldown-unit auto` reads the
  classic-db core revision from `db_version`: seconds before z2829, milliseconds from it, refused when the dump names none), the relay DB
  scripts, `areatrigger_template` from `AreaTrigger.dbc` (`AreaTriggerDbcReader`, vmangos `niffffffff`; the client's patch-2.MPQ copy
  holds 432 triggers, every `areatrigger_teleport` id of classic-db among them), and the flight masters' `taxi_nodes` and `taxi_path`
  from `TaxiNodes.dbc` and `TaxiPath.dbc` (`NpcServiceDbcReaders`, vmangos `nifffssssssssxii` and `niii`). The ships' own
  `gameobject_template` rows (type 15: data0 TaxiPath id, data1 speed, data2 acceleration) are written as the dump has them, mapped as
  the full object import maps them (`GameObjectLootDumpImporter.MapTemplate`); every other object template is left alone. A missing DBC
  leaves its table alone with a warning. A second run with the same inputs leaves the same rows. It then checks what the world logs at
  start: teleports and taverns without a trigger, battleground start locations that are not safe locations, transports without a type-15
  object, and, with `TaxiPathNode.dbc` in `--dbc-dir`, every ship the world would refuse (speed or acceleration 0, a path under three
  nodes, a map without a `map_template` row; [transports](transports.md)). It refuses a database file that does not exist, and a world whose schema is behind the
  importer's unless `--migrate` is given (it never migrates on its own: an importer built with another lane's world step would otherwise
  upgrade the live world before the server that needs it is deployed). `tools/content/refresh-world-content.ps1` wraps it for an
  operator: it refuses a database another process holds open, writes a SHA-256-checked backup (the database and any leftover `-wal`),
  checks the five DBCs (`AreaTrigger`, `WorldSafeLocs`, `TaxiNodes`, `TaxiPath`, `TaxiPathNode`) against a `SHA256SUMS` file in
  `-DbcDirectory` when there is one or extracts them from the client's MPQs with `mpqcli` (patch-2 over patch over dbc), and passes
  `-Migrate` on as `--migrate`. Run on a copy of the live world (2026-10-07): 122 safe locations, 191 graveyard links, 3 battleground
  templates, 969 + 421 battleground spawn events, 24 battlemasters, 61 exploration levels, 33 weather zones, 42 taverns, 9 transports,
  164 proc rows, 828 relay steps, 14 relay templates and 432 area triggers; every other table byte-identical afterwards
  (docs/integration/content-refresh-20261007.md). Again on 2026-10-08 with the taxi DBCs: 9 ship templates, 85 taxi nodes, 287 taxi paths,
  "TaxiPathNode.dbc: 9582 row(s) on 288 path(s)" and every ship route resolving (docs/integration/transports-content-20261008.md).
- Verified on the z2815 dump: 40 start positions, 1,497 starting spells, 353 teleport targets and 2,400 level-stat rows (human
  warrior level 1: strength 23; the file is a sample of the retail table, not committed); the daemon logged "level stats for
  2400 race/class/level rows". With no spells imported (`import-dbc` needs client DBCs) the spell feature logs each
  `spell_target_position` row as "unknown spell, skipped", the same as vmangos' "Non existing spell" skip.

### `locations-min` (`Import/Mappers/LocationDumpImporter.cs`; no schema change)

`areatrigger_teleport` (dungeon and instance portals) -> `AreaTriggerTeleportRow` and `game_tele` (GM `.tele` names) ->
`GameTeleRow`, both into the existing map-data tables. cmangos' `status_failed_text` is the row's `Message` (vmangos:
`message`); vmangos rows take the highest `patch` not above 10 (`ObjectMgr.cpp:7712-7717`). The row carries no item, quest or
heroic-key requirement, so cmangos' `required_item`, `required_item2`, `required_quest_done` and `condition_id` are not
enforced (`plan` lists them as not imported). The trigger shapes (`areatrigger_template`) come from `AreaTrigger.dbc` through
`refresh --dbc-dir` (above) and `map_template`/`area_template` through `import-map-dbc`; a portal row cannot fire until its trigger exists. Verified: 103 portals and 269 GM
teleports imported; the daemon logged "103 area trigger teleports, 269 teleport locations".

## Verified against the real classic-db dump

Run on `ClassicDB_1_12_1_z2815.sql.gz` (12,959,882 bytes, SHA-256 `4f92db52...d0c0`, `db_version` "Classic DB version 1.12.1
\"Melting Pot v2\". For Classic core z2815."), importing into a scratch SQLite file outside the repository (about 35 s with items and quests; `tools/content/verify-classic-db.ps1` repeats the run and compares every count):

| Table | Source rows | Imported |
|---|---|---|
| creature_template | 10,384 | 10,384 |
| creature (spawns) | 66,310 | 66,310 |
| creature_movement | 51,649 | 51,649 |
| creature_model_info | 10,534 | 10,534 |
| creature_addon | 1,782 | 1,782 |
| creature_ai_scripts | 10,843 | 10,843 |
| gameobject_template | 10,743 | 10,743 |
| gameobject (spawns) | 47,827 | 47,827 |
| gameobject_questrelation / involvedrelation | 257 / 208 | 257 / 208 |
| creature/gameobject/item/skinning/reference loot rows | 169,994 / 12,548 / 3,791 / 2,802 / 33,366 | 222,501 together |
| creature loot info (from creature_template) | | 5,060 |
| item_template | 17,718 | 17,718 |
| quest_template | 4,245 | 4,245 (RewXP derived for 3,492) |
| creature_questrelation / creature_involvedrelation | 3,826 / 3,951 | 3,826 / 3,951 (none dropped) |
| playercreateinfo_item | 0 | 0 (see above) |
| creature_onkill_reputation | 470 | 470 |
| playercreateinfo / playercreateinfo_spell / spell_target_position | 40 / 1,497 / 353 | 40 / 1,497 / 353 |
| player_levelstats (joined with player_classlevelstats) | 2,400 | 2,400 rows in the level-stats file |
| areatrigger_teleport / game_tele | 103 / 269 | 103 / 269 |

The world daemon (`ArcaneCore.World`, SQLite for all three databases, port overridden) then logged "Loaded 10384 creature
templates and 66310 spawns" and "Loaded 10743 game object templates, 47827 spawns, 0 locks, 222501 loot rows, 5060 creature
loot entries" and "Loaded 4245 quest templates". Item templates load lazily (first character creation), so a throw-away probe built `ItemTemplateStore` from the imported database through `EfItemTemplateSource` (17,718 templates; Hearthstone, Worn Shortsword, Linen Cloth and Lionheart Helm read back with plausible names, qualities, prices and display ids). `plan` shows the 160 other source tables (411,716 rows) as not read by any importer.

## Limits (explicit, not done)

- **Only what the importers read is imported.** Not imported (listed by `plan`):
  NPC vendors (11,890 rows), trainers (27,309), gossip menus/options/NPC text and `npc_*_template` tables (they need `creature_template.VendorTemplateId/TrainerTemplateId/GossipMenuId`, which the creature template does not carry, plus schema and Game-side consumers), conditions, `creature_spawn_entry`/`gameobject_spawn_entry`, equipment
  and template addons, `creature_template_classlevelstats` and the template multipliers, movement templates, pools and
  game events, broadcast text, DBC-derived tables (maps, areas, taxi, races, start outfits), `playercreateinfo_action` (215 rows, needs
  the action-button seam wired at character creation) and `playercreateinfo_skills` (77; no skills consumer), `race_info`/`class_info`
  (still dev seeds; their retail source is ChrRaces.dbc), graveyards. The unmapped columns of every read table are printed by `plan`.
- **Spawns with id 0** (2,802 creatures, 3,614 game objects in z2815) are imported with entry 0: cmangos resolves them through
  `creature_spawn_entry` / `gameobject_spawn_entry` (not imported), so the runtime cannot spawn them. `verify` notes them.
- **`spawnMask`** is not read: cmangos does not place a spawn whose mask is 0 in any grid (`src/game/Globals/ObjectMgr.cpp:2060-2063` creatures, `:2335-2338` game objects), the importer still imports it.
- **cmangos class level stats** are not needed for z2815 (health/mana/damage are materialised in `creature_template`); a
  HEAD-layout dump that relies on `creature_template_classlevelstats` is not computed.
- **No subset filter** and **no non-strict mode**: any guard failure aborts the whole run (exit 3) before writing.
- **Lock.dbc and the spell DBCs are tested with synthetic files only**: no client DBCs exist on the verification machine, so
  `--dbc-dir` and `import-dbc` were not run against real client data.
- **Starting items.** see the `playercreateinfo_item` note above: `CharStartOutfit.dbc` is not imported.
- **`arcane-spell-import` is not yet a shim** over `import-dbc` (the spell importer API is shared and unchanged).
- **MariaDB/PostgreSQL** transaction tests are env-gated and were not executed in the verification run (no servers); SQLite
  was exercised. The CLI's unreachable-server path is tested (exit 4, no password in output).
- SQLite file paths must fit Windows `MAX_PATH` including the journal suffix (a 230-character scratch path failed with
  "unable to open database file"); use a short directory.
- Dialect signatures are per table and conservative: a cmangos dump whose `creature`/`gameobject` lacks `spawnMask`, or a
  loot table with neither `condition_id` nor `patch_min`, is rejected (exit 3), not guessed.

## References

- vmangos (`D:\refs\vmangos`): `src/game/ObjectMgr.cpp` (`LoadCreatureTemplates` :1190, `LoadCreatures` :2319, game objects
  :2527, `LoadGameObjectTemplates` :8140), `src/game/LootMgr.cpp:105`.
- mangos-classic (`D:\refs\mangos-classic`): `sql/base/mangos.sql` (`creature.spawnMask` :722, `gameobject.spawnMask` :1935), `sql/updates/mangos/z2823_01_mangos_displayid_probability.sql`.
- classic-db (`D:\refs\classic-db`): `Full_DB/ClassicDB_1_12_1_z2815.sql.gz` (column names of every table above, `db_version`),
  `README.md`/`COPYRIGHT.md` (licensing), `LICENSE.md` (GPL v3). Read-only; nothing copied.
