using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class CounterRegistryTests
{
    private readonly CounterRegistry _registry = WatchdogTestSupport.NewRegistry();

    [Fact]
    public void GetOrAdd_ReturnsTheSameCounterForTheSameName_AndRejectsAKindMismatch()
    {
        Counter a = _registry.GetOrAdd("net.accepted");
        Assert.Same(a, _registry.GetOrAdd("net.accepted"));
        Assert.Same(a, _registry.Find("net.accepted"));
        Assert.Null(_registry.Find("net.other"));
        Assert.Throws<InvalidOperationException>(() => _registry.GetOrAdd("net.accepted", CounterKind.Gauge));
        Assert.Equal(1, _registry.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper.case")]
    [InlineData("has space")]
    [InlineData("dash-not-allowed")]
    public void GetOrAdd_RejectsBadNames(string name)
        => Assert.ThrowsAny<ArgumentException>(() => _registry.GetOrAdd(name));

    [Fact]
    public void GetOrAdd_RejectsNamesLongerThanTheLimit()
        => Assert.Throws<ArgumentException>(() => _registry.GetOrAdd(new string('a', CounterRegistry.MaxNameLength + 1)));

    [Fact]
    public void Increment_Add_Set_Decrement_BehaveAndDoNotAllocate()
    {
        Counter counter = _registry.GetOrAdd("c");
        Counter gauge = _registry.GetOrAdd("g", CounterKind.Gauge);
        counter.Increment();
        counter.Add(9);
        gauge.Set(5);
        gauge.Decrement();
        Assert.Equal(10, counter.Value);
        Assert.Equal(4, gauge.Value);
        Assert.Equal("c=10", counter.ToString());
        Assert.Equal(0, WatchdogTestSupport.AllocatedBy(counter.Increment, 1_000_000));
        Assert.Equal(0, WatchdogTestSupport.AllocatedBy(() => gauge.Set(3), 100_000));
    }

    [Fact]
    public void Counters_IsAStableArray_AndEnumeratingItDoesNotAllocate()
    {
        _registry.GetOrAdd("a");
        _registry.GetOrAdd("b");
        long bytes = WatchdogTestSupport.AllocatedBy(() =>
        {
            long sum = 0;
            foreach (Counter counter in _registry.Counters)
            {
                sum += counter.Value;
            }
        });
        Assert.Equal(0, bytes);
        Assert.Equal(["a", "b"], _registry.Counters.ToArray().Select(c => c.Name));
    }

    [Fact]
    public async Task ConcurrentIncrements_AreExact_AndSnapshotsAreMonotonicPerCounter_WhileRegistrationsHappen()
    {
        const int Threads = 8;
        const int PerThread = 200_000;
        Counter[] hot = [_registry.GetOrAdd("hot.a"), _registry.GetOrAdd("hot.b"), _registry.GetOrAdd("hot.c"), _registry.GetOrAdd("hot.d")];
        using var start = new ManualResetEventSlim(false);
        Task[] writers = [.. Enumerable.Range(0, Threads).Select(t => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < PerThread; i++)
            {
                hot[(i + t) & 3].Increment();
            }
        }))];

        // A reader takes snapshots and registers new counters while the writers run.
        var previous = new Dictionary<string, long>(StringComparer.Ordinal);
        int snapshots = 0;
        Task reader = Task.Run(() =>
        {
            start.Wait();
            int registered = 0;
            while (!writers.All(w => w.IsCompleted))
            {
                _registry.GetOrAdd("late." + registered++);
                CounterSnapshot snapshot = _registry.Snapshot(snapshots);
                snapshots++;
                Assert.True(_registry.Count >= snapshot.Count);
                foreach (CounterSample sample in snapshot.Samples)
                {
                    if (previous.TryGetValue(sample.Name, out long last))
                    {
                        Assert.True(sample.Value >= last, $"{sample.Name} went from {last} to {sample.Value}");
                    }

                    previous[sample.Name] = sample.Value;
                }
            }
        });

        start.Set();
        await Task.WhenAll(writers);
        await reader;

        Assert.Equal((long)Threads * PerThread, hot.Sum(c => c.Value));
        Assert.All(hot, c => Assert.Equal(Threads * PerThread / 4, c.Value));
        Assert.True(snapshots > 0);
        CounterSnapshot final = _registry.Snapshot(0);
        Assert.Equal(_registry.Count, final.Count);
        Assert.Equal(Threads * PerThread / 4, final["hot.a"]);
        Assert.Null(final["missing"]);
    }

    [Fact]
    public void Dump_RendersValuesAndDeltas_AndChangedOnlyLeavesIdleCountersOut()
    {
        Counter requests = _registry.GetOrAdd("requests");
        Counter players = _registry.GetOrAdd("players", CounterKind.Gauge);
        var dump = new CounterDump(new WatchdogOptions(), _registry, new RecordingLogger<CounterDump>());
        requests.Add(5);
        players.Set(3);
        Assert.Equal("requests=5 players=3", dump.Render(_registry, changedOnly: false));
        requests.Add(2);
        Assert.Equal("requests=7(+2) players=3", dump.Render(_registry, changedOnly: false));
        requests.Add(1);
        Assert.Equal("requests=8(+1)", dump.Render(_registry, changedOnly: true));
        Assert.Equal(string.Empty, dump.Render(_registry, changedOnly: true));
    }

    [Fact]
    public void Dump_LogsOnScheduleOnly()
    {
        var clock = new FakeClock();
        var log = new RecordingLogger<CounterDump>();
        var options = new WatchdogOptions();
        options.Counters.DumpIntervalSeconds = 60;
        _registry.GetOrAdd("x").Increment();
        var dump = new CounterDump(options, _registry, log);
        dump.Check(clock.NowMicros); // arms
        clock.AdvanceMs(59_000);
        dump.Check(clock.NowMicros);
        Assert.Empty(log.Lines);
        clock.AdvanceMs(1_000);
        dump.Check(clock.NowMicros);
        LogLine line = Assert.Single(log.Lines);
        Assert.Equal(WatchdogEvents.CounterDump.Id, line.Event.Id);
        Assert.Contains("x=1", line.Message);
        Assert.Equal(1, dump.Dumps);

        options.Counters.DumpIntervalSeconds = 0;
        clock.AdvanceMs(600_000);
        dump.Check(clock.NowMicros);
        Assert.Single(log.Lines);
    }
}
