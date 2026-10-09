# DB script engine follow-up (2026-10-08)

Worktree-only report. The source is ClassicDB `ClassicDB_1_12_1_z2815.sql.gz`; the table counts below were obtained by streaming its `INSERT`/`REPLACE` statements and grouping the first (script id) and fourth (command id) numeric columns. No source code was copied from vmangos or mangos-classic.

## Implemented

- `SPELL_EFFECT_SEND_EVENT` first offers the event to the instance script, then starts the event namespace with caster and unit target. Chest `data6` and goober `data2` start an event script with the user as source and object as target. cmangos `Spell::EffectSendEvent` (`src/game/Spells/SpellEffects.cpp:1794-1799`), `StartEvents_Event` (`src/game/DBScripts/ScriptMgr.cpp:3445-3478`), `GameObject::Use` (`src/game/Entities/GameObject.cpp:1554-1560, 1682-1688`); vmangos `Spell::EffectSendEvent` (`src/game/Spells/SpellEffects.cpp:1761-1775`) and goober `GameObject::Use` (`src/game/Objects/GameObject.cpp:1572-1577`). A button has a linked-trap field, not an event-id field (`src/game/Entities/GameObject.h:61-72`).
- The relay executor now dispatches command ids 2 FIELD_SET, 4 FLAG_SET, 5 FLAG_REMOVE, 9 RESPAWN_GAMEOBJECT, 12 CLOSE_DOOR, 14 REMOVE_AURA, 16 PLAY_SOUND, 17 CREATE_ITEM, 23 MORPH_TO_ENTRY_OR_MODEL, 24 MOUNT_TO_ENTRY_OR_MODEL, 27 GO_LOCK_STATE, 34 TERMINATE_COND, 40 DESPAWN_GO, 43 RESET_GO, 44 UPDATE_TEMPLATE and 48 MODIFY_UNIT_FLAGS. Command 11 OPEN_DOOR also accepts its object source when the row has no spawn GUID. The cmangos definitions and behavior are `src/game/DBScripts/ScriptMgr.h` (`ScriptCommands` enum) and `ScriptMgr.cpp:1829-1932, 2004-2153, 2204-2255, 2411-2524, 2723-2755, 2873-3020`. No GPL code was copied.
- Row `condition_id` was already checked per source/target pair in `CreatureMapSystem.RelayScripts.cs` and uses the loaded `conditions` table via `IConditionEvaluator`. Command 34 now uses that evaluator for flow termination. cmangos `ScriptAction::HandleScriptStep` and `ExecuteDbscriptCommand` (`ScriptMgr.cpp:1704-1769, 2723-2755`).
- World schema 43 adds `creature_template.ScriptName`; the importer, content store, named AI factory and refresh path carry it. vmangos `FactorySelector::selectAI` (`src/game/AI/CreatureAISelector.cpp:37-50`). Named factories registered in ArcaneCore run before `AIName`. An unregistered name still falls through to the existing entry or default AI.
- A quest shared by a player uses that player as the DB script source; an inventory-item starter uses the accepting player as a map source because inventory items are not `WorldObject`s. cmangos `Player::AddQuest` (`src/game/Entities/Player.cpp:12517-12535`).

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

- This is **not complete DB script support**. There is no honest count of fully supported ids yet: the table above only proves that every command id has a branch. Several branches still support a subset of cmangos parameters. In particular, command 9 handles existing database spawns waiting on a timer; 14 removes a whole spell but not one charge/stack or one caster; 16 supports the direct/distance forms used by the scanned rows, not map/zone/area/music variants; 17 needs a valid item template and free inventory space; 21 is a no-op because ArcaneCore has no active-object grid state; 40 relies on the current object system's despawn policy; 44 uses the existing template update path. Existing relay limitations listed in `docs/areas/creature-ai.md` still apply.
- A SEND_EVENT spell uses its explicit game-object target when present and dispatches once per cast effect; a spell-focus object selected implicitly is not represented by `SpellEffectContext`. The chest event runs on direct use even when loot cannot open. These need target and result tests against a real 5875 client.
- A named ScriptDev2 AI runs only if the name is registered in ArcaneCore. ClassicDB names for unported scripts remain data with the default AI. Item-start scripts do not preserve the item object as source. No full ClassicDB refresh, server startup, multiplayer client run or persistence test was performed in this lane.
- No third-party code was imported; `THIRD_PARTY_NOTICES.md` did not change.

## Files changed

- Data and schema: `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.Refresh.cs`, `src/ArcaneCore.Data/Content/Import/Spec/ContentTableSpecs.cs`, `src/ArcaneCore.Data/World/Creatures/{CreatureDataModule,CreatureDumpImporter,CreatureScriptNameDataModule,EfCreatureDataStore}.cs`, `src/ArcaneCore.Kernel/WorldData/Creatures/CreatureContent.cs`.
- Game: `src/ArcaneCore.Game/Creatures/AI/CreatureAiServices.cs`, `src/ArcaneCore.Game/Creatures/{CreatureMapSystem.RelayScripts,CreatureQuestScripts}.cs`, `src/ArcaneCore.Game/GameObjects/{GameObjectMapSystem,GameObjectMapSystem.DbEvents}.cs`, `src/ArcaneCore.Game/Spells/{SpellCast,SpellSystem.Effects}.cs`.
- Tests: `tests/ArcaneCore.Data.Tests/ContentImport/Core/TableSpecDriftTests.cs`, `tests/ArcaneCore.Data.Tests/{CreatureDataTests,IntegratedSchemaTests}.cs`, `tests/ArcaneCore.Game.Tests/CreatureAi/{CreatureAiHostTests,EventAi/DbScriptRuntimeTests,EventAi/RelayScriptCommandTests}.cs`, `tests/ArcaneCore.Game.Tests/Spells/SendEventDbScriptTests.cs`.
- Documentation: `docs/areas/creature-ai.md`, `docs/reference/schema.md`, this report.

## Verification

Release `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` passed with zero warnings and errors after a cached restore. Final full Game: 7,538 passed, 13 skipped. Data excluding `Resilience`: 1,211 passed, 15 skipped; the unfiltered run found a now-fixed content-spec omission and then stayed silent for several minutes, so it was stopped. A full World run passed 3,161, skipped 29 before the last SEND_EVENT and conditional-failure edits. The next full World run passed 3,160, skipped 29 and failed `LiveFxCommandTests.Lookup_FindsByIdAndByNamePart_InEveryTable`; that test passed alone, and the final affected World filter passed 14/14 after the last build. Focused Data table-spec and ScriptName refresh checks passed 31/31. `git diff --check` returned 0. Real-client acceptance and a full green World run on the final diff remain pending.
