# Druid boosts, enchant procs and reserved-name reload

Qualified locally with a complete serialized Release suite: 14,565 passes,
six existing fixture skips and zero failures. Focused checks pass 264 cases;
the native mock self-test passes 59. Original build-5875 client and DBC
acceptance and external database providers remain pending.

Druid form changes cast linked boosts, known stance passives, Leader of the
Pack and custom Heart of the Wild amounts through normal spell/aura creation.
Cleanup targets the exact self-owned holders. Total-stat percentage auras
compose with flat item, aura and progression contributions, refresh attached
combat stats, and preserve the health ratio for stamina spells with attribute
0x10. Positive and negative stat tooltip fields are floats and both are scaled.

The optional 24-field SpellItemEnchantment DBC and World schema 22 PPM table
feed equipped weapon combat procs. Loaded skill rank chains resolve first-rank
overrides for higher ranks. Chance modifiers apply to enchant rolls, matching
the separate source branches. Seven enchant slots, item GUID provenance,
positive-target selection and limited charges are covered. Online millisecond
countdowns write remaining duration into persisted item fields; zero duration
remains untimed. Refresh, charge clear, acquisition and inventory snapshots
preserve current slot identity. Broader enchant stat/equip-spell effects,
enchant spell producers and extra-attack execution/recursion remain pending.

Reserved-name reload builds a scoped frozen SQL candidate and publishes it
atomically on the world thread, with rollback retaining the previous policy.
DBC catalog components stay intact. Staff bypass SQL-only character-name
reservations; pet reservations apply to every caller.

The first full run retained a 5,856-byte failure in the unchanged zero-allocation
test. Its isolated default-runtime run and the fresh complete suite pass;
the cause of the earlier failure remains unproven. Failed evidence is retained.

Pinned vmangos remains the primary implementation reference. WoWWiki 1.12.1
and Wowhead Classic supplement it with version checks and corroboration.
