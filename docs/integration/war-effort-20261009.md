# AQ war-effort resource state — 2026-10-09

## Source and state

`mangos-classic` `WorldState.cpp`/`WorldState.h` define the 30 resource counters, their world-state fields and totals, phase 1 collection, phase 2's five-day timer, and `IsConditionFulfilled` for type-40 world-script conditions. `scripts/world/war_effort.cpp` maps each first and repeatable quest to one resource and increments by its first required item count. The mapping in `WarEffortCatalog` was checked against the copied ClassicDB z2815 `quest_template` and 31 AQ type-40 rows; `WarEffortImportedContentTests` repeats the imported-data check when `ARCANECORE_TEST_WORLD_DB` is set.

The realm-wide state is in characters schema 43: `world_war_effort_phase` and `world_war_effort_counter`. A quest's resource contribution joins the existing serializable character reward transaction. A refused duplicate, conflict, or rollback leaves the counter unchanged. If all 30 counters reach their goals, that transaction saves phase 2 and a five-day deadline. The World feature reloads the saved state every five seconds, so condition 40 and phase events catch up even if a player disconnects after the commit. The resource condition compares `count == goal`, matching the reference, while the phase transition uses `count >= goal`. Condition 2113 compares the reference's floor of days remaining plus one.

## Operation and limits

An administrator can start collection on a copied or production profile with `.event start 120` after applying schema 43. The phase event is server-side; the feature saves phase 1 when event 120 starts. The owned phase keeps events 120, 121, and 122 in step with the saved state, including after restart. The schema upgrade and event-start command were exercised on an isolated backup of the stopped wave14 profile; original and live databases were not changed.

The resource counters, condition facts, phase 1 to 2 transition, and phase 2 deadline to phase 3 are implemented. The gong turn-in, ten-hour war, final phase, resource-pile gameobjects, world-state HUD packets, and Scourge invasion state remain future work. Normal-player item turn-in and an original-client run remain to be observed; focused tests cover the transaction, duplicate handling, imported quest data, live condition, and event transition.
