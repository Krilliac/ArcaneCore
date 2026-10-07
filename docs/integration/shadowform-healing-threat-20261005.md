# Shadowform and direct healing threat — October 5 local qualification

This bounded follow-up adds catalog-gated Shadowform lifecycle and the pinned vmangos direct healing threat coefficient. Local qualification passed:14710 tests,6 existing skips,0fail; focus527/native59, clean Release, complete six-project gate. Prior Ghost Wolf/school threat qualification is preserved. Original client/DBC and external-provider qualification remain pending. No schema changes.

## Shadowform lifecycle

`FORM_SHADOW = 0x1C` is already in the shared enum. Imported spell15473 reaches `MOD_SHAPESHIFT`; an optional supplied `ShapeshiftFormCatalog` row is required. Entering removes competing shapeshift holders and other auras marked `AURA_INTERRUPT_SHAPESHIFTING_CANCELS`, writes the form byte and owns `FormHolder`. Removal clears the form only for the owning holder, performs existing shape-loss cleanup and notifies the equipment form sink.

The form has no model overlay or boost in the pinned source. This branch leaves display/scale, mana/power type and independent speed/transform owners alone. Leaving an earlier visual-bearing form removes that form's overlay through its existing removal path. Shadowform's independent aura79/87 effects are outside this lifecycle slice; no invented DBC row, display, speed or boost is added.

Sources: vmangos0e3ff01e76d4758e8a7c3108b2717cc785ed56fa `SharedDefines.h:1434-1441`, `SpellAuras.cpp:2387-2433,2512-2517,2605-2621,5484-5491`; mangos-classic8ec338a1704e7dcb1c0213eb7ed58f9231ade40f `SpellAuras.cpp:1786-1804,1847-1852,1920-1939,4282-4288`, spell_template row15473. Original `SpellShapeshiftForm.dbc` and client visual acceptance remain pending.

## Healing threat origin

`IDamageSink.Heal` adds an explicit origin overload with compatibility delegation to existing four-argument sinks. Direct `EffectHeal` and the shared `HEAL_MAX_HEALTH` tail supply Direct; periodic aura healing supplies Periodic. Existing health-leech/drain callers retain Legacy behavior.

The World sink uses effective restored health: Direct with the supplied caster class Paladin uses0.25; other Direct, Periodic and Legacy use0.5. Threat is split over the existing living same-map hostile references and passed through the shared school multiplier/helpful-suppression calculation once. No lookup invents a pet/totem real caster; the caller's already resolved unit is retained.

Source contract: vmangos pin `Spell.cpp:1360-1365` selects0.25 for direct Paladin heals versus0.5 otherwise. Periodic paths in `SpellAuras.cpp:6013,6087,6256,6297` retain0.5. CMaNGOS `Spell.cpp:1249` uses0.5 with a per-spell multiplier; this fork difference is recorded rather than silently combined. The vmangos1.12 behavior defines this slice.

Regression coverage exercises real direct and periodic producers against two hostile references, matching Holy/nonmatching Fire modifiers, overheal, suppression and actual non-Paladin class metadata. Synthetic fixtures use explicit dice and a neutral crit seam. Legacy leech threat semantics, full ownership forwarding, flat spell metadata and talent multipliers remain separate pending work. WoWWiki1.12.1/Wowhead Classic remain supplemental version-checked information sources; modern Classic data does not establish original5875 client behavior.
