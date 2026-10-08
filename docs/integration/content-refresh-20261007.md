# Content refresh of the live world (wave 3, content-import lane, 2026-10-07)

Branch `claude/w3-content-import` (base `56fc4d18`). Logs: `D:/ArcaneCore-lanes/_logs/w3-content-import/` (`red/`, `green/`, `run-before/`,
`run-after/`, `run-transports/`). No schema version was taken: every table written already exists at world 41.

## What the live world log showed, and why

The live world (`playable-content-20261005/complete-r5/world.db`, world 41) was built by an older importer. A read-only copy shows these
tables empty: `world_safe_locs`, `game_graveyard_zone`, `battleground_template`, `battlemaster_entry`, `creature_battleground`,
`gameobject_battleground`, `exploration_basexp`, `game_weather`, `areatrigger_template`, `areatrigger_tavern`, `transports`,
`spell_proc_event`, `dbscripts_on_relay`, `dbscript_relay_template`.

| Warning (live log 2026-10-08 03:24) | Cause | Fix |
|---|---|---|
| `battlegrounds: 0 template row(s) ... 0 battlemaster(s)` | data: tables empty | `refresh` imports them (3 templates, 969 + 421 events, 24 battlemasters) |
| `AlteracValley: start location 611 or 610 is not a WorldSafeLocs id` (also WSG 769/770, AB 890/889) | data, not code: the lookup reads `world_safe_locs` through the graveyard catalog, which was empty; 610/611 are classic-db `world_safe_locs` rows (and WorldSafeLocs.dbc ids) | `refresh` imports 122 safe locations; all three battlegrounds register |
| `areatrigger_teleport N: no areatrigger_template row` (103) | data: the trigger volumes come from AreaTrigger.dbc, which no importer read | new `AreaTriggerDbcReader`; the client's patch-2.MPQ copy has 432 triggers, all 103 portal ids among them |
| `exploration_basexp is empty` | data | 61 rows |
| `loaded 0 spell proc event conditions` | data (importer existed, never run) | 164 rows, cooldowns converted from seconds (z2815 < z2829) |
| `Transports are disabled` | config, and `transports` empty | 9 rows; with `World:Transports:Enabled=true` the copy logs "9 routes, 9 ships sailing" |
| `0 inn triggers` | data: `areatrigger_tavern` empty, no importer | new `AreaTriggerTavernDumpImporter`: 42 inns |
| EventAI unsupported action 4, 5, 43, 56, 58, 64, event 14, 30 ... | code: 33 action and 10 event types had no handler | implemented (docs/areas/creature-ai.md); rows of the live copy with an unsupported part, counted with the C# registry (below): 1,011 rows over 678 entries or spawn guids on the base, 48 rows over 30 on this branch |

## The refresh

`arcane-content-importer refresh <dump>... --database <world.db> --dbc-dir <dir>` (docs/areas/content-import.md) replaces only the tables
above, in one transaction, and only when the inputs carry them. `tools/content/refresh-world-content.ps1` wraps it: refuses a database
another process holds open, writes a SHA-256-checked backup, takes the two DBCs from a directory or extracts them with mpqcli from the
client (patch-2 over patch over dbc).

On a copy of the live world:

* `green/02-refresh-live-copy.log`: the counts above, "every checked reference resolves", exit 0.
* Run three times through the script (`green/03-*`): runs 1, 2 and 3 leave identical tables (per-table row hashes), and the direct CLI
  run gives the same tables. Against the untouched live copy exactly the 14 tables above differ; every other table is identical.
* The script refuses the database while another handle holds it (`green/03-script-in-use.log`).

## The world on the copies (port 18085, this branch's build)

* `run-before/` (unrefreshed copy): the warnings of the live log.
* `run-after/` (refreshed copy): "battlegrounds: 3 template row(s), 969 creature and 421 game object event row(s), 24 battlemaster(s)" and no
  start-location warning; "42 inn triggers"; "loaded 164 spell proc event conditions"; "432 area triggers, 103 area trigger teleports";
  "loaded 61 exploration base XP rows"; "loaded 122 graveyards and 135 graveyard-zone links". No error lines.
* `run-transports/`: the same with `World:Transports:Enabled=true`: "Transports: 9 routes, 9 ships sailing".

Two new warnings appear once the old ones are gone. Both have one cause outside this lane: `map_template`/`area_template` hold only the
continents (`import-map-dbc` reads maps 0 and 1 only):

* 47 x `areatrigger_teleport N: unknown target map M`: dungeon and raid portals (maps 33, 34, 36, 189, 409, 533, ...) cannot fire until
  their instance maps are in `map_template`. Before the refresh they failed one step earlier (no trigger row).
* 56 x `game_graveyard_zone has a record for the not existing zone id Z` (vmangos logs the same skip): links to instance and battleground
  zones (2597 AV, 3277 WSG, 3358 AB, ...) whose areas are not in `area_template`. Battleground graveyards do not depend on them (the
  battleground override picks its own safe locations).

