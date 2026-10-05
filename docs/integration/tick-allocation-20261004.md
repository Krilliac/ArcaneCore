# TickStats allocation verification — 2026-10-04

`TickStats.Record` is a steady-state world-thread hot path. Its implementation
uses only `Stopwatch.GetTimestamp`, a monitor lock, fixed arrays, and scalar
fields; it has no managed allocation site. The previous test warmed one call
before measuring 10,000 calls, but the Release full-suite run observed 2,016
bytes during the measured region. That isolated failure appeared after earlier
full runs passed and is consistent with tiered JIT promotion/runtime preparation
occurring inside the measurement.

The allocation test now warms the same `Record` path for 100,000 calls before
capturing `GC.GetAllocatedBytesForCurrentThread()`, then checks three independent
10,000-call windows. All three exact-zero assertions are evaluated after the
windows, keeping assertion machinery outside the measurement boundary. This
preserves the product invariant while moving the measurement past runtime warmup.

The coordinator's independent harness linked the exact production
`Maps/TickStats.cs` implementation and measured three windows in fresh
processes. With one warmup call, the first window reproduced 128 bytes and the
next two were zero in all three normal-runtime repetitions. With 100,000 warmup
calls, all windows were zero in all three repetitions. With
`DOTNET_TieredCompilation=0`, both one-call and 100,000-call warmups produced
zero in all windows. This supports runtime warmup/tiered compilation as the
cause of the reproducible 128-byte signal; it does not identify the earlier
full-suite 2,016-byte observation exactly.

The coordinator built the Release solution with zero warnings/errors and ran
the focused Game group successfully (14 cases, including this test). The exact
allocation test then passed in three additional fresh normal-runtime processes
and one process with tiered compilation disabled. Each test asserts zero for
all three measured windows. The final integrated item/pet suite also passes:
14,338 cases with six existing fixture skips and zero failures.

Coordinator verification command (Release, focused):

```powershell
dotnet test .\tests\ArcaneCore.Game.Tests\ArcaneCore.Game.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ArcaneCore.Game.Tests.Ops.TickStatsTests.Record_DoesNotAllocate'
```

Full-suite verification remains coordinator-owned.
