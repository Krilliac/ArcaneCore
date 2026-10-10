# Creature movement scripts, script targets and FOLLOW (2026-10-08)

This lane starts from main `ddebfd52`. World schema 45 (allocated as 43; renumbered in wave 10 after script-engine 43 and spawn-groups 44) adds `dbscripts_on_creature_movement`, keeps `ScriptId` on
`creature_movement` and `creature_movement_template`, and stores `spell_script_target`, `creature_linking` and
`creature_linking_template`. Full import and content refresh load the rows; refresh changes movement script ids on existing
paths without replacing their coordinates. The importer still reads `waypoint_path` into the existing namespaced entry-path
storage. No reference source code was copied.

## Reference behavior

* At a waypoint arrival, start the node's movement script with the creature as source, then inform AI. The default path
  uses vmangos `src/game/Movement/WaypointMovementGenerator.cpp::WaypointMovementGenerator<Creature>::OnArrived`; the escort path follows mangos-classic
  `src/game/MotionGenerators/WaypointMovementGenerator.cpp::WaypointMovementGenerator<Creature>::OnArrived` and `src/game/AI/BaseAI/UnitAI.h`'s escort path
  compatibility route. The map's existing DB-script scheduler handles delay, priority, speech and emotes. The escort's
  player is its target when linked; other paths use the creature itself.
* EventAI CHANGE_MOVEMENT flag `0x2` and relay MOVEMENT `datalong3 & 0x2` select the shared `waypoint_path` path id,
  as mangos-classic `src/game/AI/EventAI/CreatureEventAI.cpp::ProcessAction` and
  `src/game/DBScripts/ScriptMgr.cpp::ScriptAction::ExecuteDbscriptCommand` do. Relay `datalong3 & 0x1` passes its target
  to waypoint script execution.
* Spell target 38 (`SpellImplicitTarget.UnitScriptNearCaster`) selects one unit: a matching explicit target, else the
  nearest listed living creature/player or dead creature. A creature caster can select itself when its entry is listed (C'Thun Vulnerable 26235); target 7 (`EnumUnitsScriptAoeAtSrcLoc`) still excludes the caster and filters units in the source area by entry, type and inverse effect mask (or selects
  all eligible units when no rows exist). vmangos `src/game/Spells/Spell.cpp::CheckScriptTargeting` and
  `Spell::SetTargetMap`, cases `TARGET_UNIT_SCRIPT_NEAR_CASTER` and `TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC`.
  Snufflenose spell 8283 now acts on its selected gopher in `OnEffectExecute` (mangos-classic
  `src/game/AI/ScriptDevAI/scripts/kalimdor/razorfen_kraul/razorfen_kraul.cpp::SnufflenoseCommand::OnEffectExecute`);
  Archaedas spells 10252/10258 wake their selected units instead of a separate boss-side search (mangos-classic
  `src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/uldaman/boss_archaedas.cpp::AwakenEarthenArchaedas::OnApply`
  and `AwakenVaultWarder::OnEffectExecute`).
* `creature_linking` and `creature_linking_template` FOLLOW (`0x200`) start a follow generator at the spawn-relative
  distance and angle, and release it when the master dies or leaves the map. vmangos
  `src/game/Group/CreatureLinkingMgr.cpp::TryFollowMaster`, `SetFollowing` and `ProcessSlave`. A spawn's own
  `creature_linking` row takes precedence over its entry's template row (`GetLinkedTriggerInformation`), and a template
  `search_range` is a 2D distance between spawn points (`IsSlaveInRangeOfMaster`). vmangos re-follows on master respawn
  and on the slave's evade home; ArcaneCore checks on each map tick instead (an emulation of those two triggers).

## ClassicDB evidence and limits

ClassicDB z2815 path 3678 has script ids 367801 (point 1), 367802 (point 13), 367803 (point 57).
Their movement-script rows include the Disciple's speech and point emotes. The dump also has spell targets
`(8283,1,4781,0)`, `(10252,1,7076,0)`, `(10258,1,10120,0)`, spawn link `(13991,13990,515)` and template link
`(390,0,330,515,0)`.

Only unit spell script targets 7 and 38 are in this slice; game-object script targets and other implicit script-target
modes are not. The core selector cannot report vmangos' `BAD_TARGETS` for a missing target after cast preparation; it
returns no affected unit. Creature-linking flags other than FOLLOW are stored but not executed. FOLLOW is updated on map
ticks using loaded spawns; a master on an unloaded grid cannot be followed until it loads. No real client or hosted-CI
acceptance was run in this lane. The Molten Core per-spell selectors for targets 7 and 38
(`Instances/Scripts/MoltenCore/MoltenCoreTargetModule.cs`) still take precedence over the core selector; they were outside
this lane's brief and can be retired once their spells' `spell_script_target` rows are confirmed.

## Verification (intake, 2026-10-08)

The Codex lane could not commit; the intake agent reviewed the diff, fixed the points below and re-ran everything natively.

* Intake fixes: `DbScriptKind.CreatureMovement` is 8 (mangos-classic `ScriptMgrDefines.h` `SCRIPT_TYPE_CREATURE_MOVEMENT`;
  Codex had used 2, which is `SCRIPT_TYPE_SPELL`); targets 7 and 38 have names in `SpellImplicitTarget`; target 38 returns
  a single unit as `CheckScriptTargeting` does (Codex returned up to `EffectChainTarget` units) and the 200-yard bound is
  cited to mangos-classic, not vmangos; the waypoint generator only re-checks its state after a node script ran; FOLLOW
  honours the guid-before-entry link order and the 2D search range, keeps a found template master, and only builds its
  spawn index on maps with a FOLLOW slave; the Archaedas test uses the real z2815 layout (targets 22/7, effect 10258 DUMMY with
  MaxAffectedTargets 2), the Naralex patrol test asserts the script runs exactly once, the template-link test uses the
  ClassicDB `search_range` 0; new `ScriptTargetSelectionTests` cover nearest/explicit 38, entry and inverse-mask filtering
  for 7, and 7 without rows; stale "not run" statements in `instances.md`, `battlegrounds.md` and
  `creature-movement-spawns.md` were updated.
