# Area: ranged combat and haste (wave 4, lane "ranged-combat")

Branch `claude/vw5-ranged-combat`, based on the verified integration branch `claude/vw4-integration` at 7313b9e (waves 1-3). Standing directive: retail 1.12.1 as in vmangos, mechanics and data; every deviation sits behind a config option that defaults to retail. References are read-only (`D:\refs\vmangos`, `mangos-classic`, `wow_messages`, `classic-db`); nothing was copied. No Characters, World or Auth schema was touched: **stores exercised: none**, no schema constant, no provider-aware (SQLite/MariaDB/PostgreSQL) theory was needed.

The hunter lane (wave 2) stays documented in `docs/areas/hunter.md` (corrected by this lane: classic-db `spell_template` is a full Spell.dbc dump and Throw is not an auto-repeat spell).

## Slices delivered

| Slice | Delivered | Doc |
|---|---|---|
| S01 attack speed core | `m_modAttackSpeedPct`, float32 shadow of the ATTACKTIME fields, `GetAttackTime` = unhasted base, auras 9 / 138 / 140 / 141, `SPELLMOD_HASTE`, real `RangedAttackSpeedPct`, normalized damage on the unhasted speed | `ranged/attack-speed.md` |
| S03 swing gate and cast reset | retail early return while casting; casts with the combat interrupt bit restart the melee timers; `Combat:CastingConsumesSwing`, `Combat:CastResetsMeleeSwing` | `ranged/swing-interplay.md` |
| S08 creature ranged fields | creatures get ranged min/max damage and ranged attack power | `ranged/creature-ranged.md` |
| S02 auto-repeat slot | Auto Shot and wand Shoot, wind-up, shot cycle, cancel packets, CMSG 621 / SMSG 668, retarget, interrupt sources, melee suppression | `ranged/auto-repeat.md` |
| S04 ranged damage inputs | ammo DPS, weapon attack type, auras 127 / 131 / 113 / 185 | `ranged/damage-inputs.md` |
| S05 wand and thrown | wands roll the ranged hit and crit tables and deal the wand's school; Throw and Shoot Bow/Gun/Crossbow pinned as single shots | `ranged/wand-thrown.md` |
| S06 quivers | ON_EQUIP haste aura of quivers and ammo pouches, bag-slot events, login replay | `ranged/quiver-haste.md` |

## Not delivered

* **S07 weapon-dependent crit aura (52 MOD_CRIT_PERCENT: Lethal Shots, Bow/Gun/Crossbow/Wand Power)**: aura 52 is a plain aura-engine handler that the aura-engine-completeness lane may register; a second registration makes `SpellSystem.RegisterModules` throw at startup. The stats lane left the slot ready (`PlayerStatSystem.UpdateCritPercentage` passes `flatMod = 0`, `StatFormulas.CritPercentage` already takes it). The retail rule to port: `Aura::HandleAuraModCritPercent` (`SpellAuras.cpp:5021-5049`) and `Player::_ApplyWeaponDependentAuraCritMod` (`Player.cpp:7029-7070`), with `SpellInfo.EquippedItemClass/SubClassMask/InventoryTypeMask` (all present) against the wielded ranged weapon.
* **S09 projectile flight time**: 132 of the 150 non-test ranged-slot spells have projectile speed 20-50 (landing after `max(dist, 5) / speed`, `Spell.cpp:925-939`, `3752-3790`). It needs a delayed cast state in `SpellSystem.Cast` that the spell-core lanes may also touch (every Speed > 0 spell, 899 of them, shares it), and a client playtest. The `Ranged:Projectile:FlightTime` option from the design was therefore not added.

## Config options (all default to retail)

| Option | Default | Deviation |
|---|---|---|
| `Combat:CastingConsumesSwing` | false | true: a cast consumes the swing timer (the pre-wave-4 behaviour) |
| `Combat:CastResetsMeleeSwing` | true | false: casts leave the melee timers alone |

## Deviations from retail that remain

* The 500 ms ranged-slot cast time (`SpellEntry.cpp:511-512`) is missing for spells whose CastTimes row is the all-zero instant row (see `ranged/wand-thrown.md`).
* Seal of the Crusader's damage reduction, the druid shapeshift attack speed override, the melee analogues of auras 126 / 130 / 112 / 184, the `UNIT_MOD_DAMAGE_RANGED` TOTAL_PCT multiplier of the ranged done bonus, wand partial-resist ranges and the wand global cooldown are not modelled (each is listed in its slice doc).
* Cancel of the auto-repeat on taxi start and on `SPELL_ATTR_CANCELS_AUTO_ATTACK_COMBAT` casts are not wired.
* SpellRange.dbc is in no reference: the dead zone and range numbers of Auto Shot (range index 114), Throw (74), wand Shoot (4) are unverified.

## Open questions for the developer

1. vmangos' float32 attack time truncates (a hasted 2800 ms weapon can read 2799 from `GetAttackTime`); the port mirrors it and tests derive numbers with the same float32 sequence. Confirm that is the behaviour to keep.
2. Auras 9 / 138 / 140 / 141 are claimed by `AttackSpeedAuras`; if the aura-engine lane registers them too, `RegisterModules` fails at startup (loud, by design). Keep this module.
3. Which lane owns the general ON_EQUIP engine (quivers are a thin special case today)?
4. Needs a real-client playtest (hybrid: the developer clicks, the server log is read): Auto Shot toggle and the 668 cancel, the 0.5 s wind-up feel, the dead zone messages, quiver speed in the tooltip.
