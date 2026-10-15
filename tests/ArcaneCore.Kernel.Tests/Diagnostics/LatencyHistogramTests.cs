using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The shared latency primitive: accuracy against exact nearest-rank percentiles, windows, the text form, bounds and threads.</summary>
public sealed class LatencyHistogramTests
{
    /// <summary>A clock that counts microseconds and moves only when told.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _micros;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => Volatile.Read(ref _micros);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _micros, by.Ticks / 10);
    }

    private static long Exact(long[] sorted, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile / 100d * sorted.Length - 1e-9);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    private static void AssertWithinBucketError(long[] values, LatencySnapshot snap)
    {
        long[] sorted = [.. values.Order()];
        (double Percentile, long Reported)[] checks =
        [
            (50, snap.P50), (75, snap.P75), (90, snap.P90), (95, snap.P95), (99, snap.P99), (99.9, snap.P999),
        ];
        foreach ((double percentile, long reported) in checks)
        {
            long exact = Exact(sorted, percentile);
            // Never below the exact nearest-rank value; at most one sub-bucket (1/32) above it; exact below 32.
            Assert.True(reported >= exact, $"p{percentile}: reported {reported} < exact {exact}");
            double allowed = exact < 32 ? exact : exact * (1 + LatencyHistogram.RelativeError);
            Assert.True(reported <= allowed, $"p{percentile}: reported {reported} > exact {exact} * (1 + 1/32)");
        }

        Assert.Equal(values.Length, snap.Count);
        Assert.Equal(sorted[0], snap.Min);
        Assert.Equal(sorted[^1], snap.Max);
        double mean = values.Select(v => (double)v).Average();
        Assert.Equal(mean, snap.Mean, 1e-6 * Math.Max(1, mean));
        double variance = values.Select(v => ((double)v - mean) * ((double)v - mean)).Average();
        double sd = Math.Sqrt(variance);
        Assert.InRange(snap.StdDev, (sd * (1 - 1e-6)) - 1e-3, (sd * (1 + 1e-6)) + 1e-3);
    }

    [Fact]
    public void Uniform_PercentilesMatchExactNearestRankWithinTheBucketError()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        long[] values = [.. Enumerable.Range(1, 10_000).Select(i => (long)i * 7)];
        foreach (long v in values) histogram.Record(v);

        AssertWithinBucketError(values, histogram.Snapshot());
    }

    [Fact]
    public void LogNormal_PercentilesMatchExactNearestRankWithinTheBucketError()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        var random = new Random(20261015);
        long[] values = new long[50_000];
        for (int i = 0; i < values.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            values[i] = (long)Math.Exp(Math.Log(4_000) + (0.8 * normal)); // median 4 ms in microseconds, a long right tail
            histogram.Record(values[i]);
        }

        AssertWithinBucketError(values, histogram.Snapshot());
    }

    [Fact]
    public void Bimodal_WithOutliers_TheTailPercentilesSeeTheOutliers()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        var values = new List<long>();
        for (int i = 0; i < 9_900; i++) values.Add(2_000 + (i % 50));
        for (int i = 0; i < 90; i++) values.Add(40_000 + (i * 10));
        for (int i = 0; i < 10; i++) values.Add(900_000 + (i * 1_000));
        foreach (long v in values) histogram.Record(v);

        LatencySnapshot snap = histogram.Snapshot();
        AssertWithinBucketError([.. values], snap);
        Assert.InRange(snap.P99, 2_000, 2_150);
        Assert.InRange(snap.P999, 40_000, 42_000);
        Assert.Equal(909_000, snap.Max);
    }

    [Fact]
    public void ValuesBelowThirtyTwo_AreExact()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        long[] values = [.. Enumerable.Range(0, 32).SelectMany(v => Enumerable.Repeat((long)v, 5))];
        foreach (long v in values) histogram.Record(v);

        LatencySnapshot snap = histogram.Snapshot();
        long[] sorted = [.. values.Order()];
        Assert.Equal(Exact(sorted, 50), snap.P50);
        Assert.Equal(Exact(sorted, 90), snap.P90);
        Assert.Equal(Exact(sorted, 99), snap.P99);
        Assert.Equal(31, snap.Max);
        Assert.Equal(0, snap.Min);
    }

    [Fact]
    public void ConstantSamples_HaveZeroSpreadAndTheExactValue()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        for (int i = 0; i < 1_000; i++) histogram.Record(123_457);

        LatencySnapshot snap = histogram.Snapshot();
        Assert.Equal(123_457, snap.P50);
        Assert.Equal(123_457, snap.P999);
        Assert.Equal(123_457, snap.Min);
        Assert.Equal(0, snap.StdDev, 1);
        Assert.Equal(123_457, snap.Mean, 1e-6);
    }

    [Fact]
    public void Empty_IsEmptyAndFormatsAsNoSamples()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        LatencySnapshot snap = histogram.Snapshot();
        Assert.True(snap.IsEmpty);
        Assert.Equal(0, snap.P99);
        Assert.Equal("no samples", snap.Format());
        Assert.True(histogram.Snapshot(TimeSpan.FromMinutes(1)).IsEmpty);
    }

    [Fact]
    public void NegativeAndHugeValues_AreClampedNotRejected()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        histogram.Record(-5);
        histogram.Record(long.MaxValue);

        LatencySnapshot snap = histogram.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal(0, snap.Min);
        Assert.Equal(LatencyHistogram.MaxValue, snap.Max); // clamped to the range, the same value the buckets and the sums use
        Assert.Equal(LatencyHistogram.MaxValue / 2d, snap.Mean, 1);
    }

    [Fact]
    public void Windows_SeparateTheLastMinuteTheLastQuarterHourAndTheTotals()
    {
        var clock = new ManualClock();
        var histogram = new LatencyHistogram(clock);
        clock.Advance(TimeSpan.FromHours(1));
        for (int i = 0; i < 10; i++) histogram.Record(1_000); // an old, fast period
        clock.Advance(TimeSpan.FromMinutes(5));
        for (int i = 0; i < 10; i++) histogram.Record(50_000); // a recent slow period

        LatencyWindows windows = histogram.Windows();
        Assert.Equal(10, windows.Last1Min.Count);
        Assert.Equal(50_000, windows.Last1Min.P50);
        Assert.Equal(20, windows.Last15Min.Count);
        Assert.Equal(1_000, windows.Last15Min.Min);
        Assert.Equal(20, windows.Total.Count);

        clock.Advance(TimeSpan.FromMinutes(16));
        windows = histogram.Windows();
        Assert.True(windows.Last1Min.IsEmpty);
        Assert.True(windows.Last15Min.IsEmpty);
        Assert.Equal(20, windows.Total.Count);
        Assert.Equal(50_000, windows.Total.Max);
    }

    [Fact]
    public void RingSlices_AreReusedAfterTheSpanAndForgetTheirOldSamples()
    {
        var clock = new ManualClock();
        var histogram = new LatencyHistogram(clock, TimeSpan.FromSeconds(10), slices: 6);
        clock.Advance(TimeSpan.FromMinutes(10));
        histogram.Record(100);
        Assert.Equal(TimeSpan.FromSeconds(60), histogram.Span);

        // Exactly one span later the same ring slot is reused: the first sample must be gone from the window, still in the totals.
        clock.Advance(TimeSpan.FromSeconds(60));
        histogram.Record(900);
        LatencySnapshot window = histogram.Snapshot(histogram.Span);
        Assert.Equal(1, window.Count);
        Assert.Equal(900, window.Max);
        Assert.Equal(900, window.Min);
        Assert.Equal(2, histogram.Snapshot().Count);
    }

    [Fact]
    public void ExplicitClock_Overloads_UseTheCallersAxis()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        histogram.Record(5_000, nowMicros: 100_000_000);
        histogram.Record(7_000, nowMicros: 100_000_000 + 400_000_000);

        Assert.Equal(1, histogram.Snapshot(TimeSpan.FromMinutes(1), 100_000_000 + 400_000_000).Count);
        Assert.Equal(2, histogram.Snapshot(TimeSpan.FromMinutes(15), 100_000_000 + 400_000_000).Count);
    }

    [Fact]
    public void Format_IsTheOneConsistentLine()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        for (int i = 1; i <= 100; i++) histogram.Record(i * 100); // 0.1 ms .. 10 ms

        string text = histogram.Snapshot().Format();
        Assert.Matches(@"^p50 \d+\.\d ms p75 \d+\.\d p90 \d+\.\d p95 \d+\.\d p99 \d+\.\d p99\.9 \d+\.\d max 10\.0 \(n=100, min 0\.1, mean \d+\.\d \+/-\d+\.\d\)$", text);
        Assert.Equal(text, histogram.Snapshot().ToString());
    }

    [Fact]
    public void Format_ShowsSubMillisecondValuesWithThreeDecimals()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        histogram.Record(42);
        Assert.Contains("p50 0.042 ms", histogram.Snapshot().Format());
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        histogram.Record(100);
        histogram.Reset();
        Assert.True(histogram.Snapshot().IsEmpty);
        Assert.True(histogram.Snapshot(TimeSpan.FromMinutes(15)).IsEmpty);
        histogram.Record(200);
        Assert.Equal(1, histogram.Snapshot().Count);
    }

    [Fact]
    public async Task ConcurrentWriters_LoseNoSampleAndReadersNeverSeeATornSnapshot()
    {
        var histogram = new LatencyHistogram();
        const int Threads = 8;
        const int PerThread = 100_000;
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                LatencySnapshot snap = histogram.Snapshot();
                LatencySnapshot window = histogram.Snapshot(TimeSpan.FromMinutes(1));
                foreach (LatencySnapshot s in new[] { snap, window })
                {
                    if (s.IsEmpty) continue;
                    Assert.True(s.Min <= s.P50 && s.P50 <= s.P99 && s.P99 <= s.Max, "percentiles must stay ordered inside [min, max]");
                }
            }
        });

        Parallel.For(0, Threads, t =>
        {
            for (int i = 0; i < PerThread; i++) histogram.Record(100 + ((t * PerThread) + i) % 5_000);
        });
        stop.Cancel();
        await reader;

        LatencySnapshot total = histogram.Snapshot();
        Assert.Equal(Threads * PerThread, total.Count);
        long expectedSum = 0;
        for (int t = 0; t < Threads; t++)
        {
            for (int i = 0; i < PerThread; i++) expectedSum += 100 + (((t * PerThread) + i) % 5_000);
        }

        Assert.Equal((double)expectedSum / (Threads * PerThread), total.Mean, 1e-6);
        Assert.Equal(100, total.Min);
        Assert.Equal(5_099, total.Max);
    }

    [Fact]
    public void Registry_IsBoundedAndSendsTheExcessToOneOverflowHistogram()
    {
        var registry = new LatencyRegistry();
        for (int i = 0; i < LatencyRegistry.MaxNames + 50; i++) registry.Record(LatencyNames.PacketHandlerPrefix + "name" + i, 10 + i);

        IReadOnlyList<KeyValuePair<string, LatencyHistogram>> all = registry.Histograms();
        Assert.Equal(LatencyRegistry.MaxNames + 1, all.Count);
        KeyValuePair<string, LatencyHistogram> overflow = all.Single(p => p.Key == LatencyRegistry.OverflowName);
        Assert.Equal(50, overflow.Value.Count);
        Assert.Same(registry.Get(LatencyNames.PacketHandlerPrefix + "name0"), registry.Get(LatencyNames.PacketHandlerPrefix + "name0"));
        Assert.Same(registry.Get("brand-new"), registry.Get("another-new"));
    }

    [Fact]
    public void TheDefaultLayoutNames_AreCappedSeparately_SoTheWorstCaseMemoryHasARealBound()
    {
        var registry = new LatencyRegistry();
        for (int i = 0; i < 100; i++) registry.Record("metric" + i, 100);

        IReadOnlyList<KeyValuePair<string, LatencyHistogram>> all = registry.Histograms();
        Assert.Equal(LatencyRegistry.MaxDefaultNames + 1, all.Count); // the 16 default-layout names plus the overflow histogram
        Assert.Equal(100 - LatencyRegistry.MaxDefaultNames, all.Single(p => p.Key == LatencyRegistry.OverflowName).Value.Count);
        Assert.Same(registry.Get("metric0"), registry.Get("metric0"));
        Assert.NotSame(registry.Get("metric0"), registry.Get("metric99"));
        // Packet handlers still get their own (compact) histograms once the default names are used up.
        registry.Record(LatencyNames.PacketHandlerPrefix + "CMSG_X", 5);
        Assert.Contains(registry.Histograms(), p => p.Key == LatencyNames.PacketHandlerPrefix + "CMSG_X");
    }

    [Fact]
    public void AWriterStalledForAWholeRing_DoesNotEraseTheNewerSamplesOfItsSlice()
    {
        var histogram = new LatencyHistogram(new ManualClock());
        long slice = (long)LatencyHistogram.DefaultSliceLength.TotalMilliseconds * 1000;
        long oldNow = 5 * slice;
        long newNow = (5 + LatencyHistogram.DefaultSlices) * slice; // the same ring slot, one lap later

        histogram.Record(7_000, newNow);
        histogram.Record(9_000, oldNow); // the stalled writer finally records with its old timestamp

        LatencySnapshot recent = histogram.Snapshot(TimeSpan.FromMinutes(1), newNow);
        Assert.Equal(1, recent.Count);
        Assert.Equal(7_000, recent.Max);
        Assert.Equal(2, histogram.Snapshot().Count); // the totals still count it
    }

    [Fact]
    public void ACompactHistogram_HasNoTotalsArray_AndItsSmallestWindowIsOneSlice()
    {
        var clock = new ManualClock();
        var compact = new LatencyHistogram(clock, LatencyHistogram.CompactSliceLength, LatencyHistogram.CompactSlices, keepTotals: false);
        var normal = new LatencyHistogram(clock);
        compact.Record(1_000, 0);
        normal.Record(1_000, 0);
        long fourMinutes = 4L * 60 * 1_000_000;

        Assert.Equal(1, compact.Snapshot(TimeSpan.FromMinutes(1), fourMinutes).Count); // the 5 minute slice still covers it
        Assert.True(normal.Snapshot(TimeSpan.FromMinutes(1), fourMinutes).IsEmpty);
        Assert.Equal(compact.Span, normal.Span);
        Assert.Equal(1, compact.Snapshot().Count); // since start answers the retained span
        Assert.Equal(1, compact.Count);
    }

    [Fact]
    public void PacketHandlerNames_GetTheCompactConfiguration_AndTheTopListRanksTheLast15Minutes()
    {
        var clock = new ManualClock();
        var registry = new LatencyRegistry(clock);
        const string Prefix = LatencyNames.PacketHandlerPrefix;
        registry.Get(Prefix + "CMSG_SPIKED_EARLY").Record(900_000); // a huge outlier, then idle
        registry.Get("db.other").Record(1_000);
        clock.Advance(TimeSpan.FromMinutes(20));
        for (int i = 0; i < 100; i++)
        {
            registry.Get(Prefix + "CMSG_STEADY").Record(5_000);
            registry.Get(Prefix + "CMSG_SLOW_NOW").Record(20_000);
        }

        var top = registry.TopByP99(Prefix, 5, TimeSpan.FromMinutes(15));

        Assert.Equal([Prefix + "CMSG_SLOW_NOW", Prefix + "CMSG_STEADY"], top.Select(t => t.Name).ToArray()); // the early spike is out of the window
        Assert.Equal(20_000, top[0].Recent.P99);
        Assert.Equal(100, top[0].Recent.Count);
        // The compact configuration: a one minute window of the packet histogram reaches back a whole 5 minute slice.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(100, registry.Get(Prefix + "CMSG_STEADY").Snapshot(TimeSpan.FromMinutes(1)).Count);
        Assert.True(registry.Get("db.other").Snapshot(TimeSpan.FromMinutes(1)).IsEmpty);
    }
}