* Release solution build `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors.
* Full Game: 7,537 passed, 13 skipped, 0 failed (7,550). Full Data: 1,343 passed, 16 skipped, 0 failed (1,359; it
  completed in 7 m 29 s, no stall). Full World (includes the docs generators): 3,162 passed, 29 skipped, 0 failed (3,191).
  Kernel: 32 passed. The real-dump test `RealClassicDb_ContainsTheNaralexStepsScriptTargetsAndFollowLinks` ran against
  z2815 (`ARCANECORE_CLASSICDB_DUMP`) and passed.
* RED proofs: with the waypoint script call, the escort script call, the single-target and inverse-mask rules, the FOLLOW
  update, the Archaedas awaken call and the two `waypoint_path` branches each disabled, 9 of the 11 new or changed Game
  tests failed (the other two are a negative case and the no-rows case, which no break touched); with the importer's
  `ScriptId` and `spell_script_target` reading disabled, all 3 `MovementScriptContentTests` failed.

## Changed files

* `docs/areas/content-import.md`
* `docs/areas/creature-ai.md`
* `docs/areas/creature-movement-spawns.md`
* `docs/areas/spells.md`
* `docs/integration/movement-scripts-20261008.md`
* `docs/reference/schema.md`
* `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.Refresh.cs`
* `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.cs`
* `src/ArcaneCore.Data/Content/Import/Spec/ContentTableSpecs.cs`
* `src/ArcaneCore.Data/Content/Spells/SpellContentRows.cs`
* `src/ArcaneCore.Data/Content/Spells/SpellWorldDataModule.cs`
* `src/ArcaneCore.Data/World/Creatures/CreatureDataModule.cs`
* `src/ArcaneCore.Data/World/Creatures/CreatureDumpImporter.cs`
* `src/ArcaneCore.Data/World/Creatures/CreatureLinkRows.cs`
* `src/ArcaneCore.Data/World/Creatures/CreatureMovementTemplateDataModule.cs`
* `src/ArcaneCore.Data/World/Creatures/DbScriptDataModule.cs`
* `src/ArcaneCore.Data/World/Creatures/DbScriptDumpImporter.cs`
* `src/ArcaneCore.Data/World/Creatures/EfCreatureDataStore.cs`
* `src/ArcaneCore.Data/World/Creatures/MovementScriptDataModule.cs`
* `src/ArcaneCore.Data/World/Creatures/SpellScriptTargetRow.cs`
* `src/ArcaneCore.Game/Creatures/AI/EscortAI.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.EventAiMovement.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.Linking.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayCommands.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayScripts.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMovement.cs`
* `src/ArcaneCore.Game/Creatures/Movement/WaypointMovementGenerator.cs`
* `src/ArcaneCore.Game/Instances/Scripts/RazorfenKraul/RazorfenKraulAis.cs`
* `src/ArcaneCore.Game/Instances/Scripts/Uldaman/ArchaedasAi.cs`
* `src/ArcaneCore.Game/Instances/Scripts/Uldaman/ArchaedasAwakenSpell.cs`
* `src/ArcaneCore.Game/Spells/SpellStore.cs`
* `src/ArcaneCore.Game/Spells/SpellSystem.ScriptTargets.cs`
* `src/ArcaneCore.Game/Spells/SpellSystem.TargetSelectors.cs`
* `src/ArcaneCore.Game/Spells/SpellSystem.Targeting.cs`
* `src/ArcaneCore.Kernel/WorldData/Creatures/CreatureContent.cs`
* `src/ArcaneCore.Kernel/WorldData/Creatures/CreatureLinks.cs`
* `src/ArcaneCore.Kernel/WorldData/Creatures/DbScriptCatalog.cs`
* `src/ArcaneCore.World/Spells/SpellStoreFactory.cs`
* `tests/ArcaneCore.Data.Tests/DbScriptDumpImporterTests.cs`
* `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs`
* `tests/ArcaneCore.Data.Tests/MovementScriptContentTests.cs`
* `tests/ArcaneCore.Game.Tests/CreatureAi/EventAi/InstanceAndMovementActionTests.cs`
* `tests/ArcaneCore.Game.Tests/CreatureAi/EventAi/RelayScriptCommandTests.cs`
* `tests/ArcaneCore.Game.Tests/CreatureMovement/CreatureLinkFollowTests.cs`
* `tests/ArcaneCore.Game.Tests/CreatureMovement/WaypointGeneratorTests.cs`
* `tests/ArcaneCore.Game.Tests/Instances/SnufflenoseGopherProductionTests.cs`
* `tests/ArcaneCore.Game.Tests/Instances/UldamanEncounterTests.cs`
* `tests/ArcaneCore.Game.Tests/Instances/WailingCavernsScriptTests.cs`
* `tests/ArcaneCore.Game.Tests/Spells/ScriptTargetSelectionTests.cs` (intake)
* `src/ArcaneCore.Game/Spells/SpellDefines.cs` (intake)
* `docs/areas/battlegrounds.md`, `docs/areas/instances.md` (intake)
* `tests/ArcaneCore.World.Tests/Spells/SpellStoreFactoryCombatDataTests.cs`
