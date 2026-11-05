# Creature spell lists and the caster contract (wave 79)

Status: design record. The content model and the scheduler exist (`CreatureSpellListCatalog`, `CreatureSpellListScheduler`); the data layer,
the host adapter over `SpellSystemCreatureCaster` and the spawn/respawn/set-list binding do not (row 431 of the ultracode triage stays open).
The first part below is the wave-79 contract text, copied here so it survives the archive of its source tree; "currently" in it describes the
tree at the time it was written (the base has since gained EventAI action 57, `SetRangedModeAction`). The second part is the rule-engine comparison.

## Part 1: wave-79 contract (copied)

## Source facts

ArcaneCore currently has the EventAI row path, but not the creature spell-list path. `EfCreatureDataStore.LoadAsync` loads templates, spawns, movement, EventAI scripts/texts/summons and broadcast text only (`src/ArcaneCore.Data/World/Creatures/EfCreatureDataStore.cs:10-48`). `CreatureTemplateRow` has `PetSpellDataId` and `AIName`, but no ordinary `SpellList`/`spell_list_id` field (`src/ArcaneCore.Data/World/Creatures/CreatureDataModule.cs:12-112`); `CreatureTemplate` likewise has no spell-list id. The only `creature_spell_list` implementation is the totem importer, which resolves the lowest positive spell for `totem_spell` and discards the list as an AI schedule (`src/ArcaneCore.Data/World/Totems/TotemSpellDumpImporter.cs:20-30, 68-114, 190-205`).

The CMaNGOS reference documents `creature_spell_list_entry`, `creature_spell_targeting`, and `creature_spell_list` as a 1200 ms combat scheduler. The list carries support/ranged flags, target resolver, availability/probability, initial/repeat cooldowns and disabled state (`mangos-classic doc/SpellLists.txt`; `mangos-classic src/game/Entities/CreatureSpellList.h`). CMaNGOS `UnitAI::EnterCombat` arms initial cooldowns, `UpdateSpellLists` gates combat/castability, rolls support/ranged chances, checks conditions/readiness/target, selects by probability or position, and calls `DoCastSpellIfCan` (`mangos-classic src/game/AI/BaseAI/UnitAI.cpp:104-110, 1181-1270`). `CAST_MAIN_SPELL` is explicitly deprecated in favor of `creature_spell_list`; the same is true of `CAST_DISTANCE_YOURSELF` (`mangos-classic src/game/AI/BaseAI/UnitAI.h:65-79`).

The bounded ClassicDB reference does contain actual Imp Minion 12922 EventAI rows. The row set is:

```text
1292201, creature 12922, event 11 (the contract's label "aggro"; classic-db's event 11 is SPAWNED), action 57, params (3,20,0): Enable No Melee Mode on Aggro
1292202, creature 12922, event 4 (aggro), chance 10, action 54: Random Say
1292203, creature 12922, event 0 (timer in combat), flags 1025, initial 1000..2000, repeat 2000..3000,
  action 11, spell 20801, target 1 (victim), cast flags 256 (CAST_MAIN_SPELL)
```

The row is present in `classic-db Full_DB/ClassicDB_1_12_1_z2815.sql.gz` in the `creature_ai_scripts` INSERT. The same dump has creature_template 12922 (`Imp Minion`, AIName `EventAI`) and creature_template_spells `(12922,0,3110,...)`, but no creature spell-list producer row was found for 12922. Thus the observed no-cast behavior is consistent with missing/partial import or runtime wiring, while the source DB itself does have an EventAI cast row.

Arcane EventAI supports the timer/aggro rows and action 11 cast, but not action 57. Registered event/action ids are enumerated in `src/ArcaneCore.Game/Creatures/AI/EventAi/EventAiTypes.cs:6-40`; `EventAiRegistry` discovers only handlers present in the assembly (`.../EventAi/EventAiRegistry.cs`). `EventAiEngine` loads entry and spawn-guid rows from `CreatureAiContent`, reports unsupported rows, and never executes them (`.../EventAi/EventAiEngine.cs:37-79`). Therefore 1292201 cannot disable melee today. `EventAiContext.RangedMode` is hardcoded false (`.../EventAi/EventAiContext.cs:43-60`), so `RangedModeOnly` rows are rejected and `MeleeModeOnly` rows are allowed. Generic combat gating rejects a row only when both `Silenced` and `Pacified` are set, or while a non-melee cast is active (`EventAiContext.cs:55-60`; `EventAiEngine.cs:277-302`).

Action 11 already has a real cast seam: `CastAction` resolves target, aura-not-present and interrupt/trigger flags, then calls `ICreatureSpellCaster.Cast` (`src/ArcaneCore.Game/Creatures/AI/EventAi/Actions/LegacyActions.cs:57-101`). `SpellSystemCreatureCaster` checks spell existence, rejects overlapping non-triggered casts, and delegates to `SpellSystem.CastSpell` (`.../SpellSystemCreatureCaster.cs:55-74`). Mana, range, LOS, silence and other cast legality therefore belong to the spell system; the caster AI should treat a failed cast as an unsuccessful combat action and retain/retry its timer according to EventAI semantics. `EventAiEngine.ProcessEvent` does exactly that for `CombatAction` rows (`.../EventAi/EventAiEngine.cs:324-372`).

## Smallest complete implementation slice

