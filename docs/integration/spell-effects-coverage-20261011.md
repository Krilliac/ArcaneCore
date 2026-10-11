# Class spell effects and implicit target 28 (2026-10-11)

Base: `55624b14` (`claude/w22-qa-spells`). The full machine-generated gap rows are in
[before](spell-gaps-before-20261011.md) and [after](spell-gaps-after-20261011.md). The
[target-28 audit](spell-implicit-target-28-20261011.md) lists every affected castable spell.

## Content and method

`PlayerbotQaSpellCoverageRealContentTests` loads the read-only live world DB into a temporary test database,
then uses `PlayerbotQaSpellCoverage.Build` with all class trainer rows through level 60, starter spells,
and all talent rank spells from build-5875 `Talent.dbc`. It checks the live spell handler and script registries.
The supplied `ARCANECORE_TEST_DBC_DIR` activates the retail Skills, Talents and Enchanting features; the
test asserts they loaded. The imported world's `npc_trainer` has 27,309 rows, maximum `reqlevel` 60,
and none above 60. Coverage selected 1,238 non-passive class spells, including 804 named by rotations
or escapes.

| Scope | Before | After |
|---|---:|---:|
| Spells with static gaps | 30 | 25 |
| Static gap rows | 34 | 29 |
| Rotation and escape gaps | 0 | 0 |
| `SCRIPT_EFFECT` with no script | 5 | 0 |
| Other missing effect handlers | 0 | 0 |
| DUMMY effects with no script | 0 | 0 |

The 29 remaining rows are aura findings assigned to the aura lane: 22 advisory DUMMY aura rows
(18 spells), six `ModDetectRange` rows, and one `AurasVisible` row. The static analyzer does not
list implicit targets: it reports those only when a cast reaches `OnUnsupported`.

The older live profile supplies `ClientData:DbcDirectory` and Enchanting data but omits the four
`Skills:*DbcPath` values and both `Talents:*DbcPath` values. Running coverage with that profile
alone selected only 1,178 spells and reported an `OpenLock` handler gap; the
[profile-only list](spell-gaps-profile-without-skill-talent-dbc-20261011.md) records it. `GatheringSpells`
already implements `OpenLock` (vmangos `Spell::EffectOpenLock`, `SpellEffects.cpp:2100-2213`), but
`SkillsFeature` does not install it without the Skills DBCs. The test's full-data run proves the
handler is present when those files are supplied. The live profile itself was not edited.

## Changes and reference behavior

* The five Create Healthstone ranks were each a lone `SCRIPT_EFFECT` with no script. vmangos
  `WarlockCreateHealthstoneScript` (`src/scripts/spells/spell_warlock.cpp:260-365`) maps the five
  ranks and two Improved Healthstone auras to 15 item IDs, checks bag space before reagent
  consumption, then calls `Spell::DoCreateItem` (`SpellEffects.cpp:1885-1990`).
  `CreateHealthstoneScript` now follows that path through the existing item-creation code.
* The exact 1,238-spell book uses implicit target 28 in 23 spells, all on
  `PERSISTENT_AREA_AURA`. The cast's separate ground-effect path created their dynamic objects,
  but `SelectEffectTargets` returned `null` and called `OnUnsupported` for target 28.
  vmangos `Spell::SetTargetMap` (`Spell.cpp:2774-2778`) selects no units at cast time for this
  effect: the dynamic object visits enemies later. Other effects using 28 select enemies at the
  destination. The selector now follows both cases.

## Verification

* RED: seven Healthstone casts returned `CastOk` but produced zero items; the focused test run
  failed 7/7 at the item assertion. GREEN: all five ranks plus both improved variants and the
  full-bag/reagent case passed 8/8.
* RED: a ground-aura cast reported `(effect 0, implicit target, 28)` through `OnUnsupported`.
  GREEN: it created one dynamic object, applied the aura to an enemy after a world tick, spared
  a friend, and made no unsupported callback. A non-persistent destination-area cast also passed.
* Final `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false`:
  0 warnings, 0 errors. The Game `Spells` and `ClassSpells` namespaces passed 989/989;
  World `Spells` and QA spell coverage passed 129/129 with the real content test included.

The full Game and World suites, individual live casts of every target-28 rank, and a real-client
run were not performed. The target audit establishes the common DBC target/effect pair for all
23 spells; the cast regression exercises its representative behavior.

## Files in this lane

Production: `src/ArcaneCore.Game/Spells/Warlock/CreateHealthstoneScript.cs`,
`src/ArcaneCore.Game/Spells/Scripts/SpellScriptDispatcher.cs`,
`src/ArcaneCore.Game/Spells/SpellDefines.cs`, and
`src/ArcaneCore.Game/Spells/SpellSystem.Targeting.cs`.

Tests: `tests/ArcaneCore.Game.Tests/ClassSpells/CreateHealthstoneSpellTests.cs`,
`tests/ArcaneCore.Game.Tests/Spells/PersistentAreaAuraTests.cs`, and
`tests/ArcaneCore.World.Tests/Playerbots/Qa/PlayerbotQaSpellCoverageRealContentTests.cs`.

Reports: this note, the before and after gap lists, the profile-only gap list, and the target-28 audit.
