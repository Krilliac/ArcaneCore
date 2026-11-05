# Population share per worker, and band-aware decide

Design note, decisions kept. Code: `PopulationMapScope.ShareOf`, `PopulationBudgetController.Decide`, `PlayerbotPopulationFeature.RunAsync`.

## 1. Capacity-weighted share of the realm-wide target

Today a split worker's share of the realm-wide bot count is even over the allowed maps (the remainder to the lowest map ids), summed over the maps it owns. The workers own disjoint maps, so the shares add up to the count exactly with no exchange between workers.

Problem seen on the split stage: an even share ignores how many bots a map can hold. A worker that owns a small or sparsely spawned continent hits its zone caps while a worker that owns a large one stays under its share.

Weighting by a static per-map capacity (the surveyed creature spawns of a map) needs every worker to compute the same weight for every map, not only the maps it owns, so that the shares still add up to the count. Each worker loads and surveys spawn and terrain data of its own maps only (`PlayerbotPopulationFeature.EnsureCapacity` surveys `ScopeOf(world).Maps`). Surveying all allowed maps on every worker would repeat the whole survey (terrain tiles included) per worker, and two workers with different data versions or a failed survey would compute different weights and the shares would no longer sum to the count. The alternative, an exchange of each worker's capacity through the shared registry, is a new cluster protocol and out of scope here.

Decision: keep the even split as the safe default. It is the only share that is exact without agreement between workers. A capacity-weighted share is recorded as a design item that needs either a published per-map capacity table (generated once, shipped as data, identical on every worker) or a registry exchange. Until then the operator sizes the stage with `Cluster:Worker:OwnedMaps` so each worker's maps carry a comparable capacity.

## 2. Decide compares totals; at-target can coexist with a short band

`Decide` answers from the total of running bots against the target, so a pass can read "at-target" while one level band is below its want and another is above it. Making `Decide` band-aware (adding bots for a short band although the total is at target) would exceed the operator's target, which is a hard limit: over-target parking would then undo the add on the next pass.

Decision: kept. The shortfall is handled where it belongs and is already visible:
- the band planner works out each band's want inside the total (`PopulationBandPlanner.DesiredPerBand`), and the pass reports it per band as want and have;
- relocation runs in the "at-target" state and moves bots out of full bands (`relocate` in `RunAsync`);
- a band that stays short is reported by the `bot-population/band-deficit` anomaly with the pass's held-back note, and the action log line carries the same note.
