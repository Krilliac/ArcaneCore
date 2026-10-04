# Ranged lane S03: swing gate while casting, cast resets the melee swing

Branch `claude/vw5-ranged-combat`.

## Delivered

| Rule | Where | Reference |
|---|---|---|
| While a non-melee spell (cast or channel) is in progress the swing loop returns before it looks at any timer, so the swing fires as soon as the cast ends instead of being consumed. | `Combat/MapCombat.Melee.cs` `UpdateMeleeAttackingState` | vmangos `Unit.cpp:415-421`, `Unit.cpp:2235-2241`; mangos-classic `Unit.cpp:650-654` (same order) |
| A non-triggered cast whose spell has InterruptFlags 0x08 (combat) and not Ex2 0x20000 restarts the main hand (and off hand when wielded) to the full, hasted delay when it is cast. The ranged timer is never reset by a cast. | `Combat/MapCombat.CastReset.cs`, one marked hunk at the end of `SpellSystem.Cast` | vmangos `Spell.cpp:3805-3810`, `Spell.h:302`, `Spell.cpp:4371` (ranged variant commented out) |

## Options (config section `Combat`)

| Option | Default | Meaning |
|---|---|---|
| `CastingConsumesSwing` | `false` (retail) | `true` restores the pre-wave-4 behaviour: the swing timer is consumed and restarted while casting, the swing is lost, not delayed. Existing pins of that behaviour select it explicitly. |
| `CastResetsMeleeSwing` | `true` (retail) | `false` leaves the swing timers alone on casts. |
| `MeleeCastingBlocksSwing` (existing) | `true` | `false` lets swings through while casting; still honoured by both paths. |

## Evidence

classic-db `spell_template`: 5933 of 21108 spells carry the combat interrupt bit (Throw 2764, Shoot Bow/Gun/Crossbow, Aimed Shot, Multi-Shot included; Auto Shot and Arcane Shot (Ex2 0x20000) do not). Tests use those shapes. The tests drive the spell system and the world tick from one manual clock, in steps of 50 and 100 ms.

## Limits

* Off-hand timer reset is implemented but not covered by a test (the off-hand weapon check needs the skills stack the harness lacks).
* `IMeleeSpellHooks.IsNonMeleeSpellCasted` only counts the generic cast and channel slots; the auto-repeat slot (retail counts it) belongs to the unimplemented auto-repeat slice, see `docs/areas/ranged.md`.
