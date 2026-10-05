# Aura 161 health regeneration integration

This slice follows vmangos `Player::RegenerateHealth` (`D:\refs\vmangos\src\game\Objects\Player.cpp:2355-2406`) and adds `Rate.Health` plus `SPELL_AURA_MOD_HEALTH_REGEN_IN_COMBAT` (AuraType 161).

The combat tick now enters the health phase when the player is out of combat, has Aura116, or has Aura161. Aura161 itself is an always-applied flat contribution: `Rate.Health * 2 * (sum161 / 5)` per two-second player tick. Spirit regeneration is multiplied by `Rate.Health`; food remains out-of-combat only and is not rate-scaled. Existing Aura116 combat percentage, rage-decay guard, health cap, and fractional carry remain in the existing MapCombat path.

`CombatOptions.Normalize` treats a negative `Rate.Health` like the existing negative mana/rage rates and restores the retail default of `1.0`. The aura-source interface uses a default zero-valued Aura161 query so existing test fakes remain source-compatible. `CombatFlatHealthRegenAuras` registers the real Aura161 handler, and tests exercise SpellSystem application and a world socket cast.

Polymorph's `maxHealth / 10` branch and Aura88 percent health regeneration remain separate pending slices because ArcaneCore currently has mechanic classification but no verified live polymorph state producer.

Validation is owned by the coordinator; this implementation lane did not run builds or tests.
