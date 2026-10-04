# Ranged lane S04: inputs of ranged weapon damage

Branch `claude/vw5-ranged-combat`. Retail only.

## Delivered

| Piece | Where | Reference |
|---|---|---|
| Ammo DPS in the player's ranged damage fields: `(min + max) / 2` of the selected ammo's first damage entry times the (unhasted) weapon speed, only when the ammo fits the non-broken launcher; recomputed when the ammo changes (`PlayerInventory.AmmoChanged`, raised from `WriteAmmoId`) and, as before, when the ranged weapon changes. | `Items/PlayerInventory.Ammo.cs`, `Stats/PlayerStatSystem.cs` (`Attach`, `OnAmmoChanged`, `UpdateDamagePhysical`) | vmangos `StatSystem.cpp:440-443`, `Player.cpp:7514-7569,10125-10158` |
| Weapon damage spells swing the weapon `SpellEntry::GetWeaponAttackType` names: a ranged class spell, and any other class carrying Ex2 0x20 (wand Shoot, damage class magic). Wand Shoot used to roll main-hand damage. | `SpellSystem.Combat.cs` `EffectWeaponDamage` (via `RangedSpellFacts.UsesRangedWeapon`) | vmangos `SpellEntry.cpp:434-455` |
| Ranged attack power bonus of a weapon spell: victim aura 127 (Hunter's Mark 20/45/75/110, Expose Weakness 450) plus attacker aura 131 matched against the victim's creature type mask, `APbonus / 14 * multiplier` where the multiplier is the weapon's **unhasted** speed in seconds, or 2.8 for normalized weapon damage (effect 121). | `Ranged/RangedDamageBonus.cs` | vmangos `SpellCaster.cpp:1340-1346,1411-1437,1826-1853` |
| Aura 113 (Elune's Grace and the like): flat ranged damage taken, added after the done bonuses. | same | vmangos `Unit.cpp:5703,5728` |
| Aura 185 (MOD_ATTACKER_RANGED_HIT_CHANCE): subtracted from the attacker's ranged miss chance before the 0..60 clamp. | `SpellCombatRules.cs` ranged branch | vmangos `SpellCaster.cpp:416` |

## Limits

* The melee analogues (126, 130, 112 and 184) and the weapon-class flat part of MOD_DAMAGE_DONE are left to the aura-engine lane; no melee path was changed.
* The `UNIT_MOD_DAMAGE_RANGED` TOTAL_PCT multiplier that vmangos applies to the done bonus has no ledger to read in this tree and is 1.
* Wand hit and crit tables (Ex3 0x8000 NORMAL_RANGED_ATTACK), the wand damage school override and resist, and the wand crit multiplier are not delivered (slice S05, see `docs/areas/ranged.md`).
