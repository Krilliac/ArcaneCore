# Druid interruption and leech threat — October 5 local qualification

This bounded follow-up adds missing Druid form interruption cleanup and explicit direct/periodic leech healing-threat origins. Local qualification passed:14715 tests,6 existing skips,0fail; focus549/native59, clean Release, complete six-project gate. Prior499-file Shadowform/direct-healing qualification remains preserved. No schema changes.

## Druid apply-time interruption

The Druid branch now calls the existing `RemoveShapeshiftingCancels` helper immediately after removing competing shapeshift holders, only for a catalog row without the Stance flag. The incoming holder is excluded by reference. Warrior stance rows retain their existing exception. Druid power, boosts, root/slow cleanup, visuals, equipment callbacks and removal ownership are unchanged.

Pinned vmangos0e3ff01e76d4758e8a7c3108b2717cc785ed56fa `SpellAuras.cpp:2512-2517` removes auras marked `AURA_INTERRUPT_SHAPESHIFTING_CANCELS` for non-stance forms. Mangos-classic8ec338a1704e7dcb1c0213eb7ed58f9231ade40f `SpellAuras.cpp:1920-1923` confirms the generic cleanup. Catalog rows remain supplied fixtures/developer DBC data; no default row or modern form behavior is fabricated.

Four focused cases cover Cat/Bear flagged-versus-unflagged auras, incoming flagged-holder exclusion and the Warrior stance exception.

## Leech healing threat origin

Direct `SPELL_EFFECT_HEALTH_LEECH` now supplies explicit `NoThreat` for the self-heal. The World sink still restores effective health and returns its amount; it skips only that heal's threat assist. Harmful damage threat, damage/heal publication, multiplier, life guards, proc calls and ordering are unchanged.

Pinned vmangos `SpellEffects.cpp:1852-1880` and mangos-classic `SpellEffects.cpp:1902-1931` both heal without `threatAssist` for direct health leech. This removes an extra healing-threat component previously added by the legacy World sink.

Periodic health leech supplies explicit `PeriodicLeech`, retaining vmangos half effective-gain healing threat, hostile-reference split, matching-school multiplier and helpful suppression. vmangos `SpellAuras.cpp:5927-6014`, especially6013, issues that assist. Mangos-classic `SpellAuras.cpp:4597-4700` explicitly suppresses periodic health-leech healing aggro at4688-4690; this fork difference is recorded rather than merged. The vmangos1.12 contract defines this slice.

Default four-argument sinks retain compatibility through the typed overload's delegation. Ordinary direct Paladin quarter-coefficient and other/periodic half-coefficients are unchanged. Mana leech, health funnel, flat/talent threat and full owner forwarding remain separate pending work. Periodic caster ownership uses the existing resolved aura caster.

A real World producer regression establishes two hostile references, tests direct damage-only threat with actual restored health, verifies both periodic heal assists are distributed to both references, and tests periodic overheal/helpful suppression with matching/nonmatching school modifiers. Synthetic fixtures have explicit ranges/dice and neutral crits. Dedicated packet/proc parity is not claimed; source ordering is unchanged and existing broader tests remain part of the full gate.

Original build5875 client/DBC and external-provider acceptance of later local changes remain pending. WoWWiki1.12.1 and Wowhead Classic remain supplemental version-checked sources. Proprietary material stays local.
