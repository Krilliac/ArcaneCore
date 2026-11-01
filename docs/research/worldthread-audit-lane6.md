# World-thread audit lane 6: visibility per pair

Finding 2.2 of the verified world-thread audit: pairwise visibility is evaluated twice per tick when both players moved.

## Result

CONFIRMED and fixed in place.

- Cause: `Map.UpdateVisibility(player)` evaluates `(player, other)` and, for a player `other`, `(other, player)`. When both
  moved in the same tick, the second player's pass repeats both ordered pairs.
- Fix (`Map.Visibility.cs`, `Map.Update.cs`): during the Output stage's visibility passes (players that moved, then moved
  objects) each ordered `(viewer, target)` pair, keyed by the two `MapSequence` values, is recorded in a map-owned
  `HashSet`. A repeat is skipped. The record is cleared when the passes end and in the stage's `finally`.
- Never deduplicated: evaluations outside the stage (joins, `RefreshVisibility`, object refreshes) and detection-mode
  evaluations (`UpdateVisibilityWithDetection`), whose answer differs from a movement pass.
- Per ordered pair only, never per unordered pair, because stealth, ghost and GM rules are asymmetric.

## Why a repeat is safe

Within one Output stage nothing moves. Every `IVisibilityRule` implementation was read:

| Rule | Side effects / randomness |
|---|---|
| `StealthVisibilityRule` | none; `StealthDetection` is "roll-free", no RNG; the detect flag is false in movement passes |
| `GhostVisibilityRule` | none, pure function of death state, raid membership and corpse distance |
| `GmInvisibilityRule` | none |
| `ShadowfangKeepInstance.ArugalEventVisibility` | none; reads instance flags |

No rule draws random numbers, so RNG consumption is unchanged. The first evaluation leaves the visible list converged
(visible exactly when in range and allowed; the hysteresis range for an already-visible target only widens), so a second
evaluation of the same pair makes no change. Create and out-of-range order per viewer is unchanged: the first evaluation
of each pair stays where it was, only the repeat is dropped. The `_visibilityScratchInUse` and `_resolvedVisibleInUse`
guards are untouched.

## Counters

`Map.VisibilityPairEvaluations` (every `UpdateVisibilityOf` call) and `Map.VisibilityPairDuplicatesSkipped` (repeats
skipped). Both are map-thread internal counters declared in `Map.Visibility.cs` (no change to `Map.cs`). They are not yet
shown in `.debug maps`; the 50-bot "about half" figure was not measured, since no build or run happened in this lane.

## Tests

`tests/ArcaneCore.Game.Tests/GridTerrain/VisibilityPairDedupeTests.cs`:

- two moving players in range: exactly two evaluations performed, at least two skipped;
- leaving and re-entering range with both moving: each side gets exactly one create (`VisibilityVersion` +1);
- an asymmetric rule (A blind to B): A does not see B, B still sees A;
- evaluations outside the Output stage are never skipped.

The tests were not run in this lane (integration gate runs them). They fail before the change because the counters do
not exist and the pair is evaluated four times.
