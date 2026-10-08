# Instance maps: every map and area in the world database (wave 4, 2026-10-08)

Lane `claude/w4-instance-maps`, from `08ea5ff6` (main, live). Logs: `D:/ArcaneCore-lanes/_logs/w4-instance-maps/`. No schema change (the
reserved world 42 is unused); nothing was pushed or deployed; the live databases and servers were only read.

## The problem

`map_template` and `area_template` held only the continents: `import-map-dbc` read maps 0 and 1 of Map.dbc and the continents' areas
(970 of AreaTable's 1081). The live world therefore skipped 47 dungeon and raid portals (`areatrigger_teleport N: unknown target map M`,
maps 33 to 533) and 56 graveyard links (`game_graveyard_zone has a record for the not existing zone id Z`, instance and battleground
zones), and no instance map could be created: the dungeon scenario stopped at "map 36 is not a known dungeon".

## What changed

* `MapDbcReader` reads every Map.dbc row: id, instance type (field 2, the values of vmangos `map_type`), enUS name (field 4), linked
  zone (field 19). An unknown type or a repeated id is refused; the continents must be common maps.
* `InstanceTemplateDumpImporter` reads what Map.dbc lacks (parent, player limit, reset delay, ghost entrance, script name) from
  classic-db `instance_template` or vmangos `map_template` (newest patch up to 10). cmangos' unsigned (0, 0, 0) ghost entrance is
  vmangos' -1. Blackrock Spire (229) gets vmangos' reset delay 0 instead of classic-db's 3 (see "Blackrock Spire" below); the change is
  printed as `instance data: map 229 reset_delay 3 -> 0 (vmangos: Blackrock Spire has no global reset)`.
* `MapAreaDbcImporter` writes every map and every area (vmangos keeps the whole AreaTable, including the two areas of maps 17 and 150
  that Map.dbc does not list) and replaces both tables as a whole; parents must exist and must not cycle.
* `arcane-content-importer refresh` replaces `map_template` and `area_template` when `--dbc-dir` holds Map.dbc and AreaTable.dbc
  (one without the other is refused, nothing written) and checks two more references the world checks at start: portals to a map with
  no `map_template` row, graveyard links to a zone with no `area_template` row.
* `import-map-dbc` imports every map and area too and takes `--dump <file>` for the instance rows. Without `--replace` it now refuses
  any existing `map_template` or `area_template` row (before, only continent rows were refused and custom rows of other maps were kept;
  the test `Replace_ImportsBothContinentsAndPreservesCustomOtherMapRows` was replaced). The live world had only the two continent rows,
  and all 970 of its area rows are in the refreshed copy unchanged, so nothing is lost there.
* `tools/content/refresh-world-content.ps1` requires Map.dbc and AreaTable.dbc next to AreaTrigger.dbc and WorldSafeLocs.dbc (the
  `SHA256SUMS` check covers all four; the mpqcli path extracts all four).

No world code changed: the world already loads every `map_template` row and creates instances for dungeon, raid and battleground maps.

## Tests

RED first, on `08ea5ff6` with the new tests (`red-data.log`, `red-world.log`): 8 of 24 Data tests failed (the reader returned only maps 0
and 1, accepted an unknown type and a repeated dungeon id; `import-map-dbc` printed "2 continent maps" and refused `--dump`; the refresh
wrote no `map_template` and named no unresolved portal) and `DungeonImportedMapsTests` failed at "map 36 is not a known dungeon
(map_template)". The tests that need the new API (`InstanceTemplateDumpImporterTests`, the rewritten `MapAreaDbcImporterTests`) were held
out of that build. GREEN: `green-data-1.log` 33 of 33, `green-world-1.log` 5 of 5 (the new test and the four `DungeonScenarioTests`).

* `tests/ArcaneCore.Data.Tests/Maps/MapAreaDbcReaderTests.cs`, `MapAreaDbcImporterTests.cs`, `InstanceTemplateDumpImporterTests.cs`
  (real classic-db and vmangos tuples), `ContentImport/Cli/MapAreaDbcCliTests.cs`, `ContentImport/Cli/RefreshCliTests.cs` (the map
  tables, the two new checks, Map.dbc without AreaTable.dbc refused).
