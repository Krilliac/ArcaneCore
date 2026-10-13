using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Steady-state allocation assertions that tolerate cross-thread GC noise without hiding real growth.
/// Observed (2026-10-12, xunit running test classes in parallel): a single measured window occasionally read about one allocation quantum
/// (7-8 KB) while the code under test allocated nothing, and the jumps coincided with gen0 collections. The .NET 10 GC source does not explain
/// the mechanism (fix_allocation_context subtracts the unused part of the context, so the counter should stay exact), so the cause is recorded as
/// a correlation, not established. A window during which any collection ran (GC.CollectionCount of generation 0, 1 or 2 changed) is discarded
/// and retried, but only up to <see cref="DirtyWindowSlack"/> over the limit: growth large enough to trigger a collection in its own window
/// still fails. Every window that ran GC-free is asserted against the threshold, so an allocation that is intermittent or grows per call
/// fails instead of being averaged or minimised away. Each window runs inside a no-GC region when one can be entered (2026-10-13: CI runners
/// discarded 18 of 20 windows), so cross-thread collections no longer starve the probe of clean windows.
/// <para>
/// Second source, established by A/B (2026-10-12, CreatureMovement + GridTerrain test namespaces, fresh process per run, xunit parallel):
/// with tiered compilation on, 3 of 30 runs failed an allocation window (5264, 5480 and 8200 bytes; the code under test allocates nothing),
/// with DOTNET_TieredPGO=0 still 1 of 30, with DOTNET_TieredCompilation=0 0 of 30. So tier-0 to tier-1 promotion (call counting after its
/// 100 ms delay, background tier-1 compile, OSR of hot loops) landing inside a window is what reads as an allocation; which allocation
/// that is was not identified. <see cref="WarmUp"/> therefore spends enough calls and wall time before the first window that the promotion
/// has very likely finished (the approach of docs/integration/tick-allocation-20261004.md). It is not guaranteed: the 100 ms delay restarts
/// whenever other tests in the host compile new tier-0 code. A late promotion over-counts (a flake), it never hides an allocation.
/// </para>
/// </summary>
internal static class AllocationProbe
{
    private const int MaxAttempts = 40;

    /// <summary>Wall time the warm-up spans: the tiering delay (100 ms, restarted by new tier-0 activity) plus the background compile.</summary>
    private const int WarmUpMs = 600;

    /// <summary>Fewer calls than the tier-1 call-count threshold (30) never promote a method, however long they take.</summary>
    private const int MinWarmUpCalls = 60;

    /// <summary>
    /// Unmeasured: runs <paramref name="window"/> repeatedly, with pauses so the call-counting timer can fire and the background tier-1
    /// compile can install, until at least <see cref="MinWarmUpCalls"/> calls and <see cref="WarmUpMs"/> went by, then once more with the
    /// final code.
    /// </summary>
    private static void WarmUp(Action window)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int calls = 0;
        while (calls < MinWarmUpCalls || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < WarmUpMs)
        {
            window();
            if (++calls % 10 == 0) Thread.Sleep(25);
        }

        window();
    }

    /// <summary>How far over the limit a window that saw a collection may read before it fails instead of being retried.</summary>
    private const long DirtyWindowSlack = 16 * 1024;

    /// <summary>
    /// Runs <paramref name="window"/> until <paramref name="cleanWindows"/> GC-free windows have been measured and asserts each allocated at most
    /// <paramref name="maxBytes"/>. Fails if a clean window exceeds the limit, or if fewer than <paramref name="cleanWindows"/> clean windows
    /// were obtained within 40 attempts. The delegate must be created before the call and must not itself allocate.
    /// </summary>
    public static void AssertEveryCleanWindowAtMost(Action window, long maxBytes, string what, int cleanWindows = 5)
    {
        int clean = 0;
        int discarded = 0;
        WarmUp(window);
        for (int attempt = 0; attempt < MaxAttempts && clean < cleanWindows; attempt++)
        {
            // A no-GC region keeps collections (triggered by any thread) out of the window, so on a busy runner the window is GC-free instead
            // of being discarded. Other threads' allocations count against the budget; if they exhaust it a collection runs, the region ends
            // and the window is treated as dirty below. If the region cannot be entered the window simply runs without it.
            bool noGc = TryStartNoGcRegion();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            bool collected = g0 != GC.CollectionCount(0) || g1 != GC.CollectionCount(1) || g2 != GC.CollectionCount(2);
            if (noGc)
            {
                collected |= !EndNoGcRegion();
            }

            if (collected)
            {
                Assert.True(allocated <= maxBytes + DirtyWindowSlack,
                    $"{what}: window allocated {allocated} bytes with a collection in it (limit {maxBytes}, retried only up to +{DirtyWindowSlack})");
                discarded++;
                continue;
            }

            Assert.True(allocated <= maxBytes, $"{what}: GC-free window {clean + 1} allocated {allocated} bytes (limit {maxBytes})");
            clean++;
        }

        Assert.True(clean >= cleanWindows, $"{what}: only {clean} of {cleanWindows} GC-free windows in {MaxAttempts} attempts ({discarded} discarded because a collection ran)");
    }

    /// <summary>The no-GC budget: room for parallel test threads' allocations during one short window.</summary>
    private const long NoGcBudget = 64L * 1024 * 1024;

    private static bool TryStartNoGcRegion()
    {
        // Only one no-GC region exists per process. Parallel allocation tests used to give up at once, then every
        // window on a busy runner was discarded and the probe failed with "only 1 of 5 GC-free windows".
        long until = Environment.TickCount64 + 500;
        while (true)
        {
            if (System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion)
            {
                try
                {
                    if (GC.TryStartNoGCRegion(NoGcBudget))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // raced another thread entering the region
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false; // budget larger than this GC configuration allows
                }
            }

            if (Environment.TickCount64 >= until)
            {
                return false;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Leaves the region; false when it had already ended because the budget ran out and a collection ran.</summary>
    private static bool EndNoGcRegion()
    {
        if (System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion)
        {
            return false;
        }

        try
        {
            GC.EndNoGCRegion();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // the region was exited by a collection (budget exhausted or an induced GC)
        }
    }
}
