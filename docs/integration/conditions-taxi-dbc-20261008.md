# Conditions, taxi network and DBC signedness (2026-10-08)

Base: `847a86ea`, world schema 42; no schema step. No third-party source code or dump rows were copied. WoWDBDefs generated metadata already has its CC BY-SA 4.0 attribution in `THIRD_PARTY_NOTICES.md`; vmangos and mangos-classic were read as behavior references only.

## ClassicDB z2815 condition inventory

`py -3 tools/analysis/condition_usage_z2815.py D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz` streams the unmodified dump. It found **1,133** condition rows. The only leaf types lacking an evaluator path on this base were 31 (2 rows), 33 (11), 36 (2), 37 (6), 39 (2), 40 (37), and 42 (16). Those 76 leaves also blocked 84 composite rows. All seven now have evaluator paths. The calculation assumes every optional collaborator is present: fully decidable IDs rise from **973 to 1,133**. In actual play, a missing live fact still fails closed; this is code-path coverage, not proof that every world event or encounter supplies a value.

Composite condition operands reference other condition IDs 745 times: `value1` 380, `value2` 335, `value3` 22, `value4` 8; 556 distinct IDs. The SQL columns that name `conditions.condition_entry` have these nonzero uses:

| Source column | Uses | Distinct IDs |
| --- | ---: | ---: |
| `areatrigger_teleport.condition_id` | 2 | 1 |
| `creature_loot_template.condition_id` | 2,080 | 39 |
| `dbscripts_on_creature_death.condition_id` | 1 | 1 |
| `dbscripts_on_creature_movement.condition_id` | 6 | 3 |
| `dbscripts_on_go_template_use.condition_id` | 12 | 6 |
| `dbscripts_on_gossip.condition_id` | 2 | 2 |
| `gameobject_loot_template.condition_id` | 46 | 14 |
| `gossip_menu.condition_id` | 1,168 | 233 |
| `gossip_menu_option.condition_id` | 854 | 321 |
| `npc_spellclick_spells.condition_id` | 10 | 5 |
| `npc_vendor.condition_id` | 283 | 7 |
| `npc_vendor_template.condition_id` | 6 | 2 |
| `quest_template.RequiredCondition` | 116 | 69 |
| `reference_loot_template.condition_id` | 129 | 21 |
| `skinning_loot_template.condition_id` | 9 | 1 |
| `spell_area.condition_id` | 4 | 1 |

The other `condition_id` columns in the dump have zero nonzero rows. `combat_condition.*ConditionID` and `creature_spell_targeting.UnitCondition` name other condition tables, so they are excluded. Across the listed columns, **624** distinct external IDs are named: **534** had complete code paths before, **622** do now. IDs **953** and **964** are referenced but have no row in z2815; code cannot supply those rows.

## Behavior and producers

ArcaneCore keeps mangos-classic numbering because z2815 uses it; vmangos reuses several numeric type IDs for different meanings. The new cases follow `D:/refs/mangos-classic/src/game/Globals/Conditions.cpp::ConditionEntry::Evaluate` and `::CheckOp` (roughly 393–499 and 132–145), with the target/source swap and unknown propagation already present in `ConditionEvaluator`. vmangos `D:/refs/vmangos/src/game/Conditions.cpp::ConditionEntry::Meets` was checked for the general condition call shape. Type 42 reads a signed map variable and applies the six signed comparisons to `value3`. Waypoint comparisons use the source creature's last reached waypoint; dead/away modes 0 and 3 and live creature range read the current map. Spawn count follows mangos-classic `Creature::AddToWorld`/`RemoveFromWorld` and `Map::SpawnedCountForEntry`: it counts in-world creatures whose cmangos `ExtraFlags` has `COUNT_SPAWNS` (`0x00200000`); z2815's two target templates, 412 and 12339, both have that bit. The two z2815 `COMPLETED_ENCOUNTER` rows name DungeonEncounter IDs 715/716.

`ConditionRuntimeState` exposes per-map variables and completed encounter IDs plus explicit world-script results. The world feature binds those facts and the map's creature movement/state to the evaluator. No existing encounter script or AQ war-effort/invasion system publishes DungeonEncounter 715/716 or the z2815 type-40 world-script IDs yet. No existing feature publishes the sixteen type-42 map variables. Those rows therefore fail closed or compare against the map variable's zero default until their owner sets the facts. The `condition_id` references in loot, scripts, trainer and spell-area tables still require their respective consumers to invoke the evaluator; the counts above measure data references and evaluator support, not all consumer routing.

## Taxi and DBC

`NpcStore.TaxiNodesMask` and `QuestNpcServices.NearestTaxiNode` now use the vmangos taxi network: a node leaves it only when every outgoing TaxiPath is named by spell effect 123 (`SEND_TAXI`), following vmangos `src/game/Database/DBCStores.cpp` taxi-mask initialization (366–405); `ObjectMgr::GetNearestTaxiNode` is the nearest-node reference. Because `QuestNpcFeature` attaches before `SpellFeature`, the latter rebuilds the NPC mask immediately after loading the spell store. NPC table reloads keep those script-path IDs. A node with no outgoing path stays in the network, as in the vmangos loop (see the intake notes).