Not changed and still logged, unrelated to this lane: the optional DBC/dump paths (ItemSets, ItemRandomProperties, PageText,
SpellItemEnchantment, CharSections), the class-mask file, 326 waypoint spawns without a path, and three spawns with entry 0.

## EventAI: how the before/after figure was measured

The world reports unsupported EventAI parts once per creature entry when it builds that creature's AI, and it builds a creature only
when a player's presence loads its grid. The `run-before`/`run-after` worlds had no player online, so their logs have no
`creature_ai_scripts ... unsupported` line at all: they neither show nor contradict the change. The figure therefore comes from
`_logs/w3-content-import/eventai-registry-check/` (a throwaway console harness, not product code). It loads every
`creature_ai_scripts` row of the live copy through the world's own loader (`EfCreatureDataStore`) and applies the rules of
`EventAiEngine.Describe` with `EventAiRegistry.Default` and each handler's `UnsupportedReason`, so it measures the C# registry
itself. Built against the base tree (`w3-content-import-red`, 56fc4d18): 10,843 rows, 1,011 with an unsupported part, over 678
entries or spawn guids (`red2/04-eventai-registry-check-base.txt`). Built against this branch: 48 rows over 30
(`green2/04-eventai-registry-check-lane.txt`: action 34 x37, spawned condition x6, action 48 x3, death condition x2). The earlier
`eventai_static_check.py` count (1,011 / 660 and 48 / 30) used hand-copied type sets and grouped the spawn-guid rows under entry 0.

## Still unsupported in EventAI

Action 34 SET_INST_DATA (37 rows: no instance scripts exist), action 48 CHANGE_MOVEMENT (3 rows), a death event with a condition (2) and a
spawned event with the zone condition (6). They stay in the per-entry warning.

## Applying it to the live server

1. Merge and build the deploy that will serve the world afterwards; step 3 uses its importer (`-Importer`).
2. Stop the World daemon (the script refuses a database in use).
3. `powershell -NoProfile -File <worktree or deploy>\tools\content\refresh-world-content.ps1 -WorldDatabase
   C:\Users\Nathan\Documents\Codex\2026-10-04\c\work\playable-content-20261005\complete-r5\world.db -DbcDirectory
   D:\refs\client-dbc-5875-effective -Importer <deploy>\bin\ArcaneCore.ContentImporter\release\arcane-content-importer.dll`
   (default dump `D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz`; the backup, database and `-wal`, goes to
   `complete-r5\content-refresh-backups\`). The DBC directory is a durable copy of the files extracted from the client's patch-2.MPQ,
   with `SHA256SUMS` and a provenance `README.txt` beside them; the script checks the hashes. Without it, `-MpqTool
   C:\Users\Nathan\Documents\Codex\2026-10-04\c\work\playable-content-20261005\mpqcli-v0.11.0-windows-amd64.exe` extracts them again.
4. Schema: this branch takes no schema version, and the refresh writes only tables that exist at world 41. Whether the live world
   migrates depends on the deploy, not on this branch. If a lane merged with it raised the world schema (42-44 are reserved), the
   deploy's importer refuses the world-41 database (exit 3, nothing written) instead of migrating it on the side; then either start
   that deploy's World once first (it migrates at start) and run the refresh after stopping it again, or add `-Migrate` (the backup is
   taken first). With only this branch the importer is at world 41 and nothing migrates.
5. Start the World daemon from that deploy (the EventAI handlers are code).
6. Optional: `World:Transports:Enabled = true` in the profile's appsettings.json to sail the 9 ships.

## Review fixes (2026-10-08)

* ACTION_T_SUMMON_ID: `creature_ai_summons.spawntimesecs` holds milliseconds (cmangos passes it to `SummonCreature`; z2815 values
  10000..86400000). The lane multiplied it by 1000, which made a 10 s summon live 2.8 h and wrapped values above 4,294,967. The
  record field is now `CreatureAiSummon.LifetimeMs` and is passed through unchanged.
* A summon (actions 12 and 32) with lifetime 0 is cmangos `TEMPSPAWN_TIMED_OOC_DESPAWN` with 0 ms: it despawns as soon as it is
  alive and out of combat (no z2815 row has 0, so this is latent).
* ZONE_COMBAT_PULSE gates on the map template's type (`Map::IsDungeon`: instance or raid, not a battleground), skips players the
  creature cannot attack, and a creature with no victim attacks the closest unit of its threat list (`AttackClosestEnemy`).
* FRIENDLY_HP leaves the creature itself out when one of the row's actions casts at the event target a spell with
  SPELL_ATTR_EX_EXCLUDE_CASTER (cmangos `friendlyHp.targetSelf`; 14 z2815 rows, for example 198303 with spell 3477).
* `refresh` no longer migrates silently (`--migrate`, above); the script also hashes the `-wal` copy and checks `SHA256SUMS`.
* Logs: `red2/` (the new tests failing on fd2095b6: 7 EventAI cases and the refresh schema gate) and `green2/`.
