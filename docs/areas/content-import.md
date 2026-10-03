# Area: content import (classic-db / vmangos dumps to the world database)

Status: first slices of the unified importer (`claude/vw-content-import-full`). WoW 1.12.1 (5875). Data source of record:
the cmaNGOS **classic-db** Full_DB snapshot (`ClassicDB_1_12_1_z2815.sql.gz`, "Melting Pot v2", core z2815). vmangos
dumps are a second accepted layout. The dumps are GPL-3 data with Blizzard copyright material in them (classic-db
`COPYRIGHT.md`): they are read from wherever the developer keeps them, never committed, and the importer refuses to write a
database or report inside a git work tree unless git ignores the path.

## Delivered

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
| `import <dump>...` | the scan above, then `CreatureDumpImporter` and `GameObjectLootDumpImporter` inside one `ImportTransaction`. `--replace` empties the importers' tables first; without it a key conflict fails and nothing changes. `--dry-run` reads and counts, writes nothing. `--dbc-dir <DBFilesClient>` also reads `Lock.dbc` (game object locks). |
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

## Verified against the real classic-db dump

Run on `ClassicDB_1_12_1_z2815.sql.gz` (12,959,882 bytes, SHA-256 `4f92db52...d0c0`, `db_version` "Classic DB version 1.12.1
\"Melting Pot v2\". For Classic core z2815."), importing into a scratch SQLite file outside the repository (15 s):

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

The world daemon (`ArcaneCore.World`, SQLite for all three databases, port overridden) then logged "Loaded 10384 creature
templates and 66310 spawns" and "Loaded 10743 game object templates, 47827 spawns, 0 locks, 222501 loot rows, 5060 creature
loot entries". `plan` shows all 173 other source tables (447,128 rows) as not read by any importer.

## Limits (explicit, not done)

- **Only what the two existing importers read is imported.** Not imported (listed by `plan`): items, quests and quest
  relations, NPC vendors/trainers/gossip/NPC text, conditions, `creature_spawn_entry`/`gameobject_spawn_entry`, equipment
  and template addons, `creature_template_classlevelstats` and the template multipliers, movement templates, pools and
  game events, onkill reputation, broadcast text, DBC-derived tables (maps, areas, taxi, races, start outfits), player start
  stats, graveyards. The unmapped columns of every read table are printed by `plan`.
- **Spawns with id 0** (2,802 creatures, 3,614 game objects in z2815) are imported with entry 0: cmangos resolves them through
  `creature_spawn_entry` / `gameobject_spawn_entry` (not imported), so the runtime cannot spawn them. `verify` notes them.
- **`spawnMask`** is not read: cmangos does not place a spawn whose mask is 0 in any grid (`src/game/Globals/ObjectMgr.cpp:2060-2063` creatures, `:2335-2338` game objects), the importer still imports it.
- **cmangos class level stats** are not needed for z2815 (health/mana/damage are materialised in `creature_template`); a
  HEAD-layout dump that relies on `creature_template_classlevelstats` is not computed.
- **No subset filter** and **no non-strict mode**: any guard failure aborts the whole run (exit 3) before writing.
- **Lock.dbc and the spell DBCs are tested with synthetic files only**: no client DBCs exist on the verification machine, so
  `--dbc-dir` and `import-dbc` were not run against real client data.
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
