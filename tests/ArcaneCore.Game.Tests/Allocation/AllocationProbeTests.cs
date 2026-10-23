using ArcaneCore.TestSupport;
using Xunit;
using Xunit.Sdk;

namespace ArcaneCore.Game.Tests.Allocation;

/// <summary>
/// The probe retries a window during which a runtime cache was replaced (the cast cache growing on the thread under test, CI run
/// 39992634375, or re-allocated after a collectible load context's unload flushed it) and still fails every allocation of the code under
/// test. A test cannot make the runtime replace its caches on the thread under test (another thread may re-allocate a flushed table
/// first), so these windows replace a stand-in table instead.
/// </summary>
public sealed class AllocationProbeTests
{
    /// <summary>What the cast cache allocates growing from 2048 to 4096 entries on 64-bit: int[24582] (CI: "allocated 98352 bytes").</summary>
    private const int GrownCastTableLength = 24582;

    /// <summary>An array's header: object header, method table and length.</summary>
    private static readonly long ArrayHeader = 3L * IntPtr.Size;

    /// <summary>The stand-in for the grown cast cache table (98352 bytes on 64-bit).</summary>
    private static readonly long GrownTableBytes = ArrayHeader + 4L * GrownCastTableLength;

    /// <summary>What a window with that table replaced may allocate: the slack (16 KB) plus twice the table.</summary>
    private static readonly long Allowance = 16 * 1024 + 2 * GrownTableBytes;

    /// <summary>A second watched cache that is never replaced: its table must not widen the allowance.</summary>
    private readonly object _untouched = new int[GrownCastTableLength];

    private object? _table = new int[8];
    private object? _sink;
    private int _calls;

    [Fact]
    public void AWindowWhoseOnlyAllocationIsAReplacedRuntimeCache_IsRetried()
    {
        Action window = GrowTheTableInTheFirstMeasuredWindow;

        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "cache growth", 5, [() => _table]);

        Assert.True(_calls >= 7, $"{_calls} calls: one unmeasured, the window that grew the table, then five clean ones");
    }

    [Fact]
    public void TheSameWindow_WithoutTheCacheWatched_Fails()
    {
        // The probe before the fix: nothing tells it the bytes were the runtime's table, so the clean window fails as in CI.
        Action window = GrowTheTableInTheFirstMeasuredWindow;

        XunitException failure = Assert.ThrowsAny<XunitException>(
            () => AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "cache growth", 5, []));

        Assert.Contains($"allocated {GrownTableBytes} bytes", failure.Message);
    }

    [Fact]
    public void AWindowWithTheTableReplaced_AllocatingJustOverTheAllowance_Fails()
    {
        Action window = GrowTheTableAndAllocate(Allowance + 8);

        XunitException failure = Assert.ThrowsAny<XunitException>(
            () => AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "growth and more", 5, [() => _table, () => _untouched]));

        // Only the replaced-cache path names the table's allowance; a window with just a collection in it does not.
        Assert.Contains($"allocated {Allowance + 8} bytes", failure.Message);
        Assert.Contains($"retried only up to +16384 + 2 x {GrownTableBytes} for the replaced runtime cache", failure.Message);
    }

    [Fact]
    public void AWindowWithTheTableReplaced_AllocatingJustUnderTheAllowance_IsRetried()
    {
        Action window = GrowTheTableAndAllocate(Allowance - 8);

        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "growth and a little more", 5, [() => _table, () => _untouched]);

        Assert.True(_calls >= 7, $"{_calls} calls: one unmeasured, the window that grew the table, then five clean ones");
    }

    [Fact]
    public void AnAllocationInEveryWindow_StillFails_WithTheRuntimeCachesWatched()
    {
        Action window = () => _sink = new object();

        XunitException failure = Assert.ThrowsAny<XunitException>(
            () => AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "one object per window"));

        Assert.Contains("allocated", failure.Message);
    }

    [Fact]
    public void TheProbeWatchesTheRuntimesCastAndVirtualDispatchCaches()
    {
        // A runtime that renames them would silently send the probe back to failing on their growth: fail here instead.
        Assert.Equal(
            ["CastHelpers.s_table", "VirtualDispatchHelpers.s_virtualFunctionPointerCache"],
            AllocationProbe.RuntimeCaches.Names);
        Assert.IsType<int[]>(AllocationProbe.RuntimeCaches.Readers[0]());
        Assert.IsAssignableFrom<Array>(AllocationProbe.RuntimeCaches.Readers[1]());
        Assert.Equal(GrownTableBytes, AllocationProbe.RuntimeCaches.SizeOf(new int[GrownCastTableLength]));
    }

    private void GrowTheTableInTheFirstMeasuredWindow()
    {
        if (++_calls == 2) _table = new int[GrownCastTableLength];
    }

    /// <summary>
    /// A window that, in its first measured call, replaces the stand-in table and allocates <paramref name="total"/> bytes in all: the
    /// table and one byte array making up the rest (a multiple of 8 bytes, so no alignment padding is added).
    /// </summary>
    private Action GrowTheTableAndAllocate(long total)
    {
        int rest = checked((int)(total - GrownTableBytes - ArrayHeader));
        Assert.Equal(0, rest % 8);
        return () =>
        {
            if (++_calls != 2) return;
            _table = new int[GrownCastTableLength];
            _sink = new byte[rest];
        };
    }
}
