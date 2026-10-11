using System.Collections.ObjectModel;
using System.Diagnostics;
using ArcaneCore.World.Ops.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Ops.OpsDiagnostics;

/// <summary>The sampling allocation profiler: aggregation, the row cap, payload parsing, the report, the session lifecycle and the real listener.</summary>
public sealed class AllocationProfilerTests(ITestOutputHelper output)
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void Add_SumsBytesAndEventsPerTypeAndHeap()
    {
        var aggregator = new AllocationAggregator();
        aggregator.Add("Foo", 0, 100);
        aggregator.Add("Foo", 0, 50);
        aggregator.Add("Foo", 1, 1000);
        aggregator.Add(null, 2, 7);
        AllocationSnapshot snapshot = aggregator.Snapshot();

        Assert.Equal(3, snapshot.DistinctTypes);
        Assert.Equal(4, snapshot.TotalEvents);
        Assert.Equal(new AllocationRow("Foo", AllocationHeap.Soh, 150, 2), Assert.Single(snapshot.Rows, r => r.Type == "Foo" && r.Heap == AllocationHeap.Soh));
        Assert.Equal(1000, Assert.Single(snapshot.Rows, r => r.Heap == AllocationHeap.Loh).Bytes);
        Assert.Equal(AllocationAggregator.UnknownType, Assert.Single(snapshot.Rows, r => r.Heap == AllocationHeap.Poh).Type);
    }

    [Fact]
    public void TheRowCap_SendsNewTypesToTheOtherBucket_ButKnownTypesKeepCounting()
    {
        var aggregator = new AllocationAggregator(maxTypes: 3);
        for (int i = 0; i < 5; i++)
        {
            aggregator.Add("T" + i, 0, 10);
        }

        aggregator.Add("T0", 0, 5); // known row: still counted exactly
        aggregator.Add("T9", 1, 70); // a new LOH row beyond the cap goes to the LOH bucket
        AllocationSnapshot snapshot = aggregator.Snapshot();

        Assert.Equal(3, snapshot.DistinctTypes);
        Assert.Equal(15, snapshot.Rows.Single(r => r.Type == "T0").Bytes);
        Assert.Equal(20, snapshot.OtherBytes[(int)AllocationHeap.Soh]);
        Assert.Equal(2, snapshot.OtherEvents[(int)AllocationHeap.Soh]);
        Assert.Equal(70, snapshot.OtherBytes[(int)AllocationHeap.Loh]);
        Assert.Equal(7, snapshot.TotalEvents);
        Assert.Equal(10 + 10 + 10 + 5 + 20 + 70, snapshot.Rows.Sum(r => r.Bytes) + snapshot.OtherBytes.Sum()); // nothing lost
    }

    [Fact]
    public void TheDefaultCapIs2000Types()
    {
        var aggregator = new AllocationAggregator();
        for (int i = 0; i < 2500; i++)
        {
            aggregator.Add("Type" + i, 0, 1);
        }

        AllocationSnapshot snapshot = aggregator.Snapshot();
        Assert.Equal(2000, snapshot.DistinctTypes);
        Assert.Equal(500, snapshot.OtherBytes[0]);
    }

    [Fact]
    public void AnUnknownHeapOrANegativeSize_IsCountedAsMalformed()
    {
        var aggregator = new AllocationAggregator();
        aggregator.Add("X", 3, 1);
        aggregator.Add("X", -1, 1);
        aggregator.Add("X", 0, -5);
        AllocationSnapshot snapshot = aggregator.Snapshot();
        Assert.Equal(3, snapshot.MalformedEvents);
        Assert.Equal(0, snapshot.TotalEvents);
        Assert.Empty(snapshot.Rows);
    }

    [Fact]
    public void ManyThreadsAddingAtOnce_LoseNothing()
    {
        var aggregator = new AllocationAggregator(maxTypes: 4);
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                aggregator.Add("T" + (i % 6), i % 3, 3); // 6 types x 3 heaps = 18 keys, 4 rows allowed
            }
        });

        AllocationSnapshot snapshot = aggregator.Snapshot();
        Assert.Equal(8 * 20_000, snapshot.TotalEvents);
        Assert.Equal(8 * 20_000 * 3L, snapshot.Rows.Sum(r => r.Bytes) + snapshot.OtherBytes.Sum());
        Assert.True(snapshot.DistinctTypes <= 4);
    }

    private static (ReadOnlyCollection<string> Names, ReadOnlyCollection<object?> Values) Payload(params (string, object?)[] fields)
        => (new ReadOnlyCollection<string>([.. fields.Select(f => f.Item1)]), new ReadOnlyCollection<object?>([.. fields.Select(f => f.Item2)]));

    [Fact]
    public void Record_ReadsTheV4PayloadByName()
    {
        var aggregator = new AllocationAggregator();
        var p = Payload(("AllocationAmount", 100_000u), ("AllocationKind", 1u), ("ClrInstanceID", (ushort)0), ("AllocationAmount64", 200_000UL), ("TypeId", 0L), ("TypeName", "System.Byte[]"));
        AllocationTickListener.Record(aggregator, p.Names, p.Values);
        AllocationRow row = Assert.Single(aggregator.Snapshot().Rows);
        Assert.Equal(new AllocationRow("System.Byte[]", AllocationHeap.Loh, 200_000, 1), row);
    }

    [Fact]
    public void Record_HandlesMissingFields()
    {
        var aggregator = new AllocationAggregator();
        // Only the 32-bit amount, no type name: filed under the unknown type.
        var a = Payload(("AllocationAmount", 4096u), ("AllocationKind", 0u));
        AllocationTickListener.Record(aggregator, a.Names, a.Values);
        // No kind: cannot be attributed to a heap.
        var b = Payload(("AllocationAmount64", 5UL), ("TypeName", "X"));
        AllocationTickListener.Record(aggregator, b.Names, b.Values);
        // No amount at all, and an amount of the wrong type.
        var c = Payload(("AllocationKind", 0u), ("TypeName", "X"));
        AllocationTickListener.Record(aggregator, c.Names, c.Values);
        var d = Payload(("AllocationAmount64", "many"), ("AllocationKind", 0u));
        AllocationTickListener.Record(aggregator, d.Names, d.Values);
        AllocationTickListener.Record(aggregator, null, null);

        AllocationSnapshot snapshot = aggregator.Snapshot();
        Assert.Equal(4, snapshot.MalformedEvents);
        AllocationRow row = Assert.Single(snapshot.Rows);
        Assert.Equal(new AllocationRow(AllocationAggregator.UnknownType, AllocationHeap.Soh, 4096, 1), row);
    }

    [Fact]
    public void Format_ListsTheTopTypesPerHeap_WithRatesSharesAndTheSamplingNote()
    {
        var aggregator = new AllocationAggregator();
        aggregator.Add("Big", 0, 30 * Mib);
        aggregator.Add("Mid", 0, 10 * Mib);
        aggregator.Add("Small", 0, 1 * Mib);
        aggregator.Add("Tiny", 0, 1 * Mib);
        aggregator.Add("System.Byte[]", 1, 8 * Mib);
        AllocationSnapshot snapshot = aggregator.Snapshot();

        string[] lines = [.. AllocationReportFormat.Format(snapshot, TimeSpan.FromSeconds(10), running: false, top: 2)];
        string all = string.Join("\n", lines);

        Assert.Contains("Allocation sample (stopped): 10.0 s, 5 events", lines[0]);
        Assert.Contains("one allocation-tick event per 100 KB", all);
        Assert.Contains("SOH: 42.0 MiB sampled, 4.20 MiB/s", all);
        Assert.Contains("  Big: 30.00 MiB, 3.00 MiB/s, 71.4% (1 events)", all);
        Assert.Contains("  Mid: 10.00 MiB, 1.00 MiB/s, 23.8% (1 events)", all);
        Assert.Contains("(2 more types): 2.00 MiB", all);
        Assert.DoesNotContain("  Small:", all);
        Assert.Contains("LOH: 8.0 MiB sampled, 0.80 MiB/s", all);
        Assert.Contains("  System.Byte[]: 8.00 MiB, 0.80 MiB/s, 100.0% (1 events)", all);
        Assert.Contains("POH: 0.0 MiB sampled, 0.00 MiB/s (nothing sampled)", all);
        Assert.True(Array.FindIndex(lines, l => l.StartsWith("SOH", StringComparison.Ordinal)) < Array.FindIndex(lines, l => l.StartsWith("LOH", StringComparison.Ordinal)));
        Assert.All(lines, l => Assert.InRange(l.Length, 1, 240));
    }

    [Fact]
    public void Format_ShowsTheOtherBucketAndCutsLongNames()
    {
        var aggregator = new AllocationAggregator(maxTypes: 1);
        aggregator.Add(new string('N', 300), 0, Mib);
        aggregator.Add("Other", 0, Mib);
        string[] lines = [.. AllocationReportFormat.Format(aggregator.Snapshot(), TimeSpan.FromSeconds(1), running: true, top: 5)];
        Assert.Contains("(running)", lines[0]);
        Assert.Contains(lines, l => l.Contains(AllocationAggregator.OtherLabel, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.InRange(l.Length, 1, 240));
    }

    private sealed class FakeListener : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Session_OnlyOneAtATime_StopKeepsTheResult_AndDetachesTheListener()
    {
        var listeners = new List<FakeListener>();
        AllocationAggregator? captured = null;
        using var profiler = new AllocationProfiler(a =>
        {
            captured = a;
            var l = new FakeListener();
            listeners.Add(l);
            return l;
        });

        Assert.False(profiler.TryStart(0, out string tooShort));
        Assert.Contains("1-600", tooShort);
        Assert.False(profiler.TryStart(601, out _));
        Assert.Null(profiler.Report());
        Assert.False(profiler.Stop());

        Assert.True(profiler.TryStart(60, out string started));
        Assert.Contains("started for 60 s", started);
        Assert.True(profiler.IsRunning);
        Assert.False(profiler.TryStart(10, out string second));
        Assert.Contains("already running", second);
        Assert.Single(listeners);

        captured!.Add("Foo", 0, 1234);
        AllocationReportData running = profiler.Report()!;
        Assert.True(running.Running);
        Assert.Equal(1234, running.Snapshot.Rows.Single().Bytes);

        Assert.True(profiler.Stop());
        Assert.True(listeners[0].Disposed);
        Assert.False(profiler.IsRunning);
        AllocationReportData finished = profiler.Report()!;
        Assert.False(finished.Running);
        Assert.Equal(1234, finished.Snapshot.Rows.Single().Bytes);

        Assert.True(profiler.TryStart(5, out _)); // a new session replaces the old result
        Assert.Null(profiler.Report()!.Snapshot.Rows.FirstOrDefault().Type);
    }

    [Fact]
    public async Task Session_StopsItselfAfterTheDuration()
    {
        var listener = new FakeListener();
        using var profiler = new AllocationProfiler(_ => listener);
        Assert.True(profiler.TryStart(1, out _));
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (profiler.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(profiler.IsRunning);
        Assert.True(listener.Disposed);
        Assert.False(profiler.Report()!.Running);
    }

    [Fact]
    public void ADisposedProfiler_DetachesTheRunningListener_AndRefusesNewSessions()
    {
        var listener = new FakeListener();
        var profiler = new AllocationProfiler(_ => listener);
        Assert.True(profiler.TryStart(30, out _));
        profiler.Dispose();
        Assert.True(listener.Disposed);
        Assert.False(profiler.TryStart(5, out _));
    }

    [Fact]
    public void AFactoryThatThrows_RefusesTheSession_AndLeavesNothingRunning()
    {
        using var profiler = new AllocationProfiler(_ => throw new InvalidOperationException("no event source"));
        Assert.False(profiler.TryStart(5, out string message));
        Assert.Contains("no event source", message);
        Assert.False(profiler.IsRunning);
    }

    private sealed class Marker
    {
        public long A = 1, B = 2, C = 3, D = 4;
    }

    // Allocate until the listener has seen events (the runtime delivers them in batches, a little late).
    private static async Task<bool> AllocateUntil(Func<bool> seen, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            for (int i = 0; i < 2000; i++)
            {
                _ = new Marker();
            }

            for (int i = 0; i < 4; i++)
            {
                _ = new byte[300_000]; // LOH
            }

            if (seen())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return seen();
    }

    [Fact]
    public async Task TheRealListener_SeesGcAllocationTicks_WithTypeNames_AndStopsWhenDisposed()
    {
        var aggregator = new AllocationAggregator();
        var listener = new AllocationTickListener(aggregator);
        bool seen;
        try
        {
            seen = await AllocateUntil(() => aggregator.Snapshot().Rows.Any(r => r.Heap == AllocationHeap.Loh && r.Type == "System.Byte[]") && aggregator.Snapshot().Rows.Any(r => r.Heap == AllocationHeap.Soh), TimeSpan.FromSeconds(30));
        }
        finally
        {
            listener.Dispose();
        }

        AllocationSnapshot snapshot = aggregator.Snapshot();
        foreach (AllocationRow row in snapshot.Rows.OrderByDescending(r => r.Bytes).Take(5))
        {
            output.WriteLine($"{row.Heap} {row.Type} {row.Bytes} bytes / {row.Events} events");
        }

        Assert.True(seen, "no GCAllocationTick events arrived from the runtime event source");
        Assert.Equal(0, snapshot.MalformedEvents);
        Assert.Contains(snapshot.Rows, r => r.Heap == AllocationHeap.Loh && r.Type == "System.Byte[]");
        Assert.DoesNotContain(snapshot.Rows, r => r.Type == AllocationAggregator.UnknownType);

        // Detached: further allocation (and the time for any queued event to arrive) adds nothing.
        await Task.Delay(1500);
        long after = aggregator.TotalEvents;
        for (int i = 0; i < 20_000; i++)
        {
            _ = new Marker();
            _ = new byte[200_000];
        }

        await Task.Delay(1500);
        Assert.Equal(after, aggregator.TotalEvents);
        listener.Dispose(); // idempotent
    }

    // Measures, does not gate: the cost of the listener on an allocation-heavy loop. The numbers are printed for docs/ops/diagnostics.md.
    [Fact]
    public async Task ListenerOverhead_IsMeasured()
    {
        static double Run()
        {
            long start = Stopwatch.GetTimestamp();
            var keep = new object[64];
            for (int i = 0; i < 40_000_000; i++)
            {
                keep[i & 63] = new Marker();
            }

            for (int i = 0; i < 4000; i++)
            {
                _ = new byte[200_000];
            }

            GC.KeepAlive(keep);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Run();
        double without = Enumerable.Range(0, 3).Min(_ => Run());
        var aggregator = new AllocationAggregator();
        double with;
        using (new AllocationTickListener(aggregator))
        {
            await Task.Delay(500);
            Run();
            with = Enumerable.Range(0, 3).Min(_ => Run());
        }

        output.WriteLine($"workload 40M x 48-byte objects + 4000 x 200 KB arrays: without listener {without:F0} ms, with listener {with:F0} ms ({(with / without - 1) * 100:F1}% change), {aggregator.TotalEvents} events");
        Assert.True(aggregator.TotalEvents > 0);
    }
}
