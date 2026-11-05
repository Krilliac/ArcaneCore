# Population share per worker, and band-aware decide

Design note, decisions kept. Code: `PopulationMapScope.ShareOf`, `PopulationBudgetController.Decide`, `PlayerbotPopulationFeature.RunAsync`.

## 1. Capacity-weighted share of the realm-wide target

Today a split worker's share of the realm-wide bot count is even over the allowed maps (the remainder to the lowest map ids), summed over the maps it owns. The workers own disjoint maps, so the shares add up to the count exactly with no exchange between workers.

Problem seen on the split stage: an even share ignores how many bots a map can hold. A worker that owns a small or sparsely spawned continent hits its zone caps while a worker that owns a large one stays under its share.

Weighting needs every worker to compute the same weight for every allowed map, not only the maps it owns, so that the shares still add up to the count. The surveyed, terrain-located capacity (`PlayerbotPopulationFeature.EnsureCapacity` surveys `ScopeOf(world).Maps`) is confined to owned maps, but a static capacity does not need the survey: every worker loads the creature spawn rows of all maps (the store reads every row, and the map filter by ownership only applies when map systems are created), so `CreatureContent.GetSpawns(mapId)` answers for any allowed map on any worker.

Decision: implemented. `PopulationMapScope.ShareOf(count, capacityOf)` splits the count in proportion to each allowed map's spawn count with the largest-remainder rule (ties to the lower map id) and a worker's share is the sum over its owned maps. Because every worker computes the same split over the same capacities and the workers own disjoint maps, the shares add up to the count exactly, with no exchange between workers. The even split stays as the fallback when content is absent or every allowed map reports no spawns, and `ShareOf(count)` is the even split. `RunAsync` passes the spawn count of the loaded creature content.

Limit: two workers that hold different creature content (a `.reload` that reached one worker and not the other) briefly compute different splits, so their shares can sum to a little more or less than the count until both hold the same content. The per-worker running cap (`MaxBots`) and the over-target parking still bound each worker, so this is a transient imbalance, not a runaway.

## 2. Decide compares totals; at-target can coexist with a short band

`Decide` answers from the total of running bots against the target, so a pass can read "at-target" while one level band is below its want and another is above it. Making `Decide` band-aware (adding bots for a short band although the total is at target) would exceed the operator's target, which is a hard limit: over-target parking would then undo the add on the next pass.

Decision: kept. The shortfall is handled where it belongs and is already visible:
- the band planner works out each band's want inside the total (`PopulationBandPlanner.DesiredPerBand`), and the pass reports it per band as want and have;
- relocation runs in the "at-target" state and moves bots out of full bands (`relocate` in `RunAsync`);
- a band that stays short is reported by the `bot-population/band-deficit` anomaly with the pass's held-back note, and the action log line carries the same note.
