# Mana regeneration interruption aura (2026-10-04)

This slice wires `SPELL_AURA_MOD_MANA_REGEN_INTERRUPT` (AuraType 134 / `0x86`) into the existing player mana tick. The aura is a percentage of the spirit-derived mana component retained during the five-second post-spend window; the flat `MOD_POWER_REGEN` drink/mp5 component remains active in both states. `MOD_POWER_REGEN_PERCENT` (AuraType 110) now also contributes to the mana spirit factor, matching the same query already used by rage and energy.

The contract follows vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`: `Spells/SpellAuras.cpp:199` registers `HandleAuraModRegenInterrupt`, `StatSystem.cpp:648-660` caps the total percentage at 100 and combines it with the flat mp5 component, and `Objects/Player.cpp:2298-2308` selects that interrupted rate only while `IsUnderLastManaUseEffect()` is true.

ArcaneCore's `UnitCombat.LastManaUseTimer` remains the five-second gate. `CombatOptions.GetDrinkPowerRegen` remains the flat contribution, while `MapCombat.RegeneratePower` applies Aura 110 to the spirit term and Aura 134 to that resulting spirit term. No schema or persistence change is required.

Game coverage proves real aura application, additive holders, removal, 50/100/>100 percentages, multiplicative Aura110 stacks, RateMana and power caps, with flat mp5 outside both spirit factors. World coverage uses a real mana-cost socket cast, observes the power after cost at aura application, checks the interruption spell/timer, then waits for actual interrupted and normal regeneration ticks. Synthetic spell rows exercise the handler seam; original build-5875 talent mapping and original-client acceptance remain pending because verified proprietary DBC fixtures are not present in this checkout.
