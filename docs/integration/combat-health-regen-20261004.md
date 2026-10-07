# Combat health regeneration aura 116 — 2026-10-04

The bounded slice adds `ModRegenDuringCombat` registration and combat-health
integration. During combat, the spirit-derived health amount is multiplied by
the active aura total divided by 100; without aura 116, combat health remains
suppressed. Food modifiers remain out of combat only, and the rage-decay gate is
unchanged. Aura 161, health-rate configuration, and polymorph regeneration stay
pending.

Focused Game coverage uses a real ApplyAura producer and verifies suppression,
active combat regeneration, and removal. Coordinator verification passed:
Release0/0, six-project suite14,422 passed / six existing skips / zero failures,
native mock59. The socket producer has an explicit30s duration and asserts the
live100% modifier and continued combat state; default300ms duration was too short
for the normal two-second regen tick. Initial failed evidence is retained.
