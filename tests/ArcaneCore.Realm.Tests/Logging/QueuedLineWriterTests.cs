using ArcaneCore.Kernel.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>The bounded queue in front of every sink: ordering, overflow accounting, the drop notice, flush and shutdown drain.</summary>
public sealed class QueuedLineWriterTests
{
    /// <summary>A destination that blocks until released, then records every line.</summary>
    private sealed class GatedWriter : ILineWriter
    {
        private readonly ManualResetEventSlim _open = new(false);
        private readonly List<string> _lines = [];

        public int Flushes { get; private set; }

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

        public bool Disposed { get; private set; }

        public void Release() => _open.Set();

        public void Write(ReadOnlySpan<char> line)
        {
            _open.Wait();
            lock (_lines)
            {
                _lines.Add(line.ToString());
            }
        }

        public void Flush() => Flushes++;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Overflow_DropsAndCounts_ThenWritesOneNoticeWithTheCount()
    {
        var destination = new GatedWriter();
        using var queue = new QueuedLineWriter(destination, capacity: 2, "test", dropped => $"dropped {dropped}\n");

        queue.Write("a\n");
        // wait until the writer thread holds "a" (blocked in the destination), then fill the ring and overflow it
        Assert.True(SpinWait.SpinUntil(() => queue.Pending == 0, TimeSpan.FromSeconds(5)));
        queue.Write("b\n");
        queue.Write("c\n");
        int before = queue.Pending;
        queue.Write("d\n");
        queue.Write("e\n");

        Assert.Equal(2, before);
        Assert.Equal(2, queue.Dropped);
        destination.Release();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));

        // the notice is written at the first drain after the drops were observed (here: right after "a", which the writer
        // thread was delivering while d and e were dropped), once, with the count; the surviving lines keep their order
        IReadOnlyList<string> lines = destination.Lines;
        Assert.Equal(4, lines.Count);
        Assert.Equal(["a\n", "b\n", "c\n"], lines.Where(l => l != "dropped 2\n"));
        Assert.Single(lines, "dropped 2\n");
        Assert.True(destination.Flushes >= 1);
    }

    [Fact]
    public void Dispose_DrainsEverythingQueued_ThenDisposesTheDestination()
    {
        var destination = new GatedWriter();
        var queue = new QueuedLineWriter(destination, capacity: 100, "test", dropped => $"dropped {dropped}\n");
        for (int i = 0; i < 50; i++)
        {
            queue.Write($"{i}\n");
        }

        destination.Release();
        queue.Dispose();

        Assert.Equal(50, destination.Lines.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"{i}\n"), destination.Lines);
        Assert.True(destination.Disposed);
        Assert.Equal(0, queue.Dropped);
        queue.Write("after\n"); // ignored, counted as dropped, no exception
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public void Dispose_WithAWedgedDestination_ReturnsAfterTheDrainTimeout()
    {
        var destination = new GatedWriter();
        var queue = new QueuedLineWriter(destination, capacity: 4, "test", dropped => "x\n", shutdownDrain: TimeSpan.FromMilliseconds(200));
        queue.Write("stuck\n");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        queue.Dispose();
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
        Assert.False(destination.Disposed);
        destination.Release();
    }

    [Fact]
    public void Flush_TimesOutWhileTheDestinationBlocks_AndSucceedsOnceReleased()
    {
        var destination = new GatedWriter();
        using var queue = new QueuedLineWriter(destination, capacity: 4, "test", dropped => "x\n");
        queue.Write("one\n");
        Assert.False(queue.Flush(TimeSpan.FromMilliseconds(100)));
        destination.Release();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["one\n"], destination.Lines);
    }

    [Fact]
    public void ManyProducers_LoseNothingBelowCapacity_AndKeepEachLineIntact()
    {
        var destination = new GatedWriter();
        destination.Release();
        using var queue = new QueuedLineWriter(destination, capacity: 10_000, "test", dropped => "x\n");
        Parallel.For(0, 4, p =>
        {
            for (int i = 0; i < 1000; i++)
            {
                queue.Write($"p{p} line {i} {new string('.', 40)}\n");
            }
        });

        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));
        IReadOnlyList<string> lines = destination.Lines;
        Assert.Equal(4000, lines.Count);
        Assert.Equal(0, queue.Dropped);
        Assert.All(lines, l => Assert.Matches("^p[0-3] line \\d+ \\.{40}\\n$", l));
        for (int p = 0; p < 4; p++)
        {
            string prefix = $"p{p} line ";
            int[] order = lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => int.Parse(l.AsSpan(prefix.Length, l.IndexOf(' ', prefix.Length) - prefix.Length))).ToArray();
            Assert.Equal(Enumerable.Range(0, 1000), order);
        }
    }
}
