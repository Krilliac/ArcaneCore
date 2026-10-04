# Ranged lane S08: creature ranged damage and attack power

`Creature` now writes `UNIT_FIELD_MINRANGEDDAMAGE` / `MAXRANGEDDAMAGE` / `RANGED_ATTACK_POWER` from the template, next to the melee pair (`Game/Creatures/Creature.cs`). Before this the 47 creatures that cast WEAPON_DAMAGE ranged spells through EventAI (Multi-Shot, Piercing Shot, Exploding Shot ...; classic-db `creature_ai_scripts` action 11) rolled the 5-damage fallback of `WeaponDamageRoll`.

* Reference: vmangos `Creature.cpp:1840-1843` (`SetBaseWeaponDamage(RANGED_ATTACK, ...)`). vmangos derives the value from `creature_classlevelstats.ranged_damage * damage_multiplier * rank mod` with variance; ArcaneCore's content importer already does that into `CreatureTemplate.MinRangedDamage/MaxRangedDamage/RangedAttackPower` (`Data/World/Creatures/CreatureDumpImporter.cs:471-476`), and the melee fields take the template values the same way, so the runtime only copies them.
* Limit: the melee attack power field is still not written for creatures (not part of this lane); the off-hand halving of vmangos (`Creature.cpp:1837`) is not applied.
* Tests: `tests/ArcaneCore.Game.Tests/Ranged/CreatureRangedFieldsTests.cs`.
