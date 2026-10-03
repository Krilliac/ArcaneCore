# Integration notes: casters lane (mage, priest, warlock)

Area doc: [docs/areas/casters.md](../areas/casters.md). Schema: none (no version number is claimed).

## Seams used

- `IWorldFeature`: `ArcaneCore.World.Spells.Casters.CasterFeature` (constructor-injected `SpellFeature`, so attach order does
  not matter). It binds `Spells:Casters` and calls `CasterSpellModules.Register`. Each later caster slice appends one line to
  `CasterSpellModules.Register`.
- `SpellSystem.RegisterAura`: aura types 72, 73 (`PowerCostAuras`), 53, 64 (`DrainAuras`). Quest-reward preflight models only
  the built-in handler table, so none of these changes reward behaviour.
- `SpellSystem.RegisterEffect` is not used; `SPELL_EFFECT_DISPEL` stays in the built-in table (see below).

## New public surface on shared types

- `SpellSystem.AmountModifier` (`ISpellAmountModifier`, `SpellAmountStage`, in `Spells/ISpellAmountModifier.cs`): one slot, null by
  default. If spell-breadth S1 lands `ISpellDamageModifiers`, implement it from `SpellBonusModule` and delete this slot (the call
  sites are the five listed below).
- `SpellSystem.DispelResistChance` (talent spell mod seam), `SpellSystem.DispellableAuras(Unit, Unit, int)`.
- `UnitCombat.LastManaUseSpellId`, `UnitCombat.NoteManaUsed(uint spellId)`.
- `SpellAuraHolder.ChannelTarget` (internal).

## Edits to shared files (all small and additive)

| File | Change | Why |
|---|---|---|
| `Spells/SpellSystem.cs` | `TakePower` takes the `triggered` flag and starts the five second timer for paid, non-triggered mana casts without Ex2 `DONT_BLOCK_MANA_REGEN`; `CalculatePowerCost` applies creature-level scaling and the school percent multiplier | five second rule, aura 72 (`Spell.cpp:5077, 7026-7036`) |
| `Combat/MapCombat.cs` (`UpdateUnit`) | the timer no longer runs out while the unit channels the spell that took the mana | `Unit.cpp:235-253` |
| `Combat/UnitCombat.cs` | `LastManaUseSpellId`, `NoteManaUsed(uint)` | same |
| `Spells/SpellSystem.Auras.cs` | `TickTriggerSpell` targets the channel target; `EffectApplyAura` stores the channel target on the holder and passes periodic amounts through `SnapshotAuraAmount`; `TickPeriodicDamage` / `TickPeriodicHeal` call `ModifyTick` | Arcane Missiles, DoT/HoT snapshots |
| `Spells/SpellSystem.Effects.cs` (`EffectSchoolDamage`, `EffectHeal`), `Spells/SpellSystem.Combat.cs` (`EffectHealthLeech`) | the amount goes through `ModifyDirect` | spell power |
| `Spells/SpellSystem.Combat.cs` | `EffectDispel` and `DispellableAuras` removed; they now live in `Spells/Casters/Dispel/SpellSystem.Dispel.cs` (same names, same built-in table entry) | dispel fidelity |
| `Spells/SpellAuraHolder.cs` | `ChannelTarget` | channel trigger target |

New partials (`SpellSystem.Amounts.cs`, `Casters/Dispel/SpellSystem.Dispel.cs`) keep the logic out of the shared files.

## Merge notes

- The amount-modifier call sites (`EffectSchoolDamage`, `EffectHeal`, `EffectHealthLeech`, `EffectApplyAura`, the two tick
  methods) are the likely conflict points with spell-breadth S1/S2, warrior S02/S08b and the stats lane.
- Regen: this lane starts the five second timer; the stats lane's regen consumer should honour mp5, the aura 134 interrupt
  percentage and the channel hold that is implemented in `MapCombat.UpdateUnit`. Merge them together so casters do not sit at
  zero regeneration for 5 s without the offsets.
- `RegisterAura` replaces silently: if another lane registers aura 53, 64, 72 or 73, one registration must go.
- Verification gaps (honest): the tests were written against synthetic spells; spell-bonus-math, dispel and drain have no
  assertion-level RED run (their types did not exist, the first run was green); mana-spend-rule and channel-trigger-targets had a
  genuine RED run (4 and 1 failures at HEAD behaviour). No real client was involved (dispel, drain, mana leech packets follow
  wow_messages but have no capture).
