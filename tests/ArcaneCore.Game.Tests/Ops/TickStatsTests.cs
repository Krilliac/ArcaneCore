using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

public sealed class TickStatsTests
{
    [Fact]
    public void Record_DoesNotAllocate()
    {
        var stats = new TickStats(64);
        // Tiered JIT promotion can occur well after the first call. Warm the actual
        // hot path past that threshold before taking the allocation baseline; the
        // assertion below is about steady-state Record, not runtime compilation.
        for (int i = 0; i < 100_000; i++)
        {
            stats.Record(100, 10, 50_000);
        }
        long firstBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            stats.Record(i, i, 50_000);
        }
        long firstAllocated = GC.GetAllocatedBytesForCurrentThread() - firstBefore;

        long secondBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            stats.Record(i, i, 50_000);
        }
        long secondAllocated = GC.GetAllocatedBytesForCurrentThread() - secondBefore;

        long thirdBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            stats.Record(i, i, 50_000);
        }
        long thirdAllocated = GC.GetAllocatedBytesForCurrentThread() - thirdBefore;

        // Keep all assertions outside the measured windows so assertion machinery
        // cannot contaminate the next allocation sample.
        Assert.Equal(0, firstAllocated);
        Assert.Equal(0, secondAllocated);
        Assert.Equal(0, thirdAllocated);
    }

    [Fact]
    public void Percentiles_OfKnownSamples_UseNearestRank()
    {
        var stats = new TickStats(4096);
        for (long us = 1; us <= 1000; us++)
        {
            stats.Record(us, 0, 1_000_000);
        }

        TickStatsSnapshot s = stats.Snapshot();
        // Nearest rank: value at index ceil(p/100 * n) in the sorted samples (1-based).
        Assert.Equal(1000, s.Samples);
        Assert.Equal(500, s.P50Micros);
        Assert.Equal(900, s.P90Micros);
        Assert.Equal(990, s.P99Micros);
        Assert.Equal(999, s.P999Micros);
        Assert.Equal(1000, s.MaxMicros);
        Assert.Equal(500.5, s.MeanMicros, 6);
    }

    [Fact]
    public void RingWrap_KeepsLatestCapacitySamples_AndCountsAll()
    {
        var stats = new TickStats(10);
        for (long us = 1; us <= 25; us++)
        {
            stats.Record(us, us * 2, 1_000_000);
        }

        TickStatsSnapshot s = stats.Snapshot();
        Assert.Equal(25, s.TotalTicks);
        Assert.Equal(10, s.Samples);
        Assert.Equal(25, s.MaxMicros);
        Assert.Equal(20, s.P50Micros); // retained samples are 16..25
        Assert.Equal(41.0, s.MeanAllocatedBytes, 6);
    }

    [Fact]
    public void Overrun_CountsOnlyAboveInterval()
    {
        var stats = new TickStats(16);
        stats.Record(50_000, 0, 50_000); // equal: not an overrun
        stats.Record(50_001, 0, 50_000);
        stats.Record(10, 0, 50_000);
        Assert.Equal(1, stats.Snapshot().Overruns);
    }

    [Fact]
    public void FrameCadence_TracksMeanEffectiveRateAndFrameOverruns()
    {
        var stats = new TickStats(8);
        stats.Record(100, 0, 1_000, 1_000);
        stats.Record(100, 0, 1_000, 2_000);

        TickStatsSnapshot snapshot = stats.Snapshot();
        Assert.Equal(2, snapshot.FrameSamples);
        Assert.Equal(1_500, snapshot.MeanFrameIntervalMicros);
        Assert.Equal(1_000_000d / 1_500d, snapshot.EffectiveTicksPerSecond!.Value, 6);
        Assert.Equal(1, snapshot.FrameOverruns);
    }

    [Fact]
    public void FrameCadence_EmptyAndLegacyRecordsRemainUnmeasured()
    {
        var stats = new TickStats(4);
        stats.Record(10, 0, 1_000);
        TickStatsSnapshot snapshot = stats.Snapshot();

        Assert.Equal(0, snapshot.FrameSamples);
        Assert.Equal(0, snapshot.MeanFrameIntervalMicros);
        Assert.Null(snapshot.EffectiveTicksPerSecond);
        Assert.Equal(0, snapshot.FrameOverruns);
    }

    [Fact]
    public void FrameCadence_RingWrapUsesRetainedFrameSamples()
    {
        var stats = new TickStats(2);
        stats.Record(10, 0, 1_000, 1_000);
        stats.Record(10, 0, 1_000, 2_000);
        stats.Record(10, 0, 1_000, 3_000);

        TickStatsSnapshot snapshot = stats.Snapshot();
        Assert.Equal(2, snapshot.FrameSamples);
        Assert.Equal(2_500, snapshot.MeanFrameIntervalMicros);
        Assert.Equal(2, snapshot.FrameOverruns);
    }

    [Fact]
    public void Empty_SnapshotReportsZeroSamples_NotAFabricatedPercentile()
    {
        TickStatsSnapshot s = new TickStats(8).Snapshot();
        Assert.Equal(0, s.Samples);
        Assert.Equal(0, s.TotalTicks);
        Assert.Equal(0, s.LastTickTimestamp);
    }

    [Fact]
    public async Task Snapshot_WhileWriterRuns_NeverDecreasesTotalOrReturnsTornSamples()
    {
        var stats = new TickStats(256);
        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            // Every sample is duration == allocated bytes, so a torn pair is detectable via the mean.
            for (long i = 1; !stop.IsCancellationRequested; i++)
            {
                stats.Record(7, 7, 1_000_000);
            }
        });

        long last = 0;
        for (int i = 0; i < 2000; i++)
        {
            TickStatsSnapshot s = stats.Snapshot();
            Assert.True(s.TotalTicks >= last);
            last = s.TotalTicks;
            if (s.Samples > 0)
            {
                Assert.Equal(7, s.MaxMicros);
                Assert.Equal(7, s.MeanAllocatedBytes, 6);
            }
        }

        stop.Cancel();
        await writer;
    }

    [Fact]
    public void Percentile_NearestRank_RejectsEmptyInput()
        => Assert.Throws<ArgumentException>(() => TickStats.NearestRank([], 50));
}