Full runs after the final build (`dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors,
`build-final.log`), `--no-build`, Release, SQLite: Data 1279 passed, 9 skipped, 0 failed of 1288 (`test-Data-final.log`); World 2511
passed, 2 skipped, 0 failed of 2513 (`test-World-final.log`). The skips are the existing env-gated real-data probes. After the
Blackrock Spire rework (full build `rework1/build-green.log`: 0 warnings, 0 errors, no MSB3491): Data 1281 passed, 9 skipped, 0 failed of
1290 (`rework1/test-Data-full.log`); World 2512 passed, 2 skipped, 0 failed of 2514 (`rework1/test-World-full.log`).

* `tests/ArcaneCore.World.Tests/Playerbots/Scenarios/DungeonImportedMapsTests.cs`: end to end, `refresh` writes the map tables of a
  SQLite world from synthetic Map.dbc/AreaTable.dbc and classic-db's Deadmines rows, the world reads them through the EF store, and the
  `dungeon` scenario (two grouped bots, trigger 78 into one instance, relog inside, trigger 119 out) passes.

## The live world, on copies

`copy_live.py`: `world.db`, `auth.db` and `characters.db` of the live profile copied through the SQLite backup API from read-only
connections (`copies/copies.json`). The runs below used the build of `baf865cb` (the later commit only turns a malformed
`ghostEntranceMap` into a schema error). `rehearsal.py before|after`: a profile from the live `appsettings.json` with only the database
paths, World 18085, Auth 13724, the realm address and the playerbot keys (`RestoreOnStartup` false, `MaxBots` 16, `AllowedMaps`
[0, 1, 36], scenarios on) changed; this lane's Realm and World; the operator account `CLAUDEOP1` of the copy promoted with a random
password, `.playerbot scenario run dungeon` sent through `arcane-mock live`, then `.server shutdown 5`.

| | before (copy as live) | after (`refresh-world-content.ps1`) |
|---|---|---|
| `map_template` / `area_template` rows | 2 / 970 | 44 / 1081 (33 maps with instance rows) |
| world log "loaded ... maps, ... areas" | 2 maps, 970 areas | 44 maps, 1081 areas |
| `unknown target map` warnings | 47 | 0 |
| `not existing zone id` warnings | 56 | 0 |
| graveyard-zone links loaded | 135 | 191 |
| `dungeon` scenario | FAIL at step 1: "map 36 is not a known dungeon (map_template)" | PASS, 16 steps: the leader enters a new instance of map 36 through trigger 78, the group is bound to it, the member lands in the same instance, party chat, the member relogs inside into the same instance, both leave through 119 |
| World exit | 0 (graceful) | 0 (graceful) |

The after-run's world log has "VMaps: map 36 tree loaded" and "MMaps: map 36 navmesh parameters loaded" when the instance is created.
Warnings new in the after-run belong to grids the bots now visit: two spawns with entry 0 (classic-db spawn groups, not implemented;
docs/areas/creature-movement-spawns.md) on maps 0 and 36. One "database is locked" retry of a rested-state write appears in both runs; the
copies were made with `journal_mode=DELETE`, the live databases run in WAL (the live world log has none).

`refresh_compare.py` (`refresh-compare/compare.json`): the refresh run on a fresh copy of the live world changes exactly `map_template`
(2 -> 44 rows) and `area_template` (970 -> 1081); the other 97 tables, the 14 earlier refresh tables included, are identical. A second run
changes nothing.

Against vmangos' own `map_template` (its 1.12 rows, `cmp-instances-vmangos.txt`), the 44 imported maps differ only in: the battleground
player limits (classic-db 0, vmangos 40/10/15; neither core caps a battleground map by them), map 37 Azshara Crater's limit (unused),
Onyxia's script name (classic-db `instance_onyxias_lair`) and the DBC's spelling `<unused>StormwindPrison`. Blackrock Spire's reset
delay, the one difference a player would notice, now follows vmangos (below).

## Blackrock Spire (rework after review)

classic-db z2815 has `instance_template` (229, 0, 55, 0, 10, **3**, ...) for Blackrock Spire, a dungeon (map type 1). The world schedules
a global reset for every dungeon or raid with a reset delay (`InstanceManager.InitializeRaidSchedules`), and the global reset unbinds
everyone, resets the loaded map and sends every player inside to their homebind (`GlobalRaidReset`): with 3, anyone in Upper Blackrock
Spire would be sent out every three days. vmangos removed that on purpose: `sql/old_migrations/20170917193208_world.sql`, "Blackrock
Spire no reset", `UPDATE map_template SET ResetDelay=0 WHERE Entry=229`, and every 229 row of its `20171129015531` `map_template` has 0
(cmangos keeps 3). `InstanceTemplateDumpImporter` now applies vmangos' value for 229 to any dump that differs and lists the change
(`Corrections`); `refresh` and `import-map-dbc` print it. A vmangos dump already has 0 and produces no correction.

RED (`rework1/red-data.log`, `rework1/red-world.log`, on `416b2add` with the new tests): the importer kept 3 ("Expected (0, 10, 0, 0,
...) Actual (0, 10, 3, 0, ...)"), the refresh printed no correction, and on the refreshed tables the world had a global reset of map 229
scheduled ("Blackrock Spire has a global reset scheduled at 1791691200"). GREEN after the fix (`rework1/green-data.log` 35 of 35,
`rework1/green-world.log` 6 of 6). Tests: `InstanceTemplateDumpImporterTests.ClassicDb_BlackrockSpire_GetsVmangosNoReset_AndOtherResetDelaysStay`
(Molten Core's and Naxxramas' 7 stay), `RefreshCliTests.Refresh_GivesBlackrockSpireNoResetDelay_AsVmangosDoes_AndKeepsTheRaidsOwn`,
`DungeonImportedMapsTests.RefreshedMapTables_ScheduleNoGlobalResetForBlackrockSpire_ButTheRaidsOwn` (the world schedules Naxxramas'
reset and none for 229).

On the live-world copy (`rework1/refresh_compare2.py`, `rework1/refresh-compare-b/compare.json`): the refresh still changes exactly
`map_template` (2 -> 44) and `area_template` (970 -> 1081), a second run changes nothing, and against the first lane run the only
different value is map 229 `ResetDelay` 3 -> 0. The maps left with a reset delay are the seven raids, as in vmangos: Onyxia 5,
Zul'Gurub 3, Molten Core 7, Blackwing Lair 7, Ruins of Ahn'Qiraj 3, Ahn'Qiraj Temple 7, Naxxramas 7.

## Code paths the full map table turns on

`map_template` now holds the battleground maps (30, 489, 529) and every raid, as vmangos does. Code that was dormant on live because
those maps were missing becomes active: `InstanceManager.IsBattlegroundMap` routing at login, the battleground branches of
`GraveyardRepopService` and `MapCombat`, and the raid global reset schedules, which the World writes to the characters database at its
first start. The rehearsal on the live copy exercised only the dungeon scenario; battleground and raid entry are covered by the unit and
World test suites but have not been tried on the live data. Watch for them after the deploy.

## Data

Map.dbc (44 maps, sha256 `4bb6b538...73654245`) and AreaTable.dbc (1081 areas, `ac4c33c2...0418a3528`) are not in
`D:/refs/client-dbc-5875-effective`. They are copied (not re-extracted) from the 2026-10-05 effective set
(`playable-content-20261005/DBFilesClient-5875`, whose `effective-dbc-candidate.json` records both from patch-2.MPQ with these hashes)
into `D:/ArcaneCore-lanes/w4-instance-maps-data/client-dbc-5875-effective/` together with the three DBCs of the refs directory and a
`SHA256SUMS` for all five. Adding the two files to `D:/refs/client-dbc-5875-effective` was not done from this lane (a shared reference
directory); see the deploy steps.

## Deploy

The world schema does not change. The refresh-world-content script now needs Map.dbc and AreaTable.dbc in `-DbcDirectory`:

1. Put Map.dbc and AreaTable.dbc where the refresh reads them. Until then the wave-3 refresh command fails before it writes anything
   (fail-closed). Recommended, and durable: copy `Map.dbc` and `AreaTable.dbc` from
   `D:\ArcaneCore-lanes\w4-instance-maps-data\client-dbc-5875-effective` into `D:\refs\client-dbc-5875-effective` and append their two
   lines from that directory's `SHA256SUMS` (needs Nathan's approval: a shared reference directory). The alternative, passing
   `-DbcDirectory D:\ArcaneCore-lanes\w4-instance-maps-data\client-dbc-5875-effective` (`deploy_w3.py --dbc-dir` the same; it also sets
   `GameObjects:TransportAnimationDbcPath` from that directory, an identical file), works but points production at a lane folder that
   may be cleaned up.
2. Run the refresh as before (World stopped). Expected: four "matches SHA256SUMS" lines, "Map.dbc: 44 map(s), AreaTable.dbc: 1081
   area(s); 33 map(s) with instance data from the dump", "instance data: map 229 reset_delay 3 -> 0 (vmangos: Blackrock Spire has no
   global reset)", `map_template 44` and `area_template 1081` among the replaced tables, "refresh: every checked reference resolves", and
   three warnings (maps 29, 44 and 269 have no instance row).
3. Start the World: "loaded 44 maps, 1081 areas", "loaded 122 graveyards and 191 graveyard-zone links", no "unknown target map" and no
   "not existing zone id" lines.

Rollback: the refresh's backup (`world.db.<stamp>.bak`) restores the two tables with everything else.
