using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Steady-state allocation assertions that tolerate cross-thread GC noise without hiding real growth.
/// GC.GetAllocatedBytesForCurrentThread jumps by about one allocation quantum (7-8 KB) when a gen0 GC triggered by another thread (xunit runs
/// test classes in parallel) lands inside a measured window, even though the measuring thread allocated nothing. A window during which any
/// collection ran (GC.CollectionCount of generation 0, 1 or 2 changed) is therefore discarded and retried; every window that ran GC-free is
/// asserted against the threshold, so an allocation that is intermittent or grows per call fails instead of being averaged or minimised away.
/// </summary>
internal static class AllocationProbe
{
    private const int MaxAttempts = 20;

    /// <summary>
    /// Runs <paramref name="window"/> until <paramref name="cleanWindows"/> GC-free windows have been measured and asserts each allocated at most
    /// <paramref name="maxBytes"/>. Fails if a clean window exceeds the limit, or if fewer than <paramref name="cleanWindows"/> clean windows
    /// were obtained within 20 attempts. The delegate must be created before the call and must not itself allocate.
    /// </summary>
    public static void AssertEveryCleanWindowAtMost(Action window, long maxBytes, string what, int cleanWindows = 5)
    {
        int clean = 0;
        int discarded = 0;
        for (int attempt = 0; attempt < MaxAttempts && clean < cleanWindows; attempt++)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (g0 != GC.CollectionCount(0) || g1 != GC.CollectionCount(1) || g2 != GC.CollectionCount(2))
            {
                discarded++;
                continue;
            }

            Assert.True(allocated <= maxBytes, $"{what}: GC-free window {clean + 1} allocated {allocated} bytes (limit {maxBytes})");
            clean++;
        }

        Assert.True(clean >= cleanWindows, $"{what}: only {clean} of {cleanWindows} GC-free windows in {MaxAttempts} attempts ({discarded} discarded because a collection ran)");
    }
}
