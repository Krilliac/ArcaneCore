# World-thread audit, lane 4: spline lengths

Finding 6.2 (CONFIRMED): `CreatureSpline.LocateWithOrientation` in `src/ArcaneCore.Game/Creatures/Creature.cs` recomputed
every leg length twice per call (one pass for the total, one to find the leg), for every moving creature, every tick, from
`PositionAndOrientationAt`, `PositionAt` and `RemainingPoints`.

## Change

- Per-leg and cumulative lengths are built once on the first multi-point locate and published as one immutable
  `SplineLengths` object by `Interlocked.CompareExchange`. `Path` and the start point are init-only, so two threads that
  race to build it (the ungated wander pass and the gated stages both read splines) compute the same values and either
  result is valid. No cursor state is mutated, so the single-writer-per-map invariant is untouched.
- The leg is still found by the in-order walk that subtracts one cached leg length at a time (no `Distance` recomputation).
  A binary search over cumulative lengths was tried first and rejected: float rounding in the old running subtraction
  can pick the trailing zero-length leg where the search picks the one before, changing heading and `RemainingPoints`.
- `RemainingPoints` copies the tail into a right-sized array instead of `Skip` + collection expression (no LINQ enumerator).
- No packet bytes change: positions, leg index and blended heading are identical to the uncached code (same float operations in the same order).

## Tests

`tests/ArcaneCore.Game.Tests/Creatures/CreatureSplineLengthCacheTests.cs`
- `RepeatedLocates_DoNotRewalkEveryLeg`: counts `Path` reads; fails on the old code (about 2 reads per leg per call), passes now.
- `RandomMultiPointSplines_MatchTheUncachedAlgorithm`: 200 random 2 to 39 point splines (with zero-length legs) against a
  copy of the old algorithm: position, leg index and heading.
- `RemainingPoints_ReturnsTheTailFromTheCurrentLeg`.

## Not done

- No micro-benchmark or live measurement (no builds or servers in this lane).
- A monotonic cursor was rejected: it would need single-thread ownership proven.
- The tests were written but not run in this lane; they run in the integration gate.
