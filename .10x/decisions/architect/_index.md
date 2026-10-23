# Architect: decisions and observations

Source: the 2026-10-22 refactor audit ([plan](../../reviews/2026-10-22-refactor-audit-plan.md), [findings](../../reviews/2026-10-22-refactor-audit-findings.md)). Finding numbers refer to the findings table.

## Decisions

- [Refactor waves](refactor-waves.md): the wave order, owner files, hazards and acceptance checks. Recorded as [ADR 002](../../adrs/002-refactor-wave-strategy.md) (Proposed).

## Observations

- [DISCOVERED] The project graph is acyclic: Kernel < {Cryptography, Protocol, Data} < Game < World.Contracts < World < Playerbots. The one structural deviation is Gateway (Exe) → World (Exe), which loads the whole game assembly just for the link protocol (#184, #148). Splitting it is a planned tier change, not a refactor item.
- [DISCOVERED] Inside Game there are namespace-level cycles: Spells ↔ Combat, Crafting, Creatures, Death, Entities, GameObjects, Honor, Instances, Items, Locomotion, Maps, Pets, Progression, Ranged, Reputation, Skills, Stats, Totems (#9, #29, #59). A project split would touch hundreds of files and the hot-module surface. Wave 0b freezes the current edges with an IL-based test instead.
- [DISCOVERED] Several god types are built from partial classes: SpellSystem (89 partials, ~14k lines), CreatureMapSystem (43 partials), QuestNpcServices (23), GameObjectMapSystem, Map (2,020 lines), WorldRuntime, InstanceManager and PlayerbotBrain (#6, #17, #18, #30, #39, #40, #85, #138, #211). Extracting collaborators is **deferred**, because it splits shared private state and reflection and string lookups reach the internals. Only pure partial-file splits are in scope (Wave 3).
- [DISCOVERED] Shipped tools reference projects under `tests/ThirdParty`: Atlas → DotRecast, Sniff → WowWorldMessages (#185). Moving them has low value, because doc gates and the devskim ignore-glob depend on the path.
- [DISCOVERED] The Playerbots build target writes the Playerbots dll into World's output directory, and World reaches it through InternalsVisibleTo (#187, #186). Deploy scripts string-match the target name, so leave this alone until tier 2c.
