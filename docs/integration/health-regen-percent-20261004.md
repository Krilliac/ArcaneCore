# Aura 88 health regeneration integration

This slice adds `SPELL_AURA_MOD_HEALTH_REGEN_PERCENT` (AuraType 88) using the vmangos `Player::RegenerateHealth` ordering (`D:\refs\vmangos\src\game\Objects\Player.cpp:2369-2392`). The active Aura88 holders contribute a multiplicative product of `(100 + amount) / 100` to the spirit health result only while out of combat.

Aura88 does not make a player eligible for combat spirit regeneration. Aura116 remains the combat spirit gate/scaler; Aura161 remains the always-applied `Rate.Health * 2 * (sum161 / 5)` flat contribution. Food remains out-of-combat-only and is not `Rate.Health` scaled. Existing fractional carry, max-health cap, and nonnegative clamp remain in `MapCombat.Regen`.

The aura source exposes a default factor of `1.0` for existing fakes, and `HealthRegenPercentAuras` registers the real Aura88 holder. Game tests cover multiplicative stacking/removal, combat gates, Rate.Health versus food, negative factors, and cap behavior. The World test applies a real socket-cast Aura88 with explicit duration/dice and verifies out-of-combat regeneration stops while combat remains active.

Polymorph remains separate because the references use a spell-specific live predicate (`vmangos Unit::IsPolymorphed` or mangos-classic aura 12939), not generic Transform/Confuse/mechanic state.

Coordinator-owned builds and tests remain pending for this implementation lane.
