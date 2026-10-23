using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace ArcaneCore.TestSupport;

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
/// <para>
/// A window during which the runtime replaced one of its process-wide managed caches (<see cref="RuntimeCaches"/>) is discarded and retried
/// in the same way, up to twice the new table's size plus <see cref="DirtyWindowSlack"/> over the limit. Those caches grow on whichever
/// thread's lookup misses while they are full, so the thread under test can pay for other threads' casts. CI 2026-10-23 (run 39992634375):
/// HasQuestCandidate's GC-free window 3 allocated 98352 bytes, exactly the int[24582] the cast cache allocates when it grows from 2048 to
/// 4096 entries (one <c>is</c> cast allocates that much on its own thread once other threads have filled the cache; the scan's
/// <c>Map.FindUpdater&lt;T&gt;</c> casts through it with <c>is T</c>). This recurs for the whole run, not only while the cache first
/// fills: unloading a collectible load context flushes both caches (loaderallocator.cpp calls CastCache::FlushCurrentCache and
/// FlushVirtualFunctionPointerCaches), the flush swaps in a placeholder table, and the next lookup that misses, on any thread, allocates
/// the table again at its last size (98352 bytes for a full cast cache). The tests unload such contexts (ModuleRollbackTests,
/// ModuleHostTests, FeatureLifecycleTests, SpellScriptHotSwapTests). The probe compares the tables' identity, so a flush and its
/// re-allocation count as a replacement too.
/// </para>
/// </summary>
public static class AllocationProbe
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
        => AssertEveryCleanWindowAtMost(window, maxBytes, what, cleanWindows, RuntimeCaches.Readers);

    /// <summary>
    /// <see cref="AssertEveryCleanWindowAtMost(Action, long, string, int)"/> watching <paramref name="caches"/> instead of the runtime's
    /// caches: each reader returns its cache's current table, and a different table after a window means the cache was replaced in it.
    /// </summary>
    internal static void AssertEveryCleanWindowAtMost(Action window, long maxBytes, string what, int cleanWindows, IReadOnlyList<Func<object?>> caches)
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
            MeasureWindows(window, maxBytes, what, cleanWindows, caches, ref clean, ref discarded, ref refused, ref broken);
        }

        Assert.True(clean >= cleanWindows, $"{what}: only {clean} of {cleanWindows} GC-free windows in {MaxAttempts} attempts ({discarded} discarded because a collection ran or a runtime cache was replaced; no-GC region refused {refused}x, ended early {broken}x)");
    }

    private static readonly object Gate = new();

    /// <summary>
    /// A full blocking collection (collect, run finalizers, collect) that never lands inside a measured window: it waits for a probe in
    /// progress to finish. A collection another thread forces ends the probe's no-GC region, so a test that forces collections in a loop
    /// (waiting for a module's load context to be collected) took every clean window from a probe that ran meanwhile (CI 2026-10-18: failing
    /// ModuleRollbackTests forced collections for four minutes, and the escape-fan probe ended all 40 of its no-GC regions early). Such loops
    /// call this instead of <see cref="GC.Collect()"/>.
    /// </summary>
    public static void CollectBetweenWindows()
    {
        lock (Gate)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void MeasureWindows(Action window, long maxBytes, string what, int cleanWindows, IReadOnlyList<Func<object?>> caches,
        ref int clean, ref int discarded, ref int refused, ref int broken)
    {
        long budget = NoGcBudget;
        var tables = new object?[caches.Count];
        for (int attempt = 0; attempt < MaxAttempts && clean < cleanWindows; attempt++)
        {
            // A no-GC region keeps collections (triggered by any thread) out of the window, so on a busy runner the window is GC-free instead
            // of being discarded. Other threads' allocations count against the budget; if they exhaust it a collection runs, the region ends
            // and the window is treated as dirty below. If the region cannot be entered the window simply runs without it.
            bool noGc = TryStartNoGcRegion(budget);
            refused += noGc ? 0 : 1;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            for (int i = 0; i < tables.Length; i++) tables[i] = caches[i](); // outside the measured span: a reader may allocate
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            bool collected = g0 != GC.CollectionCount(0) || g1 != GC.CollectionCount(1) || g2 != GC.CollectionCount(2);
            long replacedBytes = 0;
            for (int i = 0; i < tables.Length; i++)
            {
                object? now = caches[i]();
                if (!ReferenceEquals(now, tables[i])) replacedBytes += RuntimeCaches.SizeOf(now);
            }
            if (noGc)
            {
                bool ended = EndNoGcRegion();
                broken += ended ? 0 : 1;
                if (!ended) budget = Math.Min(budget * 2, MaxNoGcBudget); // other threads out-allocated the budget during the window: ask for more room
                collected |= !ended;
            }

            if (collected || replacedBytes != 0)
            {
                // A runtime cache replaced in the window may have put its new table on this thread. Growth doubles a table, so a chain of
                // growths in one window sums to less than twice the last one; anything beyond that and the slack is the code's own.
                Assert.True(allocated <= maxBytes + DirtyWindowSlack + 2 * replacedBytes,
                    $"{what}: window allocated {allocated} bytes with {(collected ? "a collection" : "a runtime cache replaced")} in it (limit {maxBytes}, retried only up to +{DirtyWindowSlack}{(replacedBytes == 0 ? "" : $" + 2 x {replacedBytes} for the replaced runtime cache")})");
                discarded++;
                continue;
            }

            Assert.True(allocated <= maxBytes, $"{what}: GC-free window {clean + 1} allocated {allocated} bytes (limit {maxBytes})");
            clean++;
        }
    }

    /// <summary>
    /// The runtime's process-wide managed caches whose table is replaced by the thread whose lookup missed (when the table is full, or
    /// after a collectible load context's unload flushed it): the cast cache (<c>CastHelpers.s_table</c>, used by casts to interfaces,
    /// variant types and shared-generic type parameters) and the generic virtual method cache
    /// (<c>VirtualDispatchHelpers.s_virtualFunctionPointerCache</c>). Found by private reflection: a runtime that renames them leaves
    /// fewer <see cref="Readers"/>, the probe then fails such windows as it did before, and AllocationProbeTests fails on the missing cache.
    /// <para>
    /// The allowance for a window with a replaced table is twice the table plus <see cref="DirtyWindowSlack"/>. Measured on .NET 11
    /// (64-bit, <c>SizeOf(Readers[1]())</c> per entry): the cast cache tops out at 4096 entries, int[24582] = 98352 bytes, allowance
    /// 213088 bytes; the GVM cache tops out at 8192 entries of 48 bytes, 8193 with its header entry = 393288 bytes, allowance 802960
    /// bytes (both replaced in one window: 999664). Only a window in which a table was replaced gets it; every clean window is held to
    /// the exact limit.
    /// </para>
    /// </summary>
    internal static class RuntimeCaches
    {
        private static readonly (string Name, Func<object?> Reader)[] Found = Find();

        /// <summary>Each cache's current table.</summary>
        internal static readonly IReadOnlyList<Func<object?>> Readers = Array.ConvertAll(Found, cache => cache.Reader);

        /// <summary>The caches <see cref="Readers"/> watch.</summary>
        internal static readonly IReadOnlyList<string> Names = Array.ConvertAll(Found, cache => cache.Name);

        /// <summary>The heap size of a cache table (an array: header and elements), 0 for anything else.</summary>
        internal static long SizeOf(object? table)
            => table is Array array
                ? 3L * IntPtr.Size + (long)array.Length * RuntimeHelpers.SizeOf(array.GetType().GetElementType()!.TypeHandle)
                : 0;

        private static (string, Func<object?>)[] Find()
        {
            const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
            Assembly coreLib = typeof(object).Assembly;
            var found = new List<(string, Func<object?>)>();
            if (coreLib.GetType("System.Runtime.CompilerServices.CastHelpers")?.GetField("s_table", Static) is { } castTable)
            {
                found.Add(("CastHelpers.s_table", () => castTable.GetValue(null)));
            }

            if (coreLib.GetType("System.Runtime.CompilerServices.VirtualDispatchHelpers")?.GetField("s_virtualFunctionPointerCache", Static) is { } dispatch
                && dispatch.FieldType.GetField("_table", BindingFlags.Instance | BindingFlags.NonPublic) is { } dispatchTable)
            {
                found.Add(("VirtualDispatchHelpers.s_virtualFunctionPointerCache", () => dispatchTable.GetValue(dispatch.GetValue(null))));
            }

            return [.. found];
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
