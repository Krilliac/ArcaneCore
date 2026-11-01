using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.World.Tests.Parallel;

/// <summary>
/// Data-parallel stages inside one map update (World:Parallel:MapHelpers, default 0; docs/research/map-local-stages-design-20261024.md,
/// section 4.3). The outcome of a stage must not depend on the helper count; with 0 helpers nothing is queued and a steady-state run allocates nothing.
/// </summary>
public sealed class MapLocalStagesTests
{
    private static readonly Func<int, long> Mix = static item =>
    {
        long x = item * 2654435761L + 12345;
        for (int i = 0; i < 200; i++)
        {
            x = (x ^ (x >> 7)) * 6364136223846793005L + 1442695040888963407L;
        }

        return x;
    };

    private static List<int> Items(int count)
    {
        var items = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(i);
        }

        return items;
    }

    private static (long[] Results, int[] Order) Execute(int count, int helpers)
    {
        var stage = new MapLocalStage<int, long>();
        var results = new long[count];
        var order = new List<int>(count);
        stage.Run(Items(count), Mix, (item, value) => { results[item] = value; order.Add(item); }, helpers);
        return (results, order.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void Outcome_IsIdenticalWithHelpersOnAndOff(int helpers)
    {
        (long[] off, int[] offOrder) = Execute(500, 0);
        (long[] on, int[] onOrder) = Execute(500, helpers);

        Assert.Equal(off, on);
        Assert.Equal(offOrder, onOrder);
        Assert.Equal(Enumerable.Range(0, 500), onOrder);
    }

    [Fact]
    public void RepeatedRuns_AreDeterministic()
    {
        (long[] first, _) = Execute(300, 3);
        for (int i = 0; i < 20; i++)
        {
            (long[] again, _) = Execute(300, 3);
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void ZeroHelpers_QueueNothing_AndComputeOnTheCaller()
    {
        var stage = new MapLocalStage<int, long>();
        int threadId = Environment.CurrentManagedThreadId;
        bool otherThread = false;
        stage.Run(Items(50), item =>
        {
            otherThread |= Environment.CurrentManagedThreadId != threadId || MapLocalStages.IsHelper;
            return item;
        }, static (_, _) => { }, helpers: 0);

        Assert.False(otherThread);
        Assert.Equal(0, stage.ItemsOnHelpers);
        Assert.Equal(1, stage.Runs);
    }

    [Fact]
    public void ZeroHelpers_SteadyStateRun_AllocatesNothing()
    {
        var stage = new MapLocalStage<int, long>();
        List<int> items = Items(64);
        Func<int, long> compute = static item => item * 3L;
        long sum = 0;
        Action<int, long> apply = (_, value) => sum += value;
        stage.Run(items, compute, apply, 0);
        stage.Run(items, compute, apply, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            stage.Run(items, compute, apply, 0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }

    [Fact]
    public void Helpers_RunWithoutOwnerOrGate_AndAreCountedWhenUsed()
    {
        var stage = new MapLocalStage<int, int>();
        bool ownerOnHelper = false;
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new CountdownEvent(1);
        // Hold the caller's items back until a helper has taken one, so the helper is provably used.
        int taken = 0;
        stage.Run(Items(4), item =>
        {
            if (Environment.CurrentManagedThreadId != callerThread)
            {
                ownerOnHelper |= TickOwner.CurrentMap is not null || !MapLocalStages.IsHelper;
                // Signal only for the first helper item: a helper loops on to further items and CountdownEvent.Signal throws at zero.
                if (Interlocked.Increment(ref taken) == 1)
                {
                    release.Signal();
                }
            }
            else if (item == 0)
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return item;
        }, static (_, _) => { }, helpers: 3);

        Assert.False(ownerOnHelper);
        Assert.True(stage.ItemsOnHelpers > 0);
        Assert.True(taken > 0);
        Assert.False(MapLocalStages.IsHelper);
    }

    [Fact]
    public void CallerRuns_CompletesWhenNoHelperGetsAnItem()
    {
        // One item: no helper job is queued at all, whatever the helper count.
        var stage = new MapLocalStage<int, long>();
        var seen = new List<long>();
        stage.Run(Items(1), Mix, (_, value) => seen.Add(value), helpers: 16);

        Assert.Single(seen);
        Assert.Equal(Mix(0), seen[0]);
        Assert.Equal(0, stage.ItemsOnHelpers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void FailingCompute_ThrowsLowestIndexFault_AndSkipsApply(int helpers)
    {
        var stage = new MapLocalStage<int, int>();
        int applied = 0;
        var ex = Assert.Throws<AggregateException>(() => stage.Run(Items(40), item =>
        {
            if (item == 7 || item == 30)
            {
                throw new InvalidOperationException("item " + item);
            }

            return item;
        }, (_, _) => applied++, helpers));

        Assert.Equal("item 7", ex.InnerException!.Message);
        Assert.Equal(0, applied);

        // The stage is reusable after a fault.
        var seen = new List<int>();
        stage.Run(Items(5), static item => item, (_, value) => seen.Add(value), helpers);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seen);
    }

    [Fact]
    public void EmptyItems_DoNothing()
    {
        var stage = new MapLocalStage<int, int>();
        stage.Run(new List<int>(), static item => item, static (_, _) => throw new InvalidOperationException(), helpers: 2);
        Assert.Equal(0, stage.Runs);
    }

    [Fact]
    public void MapHelpersOption_DefaultsToOff_AndClamps()
    {
        var options = new ParallelMapOptions();
        Assert.Equal(0, options.MapHelpers);
        Assert.Equal(0, options.EffectiveMapHelpers);
        options.MapHelpers = -5;
        Assert.Equal(0, options.EffectiveMapHelpers);
        options.MapHelpers = 500;
        Assert.Equal(64, options.EffectiveMapHelpers);
    }
}
