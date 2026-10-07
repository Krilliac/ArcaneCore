# Death and creature aura lifecycles — 2026-10-04

This tranche closes two retained-aura lifecycle gaps using normal aura removal handlers: the dying Hunter's Mark caster loses its mark on the current map, and a creature's old-life aura contributions are cleared before corpse disposal and fresh respawn initialization. Auras cast by the dying unit on other targets otherwise retain their existing behavior. Passive and death-persistent auras remain on the creature at death.

The reference is vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, inspected outside the repository:

- `Objects/Unit.cpp:3969-4004`: `RemoveAllAurasOnDeath` first clears caster-owned single-target `MOD_STALKED` holders, then removes the dying target's non-passive, non-death-persistent holders.
- `Objects/Unit.cpp:3227-3261`: only holders marked single-target enter the caster's single-cast registry.
- `Spells/SpellAuras.cpp:6668`, `Spells/SpellEntry.h:995-998`, and `Spells/SpellMgr.cpp:3953`: single-target status comes from the database `Custom` flag (`0x100`), rather than a DBC attribute or the aura type alone.
- `Spells/SpellClassMask.h:251`: Hunter's Mark is Hunter family 9, bit 10 (`0x400`).
- `Objects/Creature.cpp:823-879`: respawn clears all old holders before rebuilding fields and invoking the fresh AI's respawn hook.

ArcaneCore does not currently import the `Custom` single-target flag or maintain the general single-cast registry. This change uses a bounded Hunter family 9 / bit 10 / `MOD_STALKED` classifier. It deliberately retains priest Mind Vision and unrelated/custom stalked auras. General database-controlled single-target classification and rank exclusivity remain follow-up work; no real spell-template dump was available to verify custom flags.

Mark cleanup checks the exact aura caster ownership token, target object identity, and current map resolution. A same-GUID replacement caster or a restored foreign aura with only historical caster GUID does not acquire that ownership. If either participant is held for quest settlement, cleanup records the exact holder and waits for the hold to end. The eventual removal cannot delete a replacement holder created after the death.

Creature cleanup uses real lifecycle events. The corpse-removal event runs before the existing spell-state disposal; the respawn event runs before field initialization and the AI's `OnRespawn`. Normal removal reverses stat and flag contributions, marks holders removed, clears visible slots and area children, and revokes the old aura caster lifetime. Fresh script passives may then apply with new ownership. The common respawn path covers natural and forced respawns, including an aura reapplied by a script during the invisible `DEAD` interval before the next spell-system update.

Existing ArcaneCore timing remains an explicit limit: corpse disposal already drops creature spell state earlier than upstream's respawn-time clear. This change performs handler cleanup at that same disposal point and does not retain out-of-world creature aura state. Attribution of foreign auras cast by the old creature still expires when the corpse leaves the world. Generic spell-state forgetting is unchanged.

Regression coverage uses synthetic templates and production handlers. Game tests cover selective Mark cleanup, tracking flags and stat deltas, held target debt, orphaned persisted ownership, replacement caster/holder identity, cross-map exclusion, and living-caster no-op. World tests run natural and forced corpse/respawn paths through the real creature and spell features, inject a legal death-persistent self aura during `DEAD`, and assert that fresh AI passives have exactly one contribution and new caster attribution.

The baseline exposed three missing Mark death cleanup cases. Both World respawn cases also failed before the fix: after corpse disposal, the next 11-point aura produced 29 strength because the previous passive and persistent holders' 18-point contribution had been forgotten without removal handlers. Final results are recorded in the [combined integration handoff](server-revival-20261004.md).
