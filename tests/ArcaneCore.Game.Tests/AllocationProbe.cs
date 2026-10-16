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
/// discarded 18 of 20 windows), so cross-thread collections no longer starve the probe of clean windows. One unmeasured call first keeps one-off runtime work (JIT of the delegate) out of it.
/// </summary>
internal static class AllocationProbe
{
    private const int MaxAttempts = 40;

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
        int refused = 0;
        int broken = 0;
        window(); // unmeasured: first-call runtime work stays out of the windows
        // One probe at a time per process: GC.TryStartNoGCRegion itself runs a collection to reserve its budget, and a probe that
        // cannot enter the region (another probe holds the process-wide one) runs unguarded. Concurrent probes therefore dirtied each
        // other's windows until a probe saw too few clean ones.
        lock (Gate)
        {
            MeasureWindows(window, maxBytes, what, cleanWindows, ref clean, ref discarded, ref refused, ref broken);
        }

        Assert.True(clean >= cleanWindows, $"{what}: only {clean} of {cleanWindows} GC-free windows in {MaxAttempts} attempts ({discarded} discarded because a collection ran; no-GC region refused {refused}x, ended early {broken}x)");
    }

    private static readonly object Gate = new();

    private static void MeasureWindows(Action window, long maxBytes, string what, int cleanWindows, ref int clean, ref int discarded, ref int refused, ref int broken)
    {
        long budget = NoGcBudget;
        for (int attempt = 0; attempt < MaxAttempts && clean < cleanWindows; attempt++)
        {
            // A no-GC region keeps collections (triggered by any thread) out of the window, so on a busy runner the window is GC-free instead
            // of being discarded. Other threads' allocations count against the budget; if they exhaust it a collection runs, the region ends
            // and the window is treated as dirty below. If the region cannot be entered the window simply runs without it.
            bool noGc = TryStartNoGcRegion(budget);
            refused += noGc ? 0 : 1;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            bool collected = g0 != GC.CollectionCount(0) || g1 != GC.CollectionCount(1) || g2 != GC.CollectionCount(2);
            if (noGc)
            {
                bool ended = EndNoGcRegion();
                broken += ended ? 0 : 1;
                if (!ended) budget = Math.Min(budget * 2, MaxNoGcBudget); // other threads out-allocated the budget during the window: ask for more room
                collected |= !ended;
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
    }

    /// <summary>The no-GC budget: room for parallel test threads' allocations during one short window.</summary>
    private const long NoGcBudget = 64L * 1024 * 1024;

    private const long MaxNoGcBudget = 256L * 1024 * 1024;

    private static bool TryStartNoGcRegion(long budget)
    {
        if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
        {
            return false; // another test's probe holds the process-wide region
        }

        try
        {
            return GC.TryStartNoGCRegion(budget);
        }
        catch (InvalidOperationException)
        {
            return false; // raced another thread entering the region
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // budget larger than this GC configuration allows
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