1. **Data contract:** add an immutable creature spell-list content model alongside `CreatureAiContent`, including list header (chance support/ranged), targeting rows, spell rows (flags, target id, probability, initial/repeat ranges, disabled/availability/conditions), and a template `SpellList` id. Load `creature_spell_list_entry`, `creature_spell_targeting`, `creature_spell_list` and any condition tables through the world data store. Do not reuse the totem importer: it intentionally reduces a list to one totem spell.
2. **Lifecycle/producer:** bind the loaded list to each creature AI at spawn/respawn and on explicit EventAI set-spell-set. Provide list reload/version handling analogous to `CreatureContent.DefinitionsVersion`; no database round trip in `OnUpdate`.
3. **Combat scheduler:** add a bounded 1200 ms scheduler owned by creature AI/base AI. On combat entry arm initial cooldowns; each tick gate alive/in-combat/not evading, no active cast unless non-blocking, and spell readiness. Resolve target types (at minimum current victim and self), enforce spell range/LOS through the normal cast path, choose one eligible blocking spell by probability or position, then process non-blocking/support spells separately. A successful cast starts its repeat cooldown; a failed cast must not be reported as success.
4. **Main spell/ranged state:** replace flags-only `CAST_MAIN_SPELL` interpretation with spell-list ranged-action metadata. `RangedMode` must become a live state, with chase distance and range mode type. It must transition out when the main spell is unavailable, silenced/pacified, out of mana, target is in melee reach, or cast/range checks fail, and transition in only when a valid ranged spell exists and the target is outside melee reach. Keep a conservative melee fallback unless an explicit no-melee policy is active.
5. **EventAI57:** implement action 57 at the EventAI action seam (the 12922 row's “set no melee mode” policy), with parameter validation and explicit state ownership in `CreatureEventAI`/base AI. It must set melee disabled only for the intended combat lifetime and restore melee on evade/reset/death/respawn. Do not silently treat unknown 57 as a no-op. The action must be observable in `Unsupported` before implementation and disappear from that list after a real handler is registered.
6. **AI flags/gates:** preserve existing `CombatAction` timer retry behavior, phase/chance/inverse-mask checks, and `CanExecuteCombatAction`. Correct the current silence gate only if the source-faithful contract requires silence alone to block non-triggered casts; verify against the spell system's `CasterAuraGate` before changing it. Do not add a caster-specific suppression that bypasses normal spell failure results.

## Ownership and tests

- Data owns schema rows, import conversion, immutable indexes and missing/invalid row diagnostics.
- Game creature AI owns list selection, cooldowns, ranged/melee state and reset transitions.
- SpellSystem remains authoritative for mana consumption, spell readiness, range, LOS, target legality, silence and cast state through `ICreatureSpellCaster`.
- EventAI owns action 57 dispatch and existing timer/event gating; it should call a small AI state method rather than duplicate movement or spell legality.

Meaningful tests should use existing `CreatureContent`/world fixtures and a fake `ICreatureSpellCaster`:

- import/load a synthetic spell-list header, targeting row and two spells; assert immutable grouping, target resolution and reload replacement;
- timer-in-combat list casts only after initial cooldown, honors repeat cooldown, chance/probability and target-victim selection;
- failed cast (unknown spell, active cast, no mana, silence/range/LOS rejection) does not consume the action as success and retries according to the timer contract;
- ranged mode turns on with a valid ranged spell and target distance, disables melee, chases/stays in bounded range, and falls back to melee when the target reaches melee range or the spell becomes unusable;
- action 57 on a 12922-shaped aggro row disables melee, while reset/evade/respawn restores it; unsupported output changes from row 57 to no unsupported row only when the handler exists;
- a complete 12922 fixture with action 57 plus timer cast produces a melee-disabled caster and a real spell cast, while the same fixture with no spell-list/template spell data fails closed with a diagnostic rather than manufacturing a spell;
- EventAI `CombatAction` rows remain armed after cast failure and proceed to later actions only after the first action succeeds.

The source references used were local pinned snapshots: mangos-classic `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`, vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, and ClassicDB `ClassicDB_1_12_1_z2815.sql.gz`. No source, live data, build, service, or client was changed.


## Part 2: comparison with the rule-engine direction

The rule-engine design (`docs/superpowers/specs/2026-10-28-rule-engine-design.md`) models creature timers as rule events
(`creature.timer(min, max, repeat_min, repeat_max)`, section 3) and casts as actions (`cast(spell, flags)`), and it migrates EventAI rows into
rules (`EventAiRuleConverter`, which already converts action 57 to `creature.set_ranged_mode`). The document does not mention spell lists.

A spell list does not map onto one rule per entry. cmangos `UnitAI::UpdateSpellLists` is a single 1200 ms loop per creature with state shared
between entries: one support roll and one ranged roll per tick that every entry of that kind shares, a probability-weighted choice of exactly one
blocking entry with fall-back to the next on a refused cast, and non-blocking entries cast outside that choice. Independent per-entry timer rules
would roll their gates separately, could cast several blocking spells in one tick, and could not express "weights of this tick's eligible set".

Conclusion: the scheduler stays a dedicated creature-AI component (not a rule primitive set), shaped like the other host seams:
it takes its legality questions from the spell system through `ICreatureSpellListHost`. The rule engine connects at two points, both
still to be added when the wiring lands: a `creature.set_spell_list` action (the explicit set-list the contract requires, run through the
rule path as well as the EventAI path) and the existing `creature.set_ranged_mode` state that the ranged-action entries read. Rule
timers and EventAI rows keep owning scripted casts; a creature with both runs both, as in cmangos.

## Part 3: the Imp Minion 12922 case

Source rows are quoted in Part 1. classic-db has no spell-list row for 12922, so list wiring would not change it: its casts come from EventAI
rows 1292201 (spawned, action 57) and 1292203 (timer in combat, action 11, cast flag 256). The base already supports action 57
(`SetRangedModeAction`, `CreatureEventAI.SetRangedMode`) and `CreatureEventAiTests.Imp12922Shaped_*` pins the action 57 and cast rows on the EventAI
path (written without a build or test run in the lane; unproven until the integration gate).
