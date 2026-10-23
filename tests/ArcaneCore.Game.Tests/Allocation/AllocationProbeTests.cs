using ArcaneCore.TestSupport;
using Xunit;
using Xunit.Sdk;

namespace ArcaneCore.Game.Tests.Allocation;

/// <summary>
/// The probe retries a window during which a runtime cache was replaced (the cast cache growing on the thread under test, CI run
/// 39992634375) and still fails every allocation of the code under test. The runtime's own caches cannot be made to grow on demand
/// (they stop at their maximum size, which a long test process reaches early), so these windows replace a stand-in table instead.
/// </summary>
public sealed class AllocationProbeTests
{
    /// <summary>What the cast cache allocates growing from 2048 to 4096 entries on 64-bit: int[24582] (CI: "allocated 98352 bytes").</summary>
    private const int GrownCastTableLength = 24582;

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

        Assert.Contains($"allocated {16 + 8 + 4 * GrownCastTableLength} bytes", failure.Message);
    }

    [Fact]
    public void AWindowThatAllocatesBeyondTheReplacedTable_StillFails()
    {
        Action window = () =>
        {
            if (++_calls != 2) return;
            _table = new int[16];
            _sink = new byte[64 * 1024];
        };

        XunitException failure = Assert.ThrowsAny<XunitException>(
            () => AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "growth and more", 5, [() => _table]));

        Assert.Contains("retried only up to", failure.Message);
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
        Assert.Equal(16 + 8 + 4L * GrownCastTableLength, AllocationProbe.RuntimeCaches.SizeOf(new int[GrownCastTableLength]));
    }

    private void GrowTheTableInTheFirstMeasuredWindow()
    {
        if (++_calls == 2) _table = new int[GrownCastTableLength];
    }
}
