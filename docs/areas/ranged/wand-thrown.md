# Ranged lane S05: wand Shoot rules, Throw and the single-shot abilities

Branch `claude/vw5-ranged-combat`. Retail only.

## Delivered

| Rule | Where | Reference |
|---|---|---|
| A ranged weapon attack with Ex3 0x8000 (NORMAL_RANGED_ATTACK) whose weapon attack type is ranged rolls on the **ranged** hit table (miss, no resist roll) and the ranged crit chance, whatever its damage class says. Wand Shoot is damage class magic and used the magic hit and crit tables (and, because its spell school is 0, never crit). | `Ranged/RangedSpellFacts.cs` (`IsNormalRangedAttack`, `HitDamageClass`), `Spells/SpellCombatRules.cs` (`RollHit`, `MeleeSpellHitResult`, `CritChance`) | vmangos `SpellCaster.cpp:215-232,732-737`, `Unit.cpp:5231-5239` |
| A priest, mage or warlock's ranged attack deals the school of the wielded ranged weapon's first damage entry (fire wand burns, arcane wand is arcane, a physical wand is physical). Applied to the damage (armor choice, partial resist, absorb, the damage log); the hit roll keeps the spell's own school. | `SpellSystem.Combat.cs` (`WithWandSchool`) | vmangos `Spell.cpp:65-72` (`CLASSMASK_WAND_USERS` = priest, mage, warlock, `SharedDefines.h:112`) |
| Crit damage keeps keying on the spell's damage class: wand crit is x1.5, Auto Shot crit x2 (pinned). | existing `CritMultiplier` | vmangos `SpellCaster.cpp:960-976` |
| Throw 2764 and Shoot Bow/Gun/Crossbow 2480/7918/7919 are **not** auto-repeat (Ex2 0 in classic-db); their cooldown is the ranged attack time (hasted under Rapid Fire), one thrown weapon is used per throw. Pinned with the real attribute values. | tests only | classic-db `spell_template` rows 2764, 2480, 7918, 7919; vmangos `Player.cpp:22193-22197` |

## Limits and open question

* Wand partial-resist ranges for `CLASSMASK_WAND_USERS` and the wand global cooldown (category flag `SCF_COOLDOWN_IS_GLOBAL`, `Player.cpp:22244-22253`) need SpellCategory.dbc flags and the spell-rules lane's resist code.
* **Possible fidelity gap outside this lane**: `SpellStoreFactory.ToSpellInfo` turns CastTimes row 1 (base 0) into `default`, which `SpellInfo.GetCastTime` reads as "no cast time entry" and answers 0. vmangos only returns 0 for a missing row (`SpellEntry.cpp:471-473`) and otherwise adds the 500 ms of a ranged-slot spell (`:511-512`), so Throw (CastingTimeIndex 1) and Multi-Shot (index 1) would be 0.5 s casts there and are instant here. Not changed (it affects every spell with an instant row); the tests do not assert the cast time of those spells. A presence flag on `SpellCastTime` fixes it.
