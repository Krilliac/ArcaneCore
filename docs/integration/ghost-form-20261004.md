# Ghost form — 2026-10-04

This slice connects spirit release and persisted ghost login to the production spell system. Spell definitions remain imported content: the server does not fabricate spells 8326, 20584 or 20585 when data is absent.

## Behavioral source

The behavioral reference is vmangos/core at `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `Player.cpp:4561–4578` (`ApplyGhostForm` / `RemoveGhostForm`), `SpellAuras.cpp:5639–5660` (`HandleAuraGhost`), and `UnitDefines.h:88,147`. The implementation is clean-room C#; no upstream source or content data is vendored.

Aura 95 changes `UNIT_FIELD_BYTES_1` byte 3 bit `0x01` and, for players, `PLAYER_FLAGS_GHOST`. Other visibility bits and player flags are preserved. Removing one ghost aura clears those bits even when another remains, matching the source handler. This aura does not set the combat death state or independently imply water walking. Party status is derived from player flags by the existing group update loop.

Spirit release casts 20584 when the character knows 20585, then casts 8326. This checks the spellbook rather than race, so an imported or administratively granted Wisp Spirit ability controls the choice. Resurrection removes the corresponding spells. Combat remains responsible for the explicit water walking transition, including content-free fallback. An actual 104 effect uses the ordinary movement acknowledgement mechanism.

The world combat adapter retains the previously selected hooks and forwards faction, weapon skills, equipment, rewards, graveyard, resurrection sickness, instance checks and creature lookup. Registered custom ghost callbacks still run. Hosts without a `SpellFeature` retain their existing hooks.

Persisted ghost login rebuilds the canonical form before restoring saved auras. The World loader reuses an already-live, permanent, self-owned ghost holder for 8326 or 20584 instead of replacing it with the saved permanent row. This avoids water-walk apply/remove/apply orders during hydration. Other saved auras, finite rows and foreign-owned holders retain ordinary restoration. The regression also restores an unrelated timed aura with its saved amount and duration. Combat checks the latest pending water-walk order before adding its fallback, while login still forces the client transition when no matching order exists.

Public aura removal observes quest-settlement holds; callers retry after the hold ends. Aura lookup and removal also require the exact target object, so an old player object cannot mutate a same-GUID replacement.

## Verification and limits

`GhostAuraTests` exercises discovery, bit preservation, source behavior for multiple ghost auras, quest settlement, non-player units, and player GUID reuse. `GhostFormWorldTests` uses synthetic spell definitions with the real production world thread, handlers, death hooks and spellbook. Human and orc fixtures with and without an administratively granted Wisp Spirit prove the spellbook predicate independently of race; actual night elf starting content is unavailable in this harness. The tests also cover removal on resurrection, persisted ghost login, missing-content fallback, and preservation of registered combat callbacks. Synthetic templates prove execution and integration, not the exact imported 1.12.1 DBC values or real-client rendering. The [combined integration handoff](server-revival-20261004.md) records final build and test results.
