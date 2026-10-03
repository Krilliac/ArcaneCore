# Ranged lane S01: attack speed core (haste auras 9, 138, 140, 141)

Branch `claude/vw5-ranged-combat`. Everything here is retail (vmangos) behaviour; there is no option.

## Delivered

| Piece | Where | Reference |
|---|---|---|
| Per-slot speed multiplier (`m_modAttackSpeedPct`), float32 shadow of `UNIT_FIELD_BASEATTACKTIME..RANGED` (the update field stays a `uint`; the shadow resyncs whenever someone writes the raw field), `GetAttackTime` = unhasted base, `SetAttackTime` stores `value * pct`, `ResetAttackTimer` = `GetAttackTime * pct`, `ApplyAttackTimePercentMod` with the -100 guards | `Game/Combat/UnitCombat.cs` | vmangos `Unit.h:406-413`, `Unit.cpp:529-532,9678-9697`, `Util.h:91-96`, `Object.h:248-252`, `Object.cpp:752-756` (float sent as uint32) |
| Auras 9 (all three slots), 138 (main + off hand), 140 (ranged), 141 (ranged; only for a player whose ranged slot holds a weapon with `AmmoType != 0`, evaluated once at apply, a refused apply is not reversed on removal) | `Game/Spells/Auras/AttackSpeedAuras.cs` | vmangos `SpellAuras.cpp:5092-5166` |
| `SPELLMOD_HASTE` on the applied amount through `SpellSystem.SpellModifiers` (`SpellModOp.Haste`, identity until a modifier engine exists); removal reverses the modified amount | same | vmangos `SpellAuras.cpp:5094-5098` |
| `SpellSystem.RangedAttackSpeedPct` now reads the unit's real multiplier, so Aimed Shot shaped casts follow Rapid Fire | `Game/Spells/SpellSystem.Ranged.cs` | vmangos `SpellEntry.cpp:504-507` |
| Normalized weapon damage reads the unhasted speed (haste does not change damage per hit) | `SpellSystem.Combat.cs` `WeaponDamageRoll` | vmangos `SpellCaster.cpp:1826-1833` |
| `PlayerStatSystem.ApplyWeapon` writes the weapon delay through `SetAttackTime` so a weapon swap under haste keeps the haste | `Game/Stats/PlayerStatSystem.cs` | vmangos `Unit.h:407` |

Data checked against classic-db `spell_template` (full Spell.dbc dump): Rapid Fire 3045 +40 and Quick Shots 6150 +30 are aura 140; quiver spells 14824-14829 are aura 141 (+10..+15); thrown weapons carry `ammo_type` 4, so quivers speed them up (retail quirk, pinned by a test).

## vmangos float32 artefact (deliberate)

vmangos stores the attack time as a float and truncates on every read, so apply then remove of a percent can come back one millisecond short, and `GetAttackTime` of a hasted 2800 ms weapon can read 2799. The port reproduces this; tests derive expected numbers with the same float32 sequence and otherwise assert 1 ms ranges.

## Limits (recorded, not delivered)

* Seal of the Crusader's main-hand damage reduction (`SpellAuras.cpp:5106-5111`) needs the unit-mod TOTAL_PCT ledger.
* Druid cat/bear weapon speed override (`IsAttackSpeedOverridenShapeShift`) belongs to the druid-forms lane.
* Quivers and ammo pouches do not yet apply their equip spell (no ON_EQUIP engine; bag slots 19-22 never reach a hook): aura 141 is wired but only a cast spell can apply it today.
* The `-100` percent guards are unreachable for auras (a -100 amount takes the negative branch as +100); ported verbatim.
