using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The fault runner of <see cref="SchemaStartupResilienceTests"/>: its width comes from
/// <c>ARCANECORE_TEST_FAULT_PARALLELISM</c> (default 4), it really overlaps that many faults, never more, runs every
/// fault even after one fails, and rethrows the failure. No database is involved.
/// </summary>
public sealed class SchemaStartupResilienceRunnerTests
{
    [Theory]
    [InlineData(null, SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("four", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("0", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("-2", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("16", 16)]
    public void ReadFaultParallelism_TakesAPositiveInteger_OrTheDefault(string? value, int expected)
    {
        Assert.Equal(expected, SchemaStartupResilienceTests.ReadFaultParallelism(value));
    }

    [Fact]
    public void FaultParallelism_IsAtLeastOne()
    {
        Assert.True(SchemaStartupResilienceTests.FaultParallelism >= 1);
        Assert.Equal(4, SchemaStartupResilienceTests.DefaultFaultParallelism);
    }

    [Fact]
    public async Task RunFaultsAsync_OverlapsExactlyTheConfiguredWidth_RunsEveryFault_AndRethrowsTheFirstFailure()
    {
        const int parallelism = 3;
        const int count = 10;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int running = 0;
        int peak = 0;
        int finished = 0;

        Task run = SchemaStartupResilienceTests.RunFaultsAsync(Enumerable.Range(1, count).ToList(), parallelism, async fault =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            do
            {
                seen = Volatile.Read(ref peak);
            }
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);

            try
            {
                // The first wave opens the gate only once the whole width is inside: a serial runner never gets here.
                if (Interlocked.Increment(ref started) == parallelism)
                {
                    gate.TrySetResult();
                }

                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Yield();
                if (fault == 5)
                {
                    throw new InvalidOperationException("fault 5 failed");
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref finished);
            }
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("fault 5 failed", ex.Message);
        Assert.Equal(count, finished);
        Assert.Equal(parallelism, peak);
    }

    [Fact]
    public async Task RunFaultsAsync_RefusesAWidthBelowOne()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SchemaStartupResilienceTests.RunFaultsAsync(new[] { 1 }, 0, _ => Task.CompletedTask));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(240)]
    public void FaultShard_GivesEveryFaultToExactlyOneShard(int count)
    {
        int[] faults = Enumerable.Range(0, count).ToArray();
        const int shards = SchemaStartupResilienceTestBase.WorldFaultShards;
        var seen = new List<int>();
        for (int shard = 0; shard < shards; shard++)
        {
            List<int> mine = SchemaStartupResilienceTestBase.FaultShard(faults, shard, shards);
            Assert.True(mine.Count >= count / shards && mine.Count <= (count + shards - 1) / shards, $"shard {shard} of {count} faults has {mine.Count}");
            seen.AddRange(mine);
        }

        Assert.Equal(faults, seen.Order().ToArray());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 4)]
    [InlineData(4, 4)]
    public void FaultShard_RefusesAShardOutsideTheRange(int shard, int shards)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaStartupResilienceTestBase.FaultShard(new[] { 1, 2, 3 }, shard, shards));
    }

    [Fact]
    public void UpgradeSlices_TogetherCoverEveryUpgradeStepOnce_AndSplitEachProviderEvenly()
    {
        List<string> expected = SchemaStartupResilienceTestBase.UpgradeSteps().Select(Key).Order().ToList();
        List<Type> classes = SliceClasses("SchemaStartupResilienceUpgradeSlice");
        Assert.Equal(SchemaStartupResilienceTestBase.UpgradeSliceCount, classes.Count);

        var seen = new List<string>();
        var perProvider = new Dictionary<object, List<int>>();
        foreach (Type type in classes)
        {
            List<object[]> rows = RowsOf(type);
            Assert.NotEmpty(rows);
            seen.AddRange(rows.Select(Key));
            foreach (IGrouping<object, object[]> group in rows.GroupBy(r => r[0]))
            {
                if (!perProvider.TryGetValue(group.Key, out List<int>? counts))
                {
                    perProvider[group.Key] = counts = new List<int>();
                }

                counts.Add(group.Count());
            }
        }

        Assert.Equal(expected, seen.Order().ToList());
        foreach ((object provider, List<int> counts) in perProvider)
        {
            Assert.Equal(classes.Count, counts.Count);
            Assert.True(counts.Max() - counts.Min() <= 1, $"{provider}: rows per class {string.Join(",", counts)}");
        }
    }

    [Fact]
    public void FreshCreateShares_TogetherCoverEveryFreshCreateCaseOnce_AndNoneIsEmpty()
    {
        List<string> expected = SchemaStartupResilienceTestBase.FreshCreateCases().Select(Key).Order().ToList();
        List<Type> classes = SliceClasses("SchemaStartupResilienceFreshCreateShare");
        classes.Add(typeof(SchemaStartupResilienceTests));
        Assert.Equal(SchemaStartupResilienceTestBase.FreshCreateClassCount, classes.Count);

        var seen = new List<string>();
        foreach (Type type in classes)
        {
            List<object[]> rows = RowsOf(type);
            Assert.NotEmpty(rows);
            seen.AddRange(rows.Select(Key));
        }

        Assert.Equal(expected, seen.Order().ToList());
        Assert.Equal(expected.Count, expected.Distinct().Count());
        // The world component is the cost of the theory: every provider's world is cut into its shards, each shard a distinct case.
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            Assert.Equal(
                Enumerable.Range(0, SchemaStartupResilienceTestBase.WorldFaultShards),
                SchemaStartupResilienceTestBase.FreshCreateCases().Where(r => Equals(r[0], provider[0]) && (string)r[1] == "world").Select(r => (int)r[2]).Order());
            Assert.Equal(
                Enumerable.Range(0, SchemaStartupResilienceTestBase.CharactersFaultShards),
                SchemaStartupResilienceTestBase.FreshCreateCases().Where(r => Equals(r[0], provider[0]) && (string)r[1] == "characters").Select(r => (int)r[2]).Order());
        }
    }

    [Fact]
    public async Task MeasureOnceAsync_SharesAValue_ButForgetsAFailedMeasurement()
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<int>>>();
        int calls = 0;
        Task<int> Measure(string key) => ++calls == 1 ? throw new InvalidOperationException("transient") : Task.FromResult(7);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaStartupResilienceTestBase.MeasureOnceAsync(cache, "k", Measure));
        Assert.Equal(7, await SchemaStartupResilienceTestBase.MeasureOnceAsync(cache, "k", Measure));
        Assert.Equal(7, await SchemaStartupResilienceTestBase.MeasureOnceAsync(cache, "k", Measure));
        Assert.Equal(2, calls);
    }

    private static string Key(object[] row) => string.Join("|", row);

    private static List<Type> SliceClasses(string prefix) => typeof(SchemaStartupResilienceTestBase).Assembly.GetTypes()
        .Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal) && t.Name.EndsWith("Tests", StringComparison.Ordinal) && t.IsSubclassOf(typeof(SchemaStartupResilienceTestBase)))
        .ToList();

    private static List<object[]> RowsOf(Type type)
    {
        System.Reflection.MethodInfo rows = type.GetMethod("Rows", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException(type.Name + " has no Rows");
        return ((IEnumerable<object[]>)rows.Invoke(null, null)!).ToList();
    }
}
