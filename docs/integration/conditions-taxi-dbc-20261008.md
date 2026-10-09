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

`ConditionRuntimeState` exposes per-map variables and completed encounter IDs plus explicit world-script results. The world feature binds those facts and the map's creature movement/state to the evaluator. On this original base, no encounter script or AQ war-effort/invasion system published DungeonEncounter 715/716 or the z2815 type-40 world-script IDs, and no feature published the sixteen type-42 map variables. Those rows failed closed or compared against the map variable's zero default until their owner set the facts. The `condition_id` references in loot, scripts, trainer and spell-area tables still require their respective consumers to invoke the evaluator; the counts above measure data references and evaluator support, not all consumer routing.

### 2026-10-09 instance fact follow-up

`IInstanceConditionFacts` lets `ConditionFeature` read saved instance encounter state. On map 509, conditions 6500-6505 read the six AQ20 boss slots as variables 4811, 2174, and 4812-4815: zero until each boss is Done, one afterward. On map 531, conditions 717/718 read DungeonEncounter 715/716 from the Twin Emperors and Ouro slots. These values also work immediately after the script loads a saved instance. This slice did not wire the other ten type-42 rows or the war-effort/invasion type-40 rows to their owners. Twin Emperors and Ouro now have encounter AIs and imported-content runtime checks that reach Done; a multiplayer raid and original-client run remain outstanding.

### 2026-10-09 spawn-group map context follow-up

`spawn_group.WorldState` is evaluated with its owning map and no player, including through AND/OR/NOT conditions. Previously the evaluator received no map, making all type-42 leaves unknown and keeping their groups out. The map fact reader now uses saved instance facts first, explicitly set `InstanceData` variables next, and the runtime map variable fallback last. Uldaman's variable 700001 now reaches its imported Annora group condition, and the imported AQ40 condition 5310010 reads map variable 4823's zero default so the Sartura trash groups appear. Focused tests cover map scoping, composite propagation, Uldaman's zero-to-one transition, and a Sartura group spawning from a copied ClassicDB world. At this point the quest and event variables 6506, 6507, 19020, 19021, 19997, 30011, 19990, and 19951 lacked producers; AQ40 variable 4823 had no owner for a nonzero transition. The type-40 world-script facts remained unowned.

### 2026-10-09 DB script world-state producer follow-up

ClassicDB writes those eight quest/event variables through `SCRIPT_COMMAND_SET_WORLDSTATE` (53): relay 4072 writes 19020, relay 61935 writes 19021, and quest/movement/relay steps write 6506, 6507, 19997, 30011, 19990 and 19951. The shared DB script executor now handles command 53 using signed `dataint2`, publishing to the runtime map facts and to an instance script's explicit variables when one exists. Synthetic quest-start and creature-movement tests cover positive and negative values and instance routing; the imported-world smoke executes ClassicDB relay 4072 and observes variable 19020 change. Other trigger paths for those rows still need direct gameplay acceptance. At this point AQ40 variable 4823 had no producer for a nonzero transition, and type-40 world-script facts remained unowned.

### 2026-10-09 Sartura completion variable follow-up

ClassicDB z2815 `instance_dungeon_encounters` row 711 (map 531, Battleguard Sartura) names completed world state 4823. `TempleOfAhnQirajInstance` now derives variable 4823 as zero until Sartura is Done and one afterward, including immediately after loading the saved encounter. The imported groups 5310014-5310021 require condition 5310010 (`4823 == 0`), so they can be selected before completion and stop qualifying afterward. Their flags do not request despawn on condition failure; existing living members are not forcibly removed by this change. Focused tests exercise the player condition, the spawn-group callback, and save/load. The type-40 world-script facts remain unowned.

### 2026-10-09 AQ war-effort world-script follow-up

The 30 AQ resource fields and condition 2113 now read a realm-wide, durable war-effort state rather than an unset runtime fact. ClassicDB z2815's 60 first/repeatable resource quests match the source `war_effort.cpp` map and have required item counts; the reward transaction increments the matching counter atomically with the quest, so a duplicate or rolled-back reward cannot add progress. Starting server-side game event 120 enters the gathering phase, all 30 goals advance the saved phase to transportation with a five-day deadline, and the world feature swaps event 120 for 121. On deadline it enters the gong phase and starts event 122. Resource conditions use the reference's exact target-equality comparison; condition 2113 compares the remaining-day value. An absent store still fails closed. See `war-effort-20261009.md` for the scope and checks. The six Scourge invasion type-40 fields still lack a state owner.

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
- Not done: `ConditionContext.DeadOrAwayGroup` (CONDITION_DEAD_OR_AWAY modes 1 and 2) has no production binding, so those modes stay unknown and fail closed; z2815 uses only modes 0 and 3 (conditions 317 and 318). The six Scourge invasion world-script fields still lack their own state and gameplay producer.
- Proven load-bearing by breaking each feature and rebuilding: the mask rule (2 Game tests + the World refresh test failed), the nearest-node filter alone (1 failed), signed type-42 comparison (Game + World world-state tests failed), and signed DBC dump output (Data dump test failed). Regeneration with `py -3 tools/codegen/gen_dbc_layouts.py` was byte-identical to the committed file; `arcane-db dbc dump Map.dbc` on the real 154-file client prints `Unk0=-1`. The real-client ClientData tests (`ARCANECORE_TEST_DBC_DIR`) passed 20/20.
