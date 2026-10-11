# Castable class spells with implicit target 28

From the read-only imported world `spell_template`, `npc_trainer` (all `reqlevel <= 60`), starter spells, and all five rank slots in build-5875 `Talent.dbc`. The 1,238 selected spell ids match `PlayerbotQaSpellCoverage.Build` with the full DBC configuration. Passive spells and spells outside the nine class families are excluded.

The effective selector values in that book are `0, 1, 5, 6, 15, 16, 20, 21, 22, 23, 24, 25, 28, 37, 41, 42, 43, 44, 45, 47, 57, 61`. Comparison with `SpellSystem.SelectEffectTargets`, its default selectors and `PetTargets.Install` found 28 as the only unselectable value at the base. This is a registry and DBC audit, not a cast of every rank.

vmangos `Spell::SetTargetMap` (`src/game/Spells/Spell.cpp`, `TARGET_ENUM_UNITS_ENEMY_AOE_AT_DYNOBJ_LOC`): for effect 27 (`PERSISTENT_AREA_AURA`), target 28 names the ground. The dynamic object selects enemy units on later updates; other effects select enemies at the destination. Before this change ArcaneCore reported target 28 as unsupported on cast, although its separate ground-effect path still created the object.

| Spell | Name | Effect | Effect type | Target B |
|---:|---|---:|---:|---:|
| 10 | Blizzard | 0 | 27 | 0 |
| 1510 | Volley | 0 | 27 | 0 |
| 1543 | Flare | 0 | 27 | 0 |
| 1543 | Flare | 1 | 27 | 0 |
| 2120 | Flamestrike | 1 | 27 | 0 |
| 2121 | Flamestrike | 1 | 27 | 0 |
| 5740 | Rain of Fire | 0 | 27 | 0 |
| 6141 | Blizzard | 0 | 27 | 0 |
| 6219 | Rain of Fire | 0 | 27 | 0 |
| 8422 | Flamestrike | 1 | 27 | 0 |
| 8423 | Flamestrike | 1 | 27 | 0 |
| 8427 | Blizzard | 0 | 27 | 0 |
| 10185 | Blizzard | 0 | 27 | 0 |
| 10186 | Blizzard | 0 | 27 | 0 |
| 10187 | Blizzard | 0 | 27 | 0 |
| 10215 | Flamestrike | 1 | 27 | 0 |
| 10216 | Flamestrike | 1 | 27 | 0 |
| 11677 | Rain of Fire | 0 | 27 | 0 |
| 11678 | Rain of Fire | 0 | 27 | 0 |
| 14294 | Volley | 0 | 27 | 0 |
| 14295 | Volley | 0 | 27 | 0 |
| 16914 | Hurricane | 0 | 27 | 0 |
| 16914 | Hurricane | 1 | 27 | 0 |
| 17401 | Hurricane | 0 | 27 | 0 |
| 17401 | Hurricane | 1 | 27 | 0 |
| 17402 | Hurricane | 0 | 27 | 0 |
| 17402 | Hurricane | 1 | 27 | 0 |

27 effects in 23 spells; all effect 27. A cast regression uses target 28 with an enemy and a friend, checks no unsupported callback, a created dynamic object, and the later enemy aura. The other ranks have the same target/effect pair but have not each been cast in this lane.
