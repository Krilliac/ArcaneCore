# Next slices — 2026-10-04

Item cooldown ownership, independent category expiry, pet cooldown persistence,
and actual-target consumable checks are locally qualified. Characters schema
22/23 preserves the original spell-cooldown shape; the qualified cooldown
milestone has 14,356 passing tests and six existing fixture skips. Its MockClient
result is a documented composite after one retained quest-capacity timeout.

The current wave adds queued item context, nonlethal hit durability, and food/
drink regeneration. Its integration and qualification are recorded separately
in the wave reports; implementation alone is not an acceptance result.

ON_EQUIP spells, hunter effect-56 call-current and food/drink heartbeat visuals
are implemented in the next wave; its reports define their current verification
boundary. Equip form-change re-evaluation remains pending (Player.cpp:7231-7240).

Effect 56's hunter call-current form (entry zero) uses the cached current pet and
source tame-failure reasons. A real dismissal producer remains pending. Nonzero summon,
warlock replacement, subtype/loyalty and tame/stable production remain separate
work; the existing generic summon path is not proof of those behaviors.

Broader regeneration auras, rage Anger Management, original-client acceptance,
proprietary content and external MariaDB/PostgreSQL qualification remain pending.
No schema number is reserved for future work.
