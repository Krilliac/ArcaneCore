# AQ war effort: resource piles and capital counters — 2026-10-10

This depends on `aq-war-effort-phases-20261010.md` (PR #38). It needs no schema change: everything is derived from the saved war-effort state.

## Resource piles

classic-db `Updates/4498_backport_errors.sql` adds 59 pile objects, which the z2815 importer does not load. They are guids 155000-155054 (Ironforge, map 0) and 155500-155554 (Orgrimmar, map 1); their templates (180598-180843) are already in z2815. `WarEffortPileCatalog` carries those rows: four Alliance and five Horde "Initial" piles, plus five groups × five tiers per faction. `WarEffortFeature` summons and removes them as runtime objects on loaded instance-0 continent maps on every five-second refresh, so a restart rebuilds exactly the saved tiers.

The tier rule follows vmangos `HandleWarEffortGameObject`.

- **Gathering:** for each faction's group (mangos-classic `GetResourceInfo`, three resources each), tier k is shown when the group's current count is at least objective − (5 − k) × ⌊objective/5⌋. Tier 0 ("Initial") piles show only while gathering (the 4498 rows tie them to event 120).
- **Transporting:** one tier less per elapsed day. vmangos uses its DAY1-5 events; here it is the reference days-remaining value, clamped to 1-5.
- **Gong, war and done:** tier 1 (vmangos's default case). **Disabled:** nothing.
- All tiers up to the current one are shown. The table's rotations are not applied; only the orientation is.

## Capital-city counters

`WarEffortFeature` is now an `IWorldStateProvider` for Stormwind, Darnassus, Ironforge, Orgrimmar, Thunder Bluff and Undercity (mangos-classic `FillInitialWorldStates`).

- **While gathering:** the 25 `*_TOTAL` states (the shared resources use one total for both factions) and the 30 `*_NOW` counters.
- **While transporting:** days left (2113).
- **On each refresh:** changed values are sent as SMSG_UPDATE_WORLD_STATE to players currently in a capital. States that stop applying after a phase change are sent as 0.

## Not done

- The Cenarion Hold attack waves (vmangos `npc_aqwar_ch_attack`, event 59) and the transport and Saurfang scenes are not implemented. In vmangos they belong to the phase-2 transition events.
- The four-hour timing of cmangos's piles is not used.
- Not checked with an original client or a copied production profile.

## Tests

`WarEffortPileTests` has 14 tests: catalog integrity against 4498, the gathering tier boundaries (theory), the transport and later-phase tiers, initial piles, capital states and the zone filter, and runtime summon and removal across a restart and through a transport day.
