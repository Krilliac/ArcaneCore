# DB script engine follow-up (2026-10-08)

Worktree-only report. The source is ClassicDB `ClassicDB_1_12_1_z2815.sql.gz`; the table counts below were obtained by streaming its `INSERT`/`REPLACE` statements and grouping the first (script id) and fourth (command id) numeric columns. No source code was copied from vmangos or mangos-classic.

## Implemented

- `SPELL_EFFECT_SEND_EVENT`, chest `data6` and goober `data2` all start an event through `ScriptedEvents.Start` (cmangos `StartEvents_Event`): the ScriptDev2 handler of the event answers first (the map's `InstanceData.OnSpellEvent`, or `ScriptedEvents.GatedOff` for a handler with no instance-script port), and only when it declines does the event's `dbscripts_on_event` script start, with the caster (or user) as source and the effect target (or object) as target. See "Second rework" below for the 14 ClassicDB events this gates. cmangos `Spell::EffectSendEvent` (`src/game/Spells/SpellEffects.cpp:1794-1799`), `StartEvents_Event` (`src/game/DBScripts/ScriptMgr.cpp:3445-3482`), `GameObject::Use` (`src/game/Entities/GameObject.cpp:1548-1560, 1683-1688`), reached for a chest by key or lockpick through `Spell::SendLoot` (`src/game/Spells/SpellEffects.cpp:2142-2145`); vmangos `Spell::EffectSendEvent` (`src/game/Spells/SpellEffects.cpp:1761-1775`). The goober event starts before the goober `questId` gate as in cmangos; vmangos `GameObject::Use` (`src/game/Objects/GameObject.cpp:1564-1580`) starts it only after the gate. The chest event starts only once the chest passed the quest gate and the lock and opened. A button has a linked-trap field, not an event-id field (`src/game/Entities/GameObject.h:61-72`).
- The relay executor now dispatches command ids 2 FIELD_SET, 4 FLAG_SET, 5 FLAG_REMOVE, 9 RESPAWN_GAMEOBJECT, 12 CLOSE_DOOR, 14 REMOVE_AURA, 16 PLAY_SOUND, 17 CREATE_ITEM, 23 MORPH_TO_ENTRY_OR_MODEL, 24 MOUNT_TO_ENTRY_OR_MODEL, 27 GO_LOCK_STATE, 34 TERMINATE_COND, 40 DESPAWN_GO, 43 RESET_GO, 44 UPDATE_TEMPLATE and 48 MODIFY_UNIT_FLAGS. Command 11 OPEN_DOOR also accepts its object source when the row has no spawn GUID. The cmangos definitions and behavior are `src/game/DBScripts/ScriptMgr.h` (`ScriptCommands` enum) and `ScriptMgr.cpp:1829-1924, 2001-2154, 2203-2256, 2411-2525, 2720-2758, 2887-3016`. No GPL code was copied.
- Row `condition_id` was already checked per source/target pair in `CreatureMapSystem.RelayScripts.cs` and uses the loaded `conditions` table via `IConditionEvaluator`. Command 34 now uses it for flow termination. cmangos `ScriptAction::HandleScriptStep` and `ExecuteDbscriptCommand` (`ScriptMgr.cpp:1704-1769, 2720-2758`). The first version left 41 of the 44 loaded command-34 rows undecidable (they are about the map, not a player); the second rework below decides them.
- World schema 43 adds `creature_template.ScriptName`; the importer, content store, named AI factory and refresh path carry it. vmangos `FactorySelector::selectAI` (`src/game/AI/CreatureAISelector.cpp:37-50`). Named factories registered in ArcaneCore run before `AIName`. An unregistered name still falls through to the existing entry or default AI.
- A quest shared by a player uses that player as the DB script source. An inventory-item starter runs with no source and the player as target, so only steps whose buddy search finds a source run, as in cmangos `ScriptAction::HandleScriptStep` (`src/game/DBScripts/ScriptMgr.cpp:1720-1760`, an item is no world object); vmangos `Player::AddQuest` (`src/game/Objects/Player.cpp:12889-12891`) runs no start script for an item. cmangos `Player::AddQuest` (`src/game/Entities/Player.cpp:12517-12535`).

## ClassicDB command-id inventory

The first two numeric columns are the number of rows and distinct script ids in the dump. The last two count ids for which **every row has a dispatch case** before and after this change. These are *upper bounds on fully supported scripts*, not a claim that all target, flag, trigger or persistence variants work. Tables other than quest start/end, gossip, event and relay are not imported by world 43.

| Table suffix after `dbscripts_on_` | Rows | IDs | All commands dispatched before | After | Undispatched commands after |
|---|---:|---:|---:|---:|---|
| creature_death | 44 | 25 | 22 | 24 | 53 |
| creature_movement | 2,050 | 602 | 535 | 575 | 37, 39, 42, 47, 53, 54 |
| event | 453 | 138 | 74 | 138 | none |
| go_template_use | 169 | 89 | 53 | 88 | 47 |
| go_use | 37 | 31 | 29 | 31 | none |
| gossip | 403 | 259 | 203 | 239 | 52, 200 |
| quest_end | 1,892 | 265 | 188 | 255 | 37, 38, 42, 47, 53 |
| quest_start | 629 | 87 | 69 | 85 | 6, 53 |
| relay | 828 | 198 | 176 | 187 | 37, 39, 42, 51, 52, 53 |
| spell | 32 | 21 | 19 | 21 | none |

The unique command ids in the loaded five tables still without a dispatch case are 6, 37, 38, 39, 42, 47, 51, 52, 53 and 200. The other five dump tables need their own import and trigger paths. The inventory does not count relay-template choice rows as script steps.

## Remaining limits

- This is **not complete DB script support**. There is no honest count of fully supported ids yet: the table above only proves that every command id has a branch. Several branches still support a subset of cmangos parameters. In particular, command 9 respawns a static spawn waiting on its timer and shows a not-spawned-by-default spawn (211 of the 212 spawns ClassicDB's loaded tables name) for datalong2 seconds, or for good with datalong2 0 (cmangos); 14 removes a whole spell but not one charge/stack or one caster; 16 supports the direct/distance forms used by the scanned rows, not map/zone/area/music variants; 17 needs a valid item template and free inventory space; 21 is a no-op because ArcaneCore has no active-object grid state; 40 relies on the current object system's despawn policy; 44 uses the existing template update path. Existing relay limitations listed in `docs/areas/creature-ai.md` still apply.
- A SEND_EVENT spell uses its explicit game-object target when present and dispatches once per cast effect; a spell-focus object selected implicitly is not represented by `SpellEffectContext`. A restocking chest (GO_NOT_READY) refuses the open and so starts no chest event, where cmangos would start it with an empty loot window. These need target and result tests against a real 5875 client.
- Of the 50 ClassicDB `scripted_event_id` handlers, only the gates of the 14 that have `dbscripts_on_event` rows are ported. The other 36 have no DB rows, so their events still do nothing beyond what an instance script already ports (an event with no handler and no rows is logged as an unsupported send event). The five gate-only ports (`ScriptedEvents.GatedOff`) carry the handler's return value, not its side effects: no furbolg purification for 3938, no possess visual for 8302, no J'eevee ritual for 8420/8428 (so the Dreadsteed summon stays shut), no zombie chow movement for 10495.
- TERMINATE_COND and row conditions decide the map types 36, 37 and 39 (and AND/OR/NOT over them) in the script engine. DEAD_OR_AWAY mode 1 reads the online members `IScriptQuestEvents.GroupMembersOf` returns (cmangos walks every member). A condition the evaluator still cannot decide (a missing collaborator) stops a reversed (`data_flags` 8) TERMINATE_COND but never fails its quest; cmangos has no undecidable case. ClassicDB's two player rows (9527 aura, 3141 quest taken) depend on the world's aura and quest collaborators, which the world wires.
- A named ScriptDev2 AI runs only if the name is registered in ArcaneCore. ClassicDB names for unported scripts remain data with the default AI. Item-start scripts have no source object (the item-only field commands do not run). No full ClassicDB refresh, server startup, multiplayer client run or persistence test was performed in this lane.
- No third-party code was imported; `THIRD_PARTY_NOTICES.md` did not change.

## Files changed

- Data and schema: `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.Refresh.cs`, `src/ArcaneCore.Data/Content/Import/Spec/ContentTableSpecs.cs`, `src/ArcaneCore.Data/World/Creatures/{CreatureDataModule,CreatureDumpImporter,CreatureScriptNameDataModule,EfCreatureDataStore}.cs`, `src/ArcaneCore.Kernel/WorldData/Creatures/CreatureContent.cs`.
- Game: `src/ArcaneCore.Game/Creatures/AI/CreatureAiServices.cs`, `src/ArcaneCore.Game/Creatures/{CreatureMapSystem.RelayScripts,CreatureMapSystem.RelayConditions,CreatureQuestScripts}.cs`, `src/ArcaneCore.Game/Creatures/Scripts/RelayScriptRunner.cs`, `src/ArcaneCore.Game/Conditions/{ConditionEvaluator,IConditionTableEvaluator}.cs`, `src/ArcaneCore.Game/GameObjects/{GameObjectMapSystem,GameObjectMapSystem.DbEvents}.cs`, `src/ArcaneCore.Game/Instances/Scripts/{InstanceData,ScriptedEvents,DungeonScriptHooks}.cs`, `src/ArcaneCore.Game/Instances/Scripts/ZulFarrak/ZulFarrakInstance.cs`, `src/ArcaneCore.Game/Spells/{SpellCast,SpellSystem.Effects}.cs`.
- World: `src/ArcaneCore.World/Npc/ConditionFeature.cs` (implements `IConditionTableEvaluator`).
- Tests: `tests/ArcaneCore.Data.Tests/ContentImport/Core/TableSpecDriftTests.cs`, `tests/ArcaneCore.Data.Tests/{CreatureDataTests,IntegratedSchemaTests}.cs`, `tests/ArcaneCore.Game.Tests/CreatureAi/{CreatureAiHostTests,EventAi/DbScriptRuntimeTests,EventAi/RelayScriptCommandTests,EventAi/TerminateCondMapConditionTests}.cs`, `tests/ArcaneCore.Game.Tests/Spells/SendEventDbScriptTests.cs`, `tests/ArcaneCore.Game.Tests/GameObjects/{ChestEventScriptTests,GameObjectTypeRig,ScriptedEventStartTests}.cs`, `tests/ArcaneCore.Game.Tests/Instances/{DungeonScriptHarness,DungeonScriptHooksTests,ZulFarrakScriptTests}.cs`.
- Documentation: `docs/areas/creature-ai.md`, `docs/reference/schema.md`, this report.

## Verification

Release `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` passed with zero warnings and errors after a cached restore. Final full Game: 7,538 passed, 13 skipped. Data excluding `Resilience`: 1,211 passed, 15 skipped; the unfiltered run found a now-fixed content-spec omission and then stayed silent for several minutes, so it was stopped. A full World run passed 3,161, skipped 29 before the last SEND_EVENT and conditional-failure edits. The next full World run passed 3,160, skipped 29 and failed `LiveFxCommandTests.Lookup_FindsByIdAndByNamePart_InEveryTable`; that test passed alone, and the final affected World filter passed 14/14 after the last build. Focused Data table-spec and ScriptName refresh checks passed 31/31. `git diff --check` returned 0. Real-client acceptance and a full green World run on the final diff remain pending.

## Intake review (2026-10-08)

The coordinator's intake rebuilt and re-ran this diff and changed three things:

- **Command 9 was a no-op on ClassicDB data.** It only respawned objects already waiting on a respawn timer, but 211 of the 212
  `gameobject` spawns that command 9 names in the five loaded tables have a negative `spawntimesecs` (never spawned by default, so no timer).
  It now follows cmangos (`ScriptMgr.cpp:2001-2047`): a static spawn respawns; a hidden one is shown and despawns again after `datalong2`
  seconds. Test: `RelayScriptCommandTests.RespawnGameObject_ShowsAHiddenSpawnForItsDespawnDelay`.
- **Item-started quests** ran every step with the player as source, which neither reference does (vmangos runs none; cmangos runs only
  buddy-sourced steps). `CreatureMapSystem.StartDbScript` now accepts a null source and the item path uses it. Test:
  `DbScriptRuntimeTests.ScriptWithoutAWorldSource_RunsOnlyTheStepsWhoseBuddyIsTheSource`.
- `IntegratedSchemaTests` pins the world schema literal 43 again instead of comparing the module constant with itself.

The before/after table above was re-derived independently from the dump and matches. SEND_EVENT dispatches once per effect even when
the effect has several unit targets; vmangos calls the handler per target, which matters only for a multi-target SEND_EVENT spell.

## Rework after intake review (2026-10-08)

- **Chest event on the spell path (material).** `GameObjectMapSystem.OpenLock` opened a chest with `OpenChest` and never started
  `chest.eventId` (data6), but cmangos `Spell::SendLoot` (`SpellEffects.cpp:2142-2145`) passes a chest to `GameObject::Use`, which starts
  it (`GameObject.cpp:1548-1560`). In ClassicDB z2815 all 14 chests whose data6 event has `dbscripts_on_event` rows are locked (events 259,
  264, 383-385, 415-417, 498, 619, 5225, 5300, 5301, 8175), so none of them ran. Both paths now go through `OpenChestStartingEvent`, which
  starts the event only after the chest actually opened. The direct use used to start it before the quest gate and the lock check, so a
  refused use sprang the ambush; it no longer does. Tests: `ChestEventScriptTests` (key opening through `OpenLock`, a wrong key, the direct
  use without and with the key, the chest quest gate).
- **Command 9 with datalong2 0 (material).** The intake fix queued the despawn at `_clockMs + 0`, so the shown object left on the next tick.
  ClassicDB has three such rows (`dbscripts_on_event` 466-468, the Water Well Cleansing Aura 2904, spawntimesecs -180, quests 754/758/760).
  It now follows cmangos: `SetRespawnTime(0)` leaves `m_respawnDelay` 0, which `IsSpawned()` reads as spawned (`GameObject.h:757-768`),
  so no despawn is scheduled and the object stays. vmangos would clamp to 5 s (`ScriptCommands.cpp:383`). Showing an object also drops a
  stale pending despawn for it, as a later `SetRespawnTime` replaces the earlier one. Test:
  `RelayScriptCommandTests.RespawnGameObject_WithoutADespawnDelay_LeavesTheShownSpawnInTheWorld`.
- **Goober citation.** The goober event ordering (before the questId gate) is cmangos `GameObject.cpp:1683-1688` (event) and `:1691-1696` (gate); vmangos
  (`GameObject.cpp:1564-1580`) gates first. The code comment, `docs/areas/creature-ai.md` and this report now cite cmangos.
- **ScriptName selection is inert for now (note, no change).** Nothing in `src` calls `CreatureAiFactory.Register`, so no production
  creature takes the ScriptName branch; ClassicDB's 294 distinct ScriptNames do not collide with an AIName key, so it is safe.
- **Command 48 toggle.** Datalong2 2 now follows cmangos (`ScriptMgr.cpp:2993-3016`): `HasFlag` is true for any bit of the mask
  (`Object.h:476-480`), and then the whole mask is removed; otherwise it is set. No ClassicDB row uses the toggle. Test:
  `RelayScriptCommandTests.ModifyUnitFlags_Toggle_RemovesTheWholeMaskWhenAnyBitIsSet_AndSetsItOtherwise`.
- **Command 17 destroy.** The additional flag (destroy) already worked; it now has a test for its one ClassicDB row (`dbscripts_on_gossip`
  7166): `RelayScriptCommandTests.CreateItem_WithTheAdditionalFlag_DestroysTheCount`.
- **SEND_EVENT noise.** `EffectSendEvent` reported "send event unsupported" whenever `StartDbScript` returned false, including an event
  already running for the same caster and target. It now reports only an event without rows (`CreatureMapSystem.HasDbScript`); cmangos
  `Map::ScriptsStart` (`Maps/Map.cpp:2181-2193`) skips a running script as a success. Test:
  `SendEventDbScriptTests.SendEventSpell_AnEventAlreadyRunning_IsNotReportedUnsupported_AMissingOneIs`.
- Schema unchanged by the rework: world 43 is still the single `creature_template.ScriptName` column (auth 5, characters 42).
- Every new test except the command 17 one (coverage of existing behaviour) was seen failing with its fix reverted (7 failures: the 6 new
  chest, command 9, command 48 and SEND_EVENT tests, plus `SendEventSpell_StartsItsEventDbScript` under the SEND_EVENT revert), then all green with the fixes back.
- Rework verification: Release `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` 0 warnings, 0 errors (fresh DLL
  timestamps). Full Game: 7,548 passed, 13 skipped, 0 failed. Full World: 3,161 passed, 29 skipped, 0 failed. Data was not rerun (the
  rework touches no Data code or schema). `git diff --check` clean.

## Re-verification of the reworked tip (2026-10-08)

A second intake pass rebuilt tip `12d974b7` and re-ran every touched test project in full; no code changed.

- Release `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors (fresh DLL timestamps).
- Full Game: 7,548 passed, 13 skipped, 0 failed. Full World: 3,161 passed, 29 skipped, 0 failed. Full Data (including the
  `Resilience` group that stalled in the lane run, with a 5-minute hang timeout that did not fire): 1,341 passed, 15 skipped, 0 failed.
  Kernel: 32 passed.
- Mutation check: with the chest `StartDbEvent`, the SEND_EVENT `StartDbScript` call, the TERMINATE_COND polarity and the command 9
  despawn delay broken together, 10 of the 39 tests in `ChestEventScriptTests`, `SendEventDbScriptTests`, `RelayScriptCommandTests` and
  `DbScriptRuntimeTests` failed (3 chest, 2 SEND_EVENT, 4 TERMINATE_COND, 1 command 9); all 39 passed with the code restored.

## Second rework after review (2026-10-08)

A second review read the reworked tip against cmangos, the ClassicDB z2815 dump and the 5875 `Spell.dbc`. Each point was fixed or
checked against the reference:

- **Zul'Farrak pyramid wave ran twice (material, fixed).** Unlocking (10738: effect 61 SEND_EVENT, misc value 2609) made
  `EffectSendEvent` start the `dbscripts_on_event` 2609 script (Zul'Farrak had no `OnSpellEvent`), and `DungeonScriptHooks.OnSpellFinished`
  started its relay copy (`RelayScriptCatalog.EventRelayId(2609)`) as well; later casts restarted the DB script each time.
  `ZulFarrakInstance.OnSpellEvent` now ports `ProcessEventId_event_spell_unlocking` and `_event_go_zulfarrak_gong` (`zulfarrak.cpp:39-75`):
  for a player source it starts the relay copy on the first allowed start and returns true every time, so the DB script never runs a
  second wave; another source gets false and the DB script, as in cmangos. The completed-cast hook no longer handles 10738. Test:
  `ZulFarrakScriptTests.UnlockingCast_AgainstTheImportedEvent2609Rows_SpawnsASingleWave` (casts a 10738-shaped spell twice through
  `SpellSystem` against 2609 rows in both the event catalog and the relay block; one summon in all).
- **Unported ScriptDev2 gates (material, fixed).** Every event start now goes through `ScriptedEvents.Start` (cmangos
  `StartEvents_Event`, `ScriptMgr.cpp:3445-3482`): the instance's `OnSpellEvent`, then `ScriptedEvents.GatedOff`, then the DB script. Of
  the 50 `scripted_event_id` handlers, 14 events have `dbscripts_on_event` rows: 2488 and 2609 (Zul'Farrak), 4884 (Blackrock Spire; it
  returns true for any source, where cmangos lets a non-player source reach the DB script) and 5618-5623 (Scholomance, which stands in
  for the rows) were already instance scripts; the other five now keep their cmangos return value: 3938 `event_purify_food`
  (`darkshore.cpp:826-860`: false only for a player at a game object), 8302 `event_razorgore_possess` (`boss_razorgore.cpp:216-225`:
  always true), 8420 and 8428 `event_spells_warlock_dreadsteed` (`dire_maul.cpp:43-80`: true unless TYPE_DREADSTEED allows the step,
  which the Dire Maul port never sets) and 10495 `event_naxxramas` (`naxxramas.cpp:916-951, 1140-1146`: false only while Gluth is in the
  instance; Gluth on the map stands for the storage). Goober and chest events share the path. Test:
  `ScriptedEventStartTests.UnportedScriptDev2Handlers_LetTheDbScriptRunOnlyWhereCmangosDoes`.
- **TERMINATE_COND rarely decided (minor, fixed rather than documented).** 41 of the 44 loaded command-34 rows use 36 DEAD_OR_AWAY (38
  quest_start rows of the escorts 667, 1090, 3382, 3982, 5713), 39 SPAWN_COUNT (quest_start 6148, quest_end 252) or an OR over 37
  CREATURE_IN_RANGE (quest_start 3566, gossip 1282). `CreatureMapSystem.RelayConditions` now decides those types (and AND/OR/NOT over
  them) with the script's objects (`Conditions.cpp:299-308, 424-466, 484-489`; the COUNT_SPAWNS extra flag, `Creature.cpp:208-209,
  594`), and TERMINATE_COND passes the player as target and the other object as source with a null player allowed
  (`ScriptMgr.cpp:2720-2758`). The table is read through the new `IConditionTableEvaluator`, which the world's forwarding
  `ConditionFeature` implements; without it the world would never have reached the new code. Tests:
  `TerminateCondMapConditionTests` (8 cases, through a forwarding evaluator of the world's shape).
- **Event uniqueness (minor, disproved and corrected the other way).** The review read `StartEvents_Event` as unique by target for a
  player at a goober. `TYPEMASK_CREATURE_OR_GAMEOBJECT` is `TYPEMASK_UNIT | TYPEMASK_GAMEOBJECT` (`ObjectGuid.h:45-52`) and a player
  carries `TYPEMASK_UNIT` (`Unit.cpp:334`, `Player.cpp:481`; `isType` is a bit test, `Object.h:374`), so the first branch
  (`SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE`) fires for any player, creature or object source; only a source that is none of those (an item)
  falls to the by-target branch. Two players at the Altar of the Tides (event 364) therefore each get their own Gazban in cmangos too.
  The lane's per-(source, target) dedupe was still wrong for one player at two objects with the same event; `StartDbScript` now applies
  cmangos' modes for event scripts (`RelayScriptRunner` `DbScriptUniqueness`). Test:
  `ScriptedEventStartTests.GooberEvent_RunsOncePerPlayer_AndEachPlayerStartsItsOwn`.
- **Goober event before the in-use refusal (minor, fixed).** `UseGoober` now refuses an in-use goober (activated, auto-close time not run
  out) before its page, gossip and event, as the chest path refuses before its event; the event still precedes the questId gate as in
  cmangos. Test: `ScriptedEventStartTests.GooberInUse_RefusesAnotherUse_BeforeItsEventStarts`.
- **Citations (nit, fixed).** The command comments and both documents now cite the cmangos `ScriptMgr.cpp` case ranges as they are:
  2/4/5 1829-1924, 9 2001-2047, 14 2130-2154, 16 2203-2240, 17 2241-2256, 22 2399-2410, 23 2411-2429, 24 2430-2459, 27 2499-2525,
  34 2720-2758, 40 2887-2895, 43 2930-2947, 44 2948-2960, 45 2961-2972, 48 2993-3016. Command 2 FIELD_SET on an item source stays
  unsupported, as noted above.
- **Schema:** unchanged by this rework. World 43 is still this lane's single change (`creature_template.ScriptName`, pinned in
  `IntegratedSchemaTests`); auth 5, characters 42. If movement-scripts also took world 43, the integrator renumbers one of them.
- **Proof the new tests bite:** each fix was reverted on its own and the new tests failed: the lane's previous ZF/SEND_EVENT code (2
  failures: the single-wave cast test and the rewritten hook test), no gates plus no uniqueness modes (2), event before the in-use
  refusal plus the map condition types off (6), and the table read only from a bare `ConditionEvaluator` instead of the forwarding
  interface (5). All 24 tests of the touched classes passed with the code restored.
- **Verification:** Release `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors, fresh DLL
  timestamps. Full Game: 7,560 passed, 13 skipped, 0 failed (12 new). Full World: 3,161 passed, 29 skipped, 0 failed. Full MockClient:
  467 passed, 0 failed. Data and Kernel were not rerun (no Data, Kernel or schema change in this rework). Static reading for the
  ClassicDB figures above (z2815 `scripted_event_id`, `dbscripts_on_*`, `conditions`, `creature_template` rows, parsed in a scratch
  script); no real-client run.
