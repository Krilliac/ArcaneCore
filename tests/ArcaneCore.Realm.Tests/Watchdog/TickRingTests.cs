using ArcaneCore.Kernel.Ops.Watchdog;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class TickRingTests
{
    [Theory]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    [InlineData(4096, 4096)]
    [InlineData(5000, 8192)]
    [InlineData(int.MaxValue, TickRing.MaxCapacity)]
    public void Capacity_IsClampedAndRoundedToAPowerOfTwo(int requested, int expected)
        => Assert.Equal(expected, new TickRing(requested).Capacity);

    [Fact]
    public void Snapshot_ReturnsTheMostRecentSamplesOldestFirst_AndWrapsAround()
    {
        var ring = new TickRing(16);
        for (int i = 1; i <= 40; i++)
        {
            ring.Record(i);
        }

        // Capacity - 1 samples: the slot the writer may be filling next is never read.
        long[] buffer = new long[16];
        int count = ring.Snapshot(buffer);
        Assert.Equal(15, count);
        Assert.Equal(Enumerable.Range(26, 15).Select(i => (long)i), buffer.Take(count));
        Assert.Equal(40, ring.Count);

        long[] small = new long[4];
        Assert.Equal(4, ring.Snapshot(small));
        Assert.Equal(new long[] { 37, 38, 39, 40 }, small);
    }

    [Fact]
    public void Snapshot_OfAnEmptyOrPartialRing_ReturnsWhatWasRecorded()
    {
        var ring = new TickRing(16);
        long[] buffer = new long[16];
        Assert.Equal(0, ring.Snapshot(buffer));
        ring.Record(7);
        ring.Record(9);
        Assert.Equal(2, ring.Snapshot(buffer));
        Assert.Equal(new long[] { 7, 9 }, buffer.Take(2));
    }

    [Fact]
    public void Record_DoesNotAllocate()
    {
        var ring = new TickRing(64);
        long value = 0;
        Assert.Equal(0, WatchdogTestKit.AllocatedBy(() => ring.Record(value++), 100_000));
    }

    [Fact]
    public void Snapshot_ConcurrentWithTheWriter_NeverContainsAMixedGenerationOrTornSample()
    {
        // The writer records the sequence 1, 2, 3, ...; any snapshot must be a run of consecutive values.
        //
        // The writer is a dedicated thread, not a thread-pool task: with the test assemblies running in parallel the pool can be starved
        // for longer than any fixed window (CI once saw "only 0 non-empty snapshots" in 500 ms: the writer had not run at all). The
        // measurement starts only once the writer has filled the ring, and it ends on a number of non-empty snapshots, with a generous
        // deadline for a host that is merely slow.
        const int WantedNonEmpty = 20_000;
        var ring = new TickRing(64);
        using var stop = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            long next = 1;
            while (!stop.IsSet)
            {
                ring.Record(next++);
                // A tight writer laps the whole ring during one copy, so every snapshot would be empty. Yield a moment between samples.
                Thread.SpinWait(64);
            }
        })
        { IsBackground = true, Name = "TickRing test writer" };
        writer.Start();

        long[] buffer = new long[64];
        int snapshots = 0;
        int nonEmpty = 0;
        int overlapped = 0; // snapshots during which the writer recorded: the ones that can tear
        string? problem = null;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => ring.Count >= ring.Capacity, TimeSpan.FromSeconds(30)), "the writer thread never filled the ring");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((nonEmpty < WantedNonEmpty || overlapped == 0) && DateTime.UtcNow < deadline && problem is null)
            {
                long before = ring.Count;
                int count = ring.Snapshot(buffer);
                snapshots++;
                if (ring.Count != before)
                {
                    overlapped++;
                }

                if (count == 0)
                {
                    continue;
                }

                nonEmpty++;
                for (int i = 1; i < count; i++)
                {
                    if (buffer[i] != buffer[i - 1] + 1)
                    {
                        problem = $"snapshot {snapshots} is not consecutive at {i}: {buffer[i - 1]} then {buffer[i]}";
                        break;
                    }
                }
            }
        }
        finally
        {
            stop.Set(); // never leave the writer spinning
            writer.Join();
        }

        Assert.Null(problem);
        Assert.True(nonEmpty >= WantedNonEmpty, $"only {nonEmpty} non-empty snapshots of {snapshots} were taken in 30 s");
        Assert.True(overlapped > 0, $"none of {snapshots} snapshots overlapped a Record, so none could have torn");
    }

    [Fact]
    public void RingStats_ComputesNearestRankPercentiles()
    {
        long[] samples = [.. Enumerable.Range(1, 100).Select(i => (long)i).Reverse()];
        RingStats stats = RingStats.Compute(samples);
        Assert.Equal(100, stats.Samples);
        Assert.Equal(50, stats.P50);
        Assert.Equal(90, stats.P90);
        Assert.Equal(99, stats.P99);
        Assert.Equal(100, stats.Max);
        Assert.Equal(50, stats.Mean);
        Assert.Equal(1, samples[0]); // sorted in place
    }

    [Fact]
    public void RingStats_OfNothing_IsEmptyAndNearestRankRefusesAnEmptySpan()
    {
        Assert.Equal(0, RingStats.Compute([]).Samples);
        Assert.Throws<ArgumentException>(() => RingStats.NearestRank([], 50));
        Assert.Equal(5, RingStats.NearestRank([5], 99.9));
    }
}
