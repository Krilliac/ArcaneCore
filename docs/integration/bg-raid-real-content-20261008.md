# Battleground and raid entry on refreshed world content (2026-10-08)

`RealBattlegroundRaidEntryTests` uses a fresh SQLite world made from classic-db z2815 and the
build-5875 client DBCs. The tests copy that database before starting a loopback world; neither
the source database nor the read-only reference trees are changed.

## Build the fixture and run it

From the worktree root in PowerShell:

```powershell
New-Item -ItemType Directory -Force local-data/bg-raid-acceptance | Out-Null
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false
dotnet tools/ArcaneCore.ContentImporter/bin/Release/net10.0/arcane-content-importer.dll import D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz --database local-data/bg-raid-acceptance/world.db
dotnet tools/ArcaneCore.ContentImporter/bin/Release/net10.0/arcane-content-importer.dll refresh D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz --database local-data/bg-raid-acceptance/world.db --dbc-dir D:/refs/client-dbc-5875-effective
$env:ARCANECORE_TEST_WORLD_DB = (Resolve-Path local-data/bg-raid-acceptance/world.db).Path
dotnet test tests/ArcaneCore.World.Tests/ArcaneCore.World.Tests.csproj -c Release --no-build --filter FullyQualifiedName~RealBattlegroundRaidEntryTests
```

The full importer is run without `--dbc-dir` because this DBC set lacks `Lock.dbc`; `refresh`
reads the seven DBCs it needs. The test attribute skips explicitly when
`ARCANECORE_TEST_WORLD_DB` is not set to a refreshed SQLite database. With the variable set,
the tests require 44 imported maps, the three battleground templates, the battlemaster rows,
and the raid triggers. The ignored `local-data/` directory holds the generated database; it is
not a content source to commit.

## Checks and reference behavior

- Two managed clients queue at imported battlemasters for Warsong Gulch, Arathi Basin and
  Alterac Valley, receive the invitation, port into the same match instance, and run the start
  countdown on the manual world clock. The test changes only the minimum team size to one:
  classic-db's 5/8/20 players per side must remain the live rule. References: vmangos
  `BattleGroundMgr.cpp`, `BattleGroundQueue::Update` and `BattleGroundMgr::Update`;
  `BattleGroundHandler.cpp`, `HandleBattlemasterJoinOpcode`, `RequestBgJoinQueue`,
  `HandleBattleFieldPortOpcode`.
- Onyxia's trigger 2848 refuses a player below level 50 and one without Drakefire Amulet
  (16309), then admits the keyed raid. Molten Core's trigger 3528 refuses without rewarded
  quest 7848; after a test journal records that reward, it admits the leader. The other member
  enters through trigger 2886 in Blackrock Depths. They share the group save for each raid.
  References: vmangos `MapManager.cpp`, `MapManager::CanPlayerEnter`; mangos-classic
  `Entities/Player.cpp`, `Player::GetAreaTriggerLockStatus` for level/item/quest gates;
  classic-db z2815 `areatrigger_teleport` rows 2848, 2886 and 3528.
- The imported `map_template` has five-day Onyxia and seven-day Molten Core periods. After
  eight days on the test clock, the global reset removes both group binds, sends members
  home and schedules the next resets. Reference: vmangos `MapPersistentStateMgr.cpp`,
  `DungeonResetScheduler::ScheduleAllDungeonResets`, `Update`, and
  `MapPersistentStateManager::_ResetOrWarnAll`. The world `InstanceFeature` now gives its
  manager the host `TimeProvider` so this path is deterministic in manual-clock tests.
- The reference's two item columns are alternatives. A focused Game test guards that either
  key admits a player and a missing pair reports the first key. Reference: mangos-classic
  `Entities/Player.cpp`, `Player::GetAreaTriggerLockStatus` (the `requiredItem` branch).

## 2026-10-09 portal refresh on an older world copy

The preserved wave12 rehearsal world was imported before the portal requirement columns were populated. It has 103 portal rows, but trigger 2848 stores `RequiredItem=0` and trigger 3528 stores `RequiredQuestDone=0`, despite ClassicDB z2815 carrying 16309 and 7848. The optional real-content World suite therefore fails its Onyxia entrance check against that unmodified snapshot. The scoped `arcane-content-importer refresh-portals` command reads the same dump and replaces only `areatrigger_teleport` in an existing current-schema world. A dry run found 103 rows; a fresh SQLite backup of the wave12 world gained the two requirements while its unrelated table counts and database integrity stayed intact. The full optional-content World suite passed against that refreshed copy. The preserved rehearsal world and any live world were not modified.

## Fixture limits

The supplied DBC directory has no `FactionTemplate.dbc`, so the tests add neutral reaction
records for the ten faction-template IDs used by its battlemasters. The real NPC templates,
spawns, flags and battlemaster mapping still come from the imported database. The full import
also lacks `Lock.dbc`; locked game objects are outside these entry tests. The test host's
synthetic player appearance and spell auras allow managed clients to log in because this
fixture does not import the corresponding character and spell DBCs. To remove those test-only
substitutions, supply the missing effective DBCs and import their data.

vmangos `Handlers/MiscHandler.cpp`, `WorldSession::HandleAreaTriggerOpcode` checks its
teleport row's level and condition; it has no direct `required_item` or `required_quest_done`
check there. Those two columns are present in the tested classic-db rows and are checked by
mangos-classic `Entities/Player.cpp`, `Player::GetAreaTriggerLockStatus`. The acceptance tests
follow the imported rows and this latter reference for Drakefire Amulet and quest 7848.
