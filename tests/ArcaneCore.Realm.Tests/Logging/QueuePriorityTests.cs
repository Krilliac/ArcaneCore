using System.Diagnostics;
using ArcaneCore.Kernel.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>The priority (Error and above) lane of <see cref="QueuedLineWriter"/>: bounded cost, ordering, and what Flush promises.</summary>
public sealed class QueuePriorityTests
{
    /// <summary>A destination that blocks until released, then records every line in order.</summary>
    private sealed class GatedWriter : ILineWriter
    {
        private readonly ManualResetEventSlim _open = new(false);
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Release() => _open.Set();

        public void Write(ReadOnlySpan<char> line)
        {
            _open.Wait();
            lock (_lines)
            {
                _lines.Add(line.ToString());
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private static QueuedLineWriter Blocked(GatedWriter destination, int capacity)
    {
        var queue = new QueuedLineWriter(destination, capacity, "test", dropped => $"dropped {dropped}\n", priorityDropNotice: lost => $"LOST {lost}\n");
        queue.Write("hold\n");
        Assert.True(SpinWait.SpinUntil(() => queue.Pending == 0, TimeSpan.FromSeconds(5)));
        return queue;
    }

    /// <summary>Ticks per write of <paramref name="writes"/> priority lines into a ring that is full of ordinary lines.</summary>
    private static double PriorityWriteCost(int capacity, int writes)
    {
        var destination = new GatedWriter();
        using QueuedLineWriter queue = Blocked(destination, capacity);
        for (int i = 0; i < capacity; i++)
        {
            queue.Write("o\n");
        }

        Assert.Equal(capacity, queue.Pending);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < writes; i++)
        {
            queue.Write("P\n", priority: true);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        destination.Release();
        return (double)elapsed / writes;
    }

    [Fact]
    public void ErrorWrite_IntoAFullRing_CostDoesNotScaleWithCapacity()
    {
        PriorityWriteCost(64, 50); // warm up the JIT
        double small = double.MaxValue;
        double large = double.MaxValue;
        for (int trial = 0; trial < 3; trial++)
        {
            small = Math.Min(small, PriorityWriteCost(64, 50));
            large = Math.Min(large, PriorityWriteCost(65536, 50));
        }

        // An O(capacity) eviction is about a thousand times dearer at 65536 slots than at 64; allow a generous factor for noise.
        Assert.True(large < small * 30 + 2000, $"priority write into a full ring: {small:F0} ticks at capacity 64, {large:F0} ticks at capacity 65536");
    }

    [Fact]
    public async Task Flush_DoesNotReturnTrue_BeforeAnEarlierPriorityLineIsDelivered_WhenLaterLinesCrowdTheRing()
    {
        var destination = new GatedWriter();
        using QueuedLineWriter queue = Blocked(destination, capacity: 3);
        queue.Write("P1\n", priority: true);

        Task<bool> flush = Task.Run(() => queue.Flush(TimeSpan.FromSeconds(3)));
        await Task.Delay(100); // let Flush take its target
        queue.Write("O2\n");
        queue.Write("O3\n");
        queue.Write("P4\n", priority: true);
        queue.Write("P5\n", priority: true);

        await Task.Delay(500);
        Assert.False(flush.IsCompleted, "Flush returned while the writer was still blocked on the first line");
        destination.Release();
        Assert.True(await flush.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("P1\n", destination.Lines);
    }

    [Fact]
    public void Lines_AreDeliveredInTheOrderTheyWereWritten_AcrossWrapAroundAndMixedPriorities()
    {
        var destination = new GatedWriter();
        destination.Release();
        using QueuedLineWriter queue = Blocked(destination, capacity: 4);
        var expected = new List<string> { "hold\n" };
        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 3; i++)
            {
                string line = $"{round}-{i}\n";
                queue.Write(line, priority: i == 1);
                expected.Add(line);
            }

            Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(expected, destination.Lines);
    }

    [Fact]
    public void PriorityLines_BehindOrdinaryOnes_KeepTheirPlace_AndOrdinaryOverflowStillDropsOnlyOrdinary()
    {
        var destination = new GatedWriter();
        using QueuedLineWriter queue = Blocked(destination, capacity: 3);
        queue.Write("o1\n");
        queue.Write("P2\n", priority: true);
        queue.Write("o3\n");
        queue.Write("o4\n");
        queue.Write("o5-dropped\n"); // the ordinary ring (3) is full
        queue.Write("P6\n", priority: true);

        Assert.Equal(1, queue.Dropped);
        Assert.Equal(0, queue.DroppedPriority);
        destination.Release();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["hold\n", "o1\n", "P2\n", "o3\n", "o4\n", "P6\n"], destination.Lines.Where(l => !l.StartsWith("dropped", StringComparison.Ordinal)));
        Assert.Single(destination.Lines, "dropped 1\n");
    }

    [Fact]
    public void ALostPriorityLine_IsReportedByItsOwnNoticeOnly()
    {
        var destination = new GatedWriter();
        using QueuedLineWriter queue = Blocked(destination, capacity: 2);
        queue.Write("P1\n", priority: true);
        queue.Write("P2\n", priority: true);
        queue.Write("P3-lost\n", priority: true);

        Assert.Equal(1, queue.DroppedPriority);
        destination.Release();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));
        // the notice lands at the writer thread's next look at the counters (here before the queued lines); it is the only notice
        Assert.Equal(["hold\n", "P1\n", "P2\n"], destination.Lines.Where(l => !l.StartsWith("LOST", StringComparison.Ordinal)));
        Assert.Single(destination.Lines, "LOST 1\n");
        Assert.DoesNotContain(destination.Lines, l => l.StartsWith("dropped", StringComparison.Ordinal));
    }
}