`gen_dbc_layouts.py` carries the `u` marker from WoWDBDefs field widths into `DbdField.IsSigned`; its output was regenerated from WoWDBDefs commit `e3df370` and the existing 154 generated file names (only 14 effective DBCs were present under `D:/refs/client-dbc-5875-effective`). `arcane-db dbc dump <file.dbc> --dbc-dir <dir>` prints fields using that signedness without a world database. Synthetic Map.dbc test values pin `<32>` as `-1` and `<u32>` as `4294967295`.

## Validation

Release solution build: passed, 0 warnings, 0 errors. Focused: Game 58 passed initially plus the new nearest-node test 2/2; Data 18 passed/2 skipped; World 11 passed (including the real z2815 import test), then two new integration checks and the final condition set (7/7) passed. The real dump's `ConditionTable` accepted **1,133/1,133** rows with zero rejected. Full Game on the final build: **7,573 passed/15 skipped**. Full Data: **1,343 passed/15 skipped**. The first full World run passed **3,233/30 skipped**; a later full run had **3,233 passed/30 skipped/1 failed** in `PlayerbotAreaTriggerTests.ObservingWithoutAnEntry_AllocatesNothing`, which passed immediately alone. The bounded rerun passed **3,234/30 skipped**. After the spawn-count flag correction, the final full World run again passed **3,234/30 skipped**. No Playerbots source was changed in this lane.

## Files

- Conditions: `src/ArcaneCore.Game/Conditions/{ConditionContext,ConditionEvaluator,ConditionRuntimeState}.cs`, `src/ArcaneCore.Game/Creatures/Movement/MotionMaster.cs`, `src/ArcaneCore.World/Npc/ConditionFeature.cs`, `tests/ArcaneCore.Game.Tests/Conditions/ConditionContentGapTests.cs`, `tests/ArcaneCore.World.Tests/Npc/ConditionFeatureTests.cs`.
- Taxi: `src/ArcaneCore.Game/Npc/{NpcStore,QuestNpcServices.Travel}.cs`, `src/ArcaneCore.World/Npc/QuestNpcFeature.cs`, `src/ArcaneCore.World/Spells/SpellFeature.cs`, `src/ArcaneCore.World/Reload/NpcContentReloadables.cs`, `tests/ArcaneCore.Game.Tests/Npc/TaxiNetworkTests.cs`, `tests/ArcaneCore.World.Tests/Npc/TaxiNetworkFeatureTests.cs`.
- DBC: `tools/codegen/gen_dbc_layouts.py`, `src/ArcaneCore.Data/ClientData/{ClientDbcLayouts.Dbd.g,DbcRecordDumper}.cs`, `src/ArcaneCore.Data/Schema/Upgrade/Cli/{DbUpgradeArguments,DbUpgradeCli}.cs`, `tests/ArcaneCore.Data.Tests/ClientData/DbcDumpSignedTests.cs`.
- Inventory and docs: `tools/analysis/condition_usage_z2815.py`, this report, `docs/areas/client-data.md`, `docs/integration/npc-services.md`, `docs/ops/database-upgrade.md`.

Generation was repeated against the same 154 names: byte-identical SHA-256 `9C5ED7F77B9FEE0EC6DBB0F7DA8D5349298EE8CA366CEC5A2CFC4B886F2A2E89`. The scratch name files were removed. `git diff --check` passed. No commit or push was made.

## Intake (Claude, 2026-10-08)

- Taxi network: Codex's mask also dropped every node with no outgoing TaxiPath (it read the brief's summary literally). vmangos `DBCStores.cpp` (366-405) keeps such nodes and drops only nodes whose outgoing paths are all `SEND_TAXI` paths; `NpcStore` now matches vmangos, so a saved taxi mask is no longer stripped of path-less nodes at login (`QuestNpcServices` ANDs it with the network). `TaxiNetworkTests` pins both cases.
- The `SEND_TAXI` path-id scan exists once (`QuestNpcFeature.SendTaxiPaths`) instead of twice.
- `MotionMaster.LastReachedWaypoint` documents that it reads only the default generator: mangos-classic searches the whole stack, but ArcaneCore never pushes a waypoint generator above the default.
- Not done: `ConditionContext.DeadOrAwayGroup` (CONDITION_DEAD_OR_AWAY modes 1 and 2) has no production binding, so those modes stay unknown and fail closed; z2815 uses only modes 0 and 3 (conditions 317 and 318). No producer sets DungeonEncounter completion, world-script results or map variables yet (as above).
- Proven load-bearing by breaking each feature and rebuilding: the mask rule (2 Game tests + the World refresh test failed), the nearest-node filter alone (1 failed), signed type-42 comparison (Game + World world-state tests failed), and signed DBC dump output (Data dump test failed). Regeneration with `py -3 tools/codegen/gen_dbc_layouts.py` was byte-identical to the committed file; `arcane-db dbc dump Map.dbc` on the real 154-file client prints `Unk0=-1`. The real-client ClientData tests (`ARCANECORE_TEST_DBC_DIR`) passed 20/20.
