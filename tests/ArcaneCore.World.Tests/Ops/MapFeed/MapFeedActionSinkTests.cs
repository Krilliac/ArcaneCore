using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>The reply sink of one action (review d1-review-3, IMPORTANT 7): lines are split on newlines under one lock, and the number of lines kept is bounded.</summary>
public sealed class MapFeedActionSinkTests
{
    private const int MaxLines = 2000;
    private const int MaxLateLines = 200;

    [Fact]
    public void AWriteWithSeveralNewlines_IsSplitIntoLines_AndAPartialLineWaitsForItsEnd()
    {
        var sink = new ActionSink(_ => { });

        sink.Write("one\r\ntwo\nthr");
        Assert.Equal(2, sink.Count);
        sink.Write("ee\n");
        sink.WriteLine("four");

        Assert.Equal(["one", "two", "three", "four"], sink.Finish());
    }

    [Fact]
    public void LinesPastTheCap_AreDropped_AndTheLastLineSaysHowManyWere()
    {
        var sink = new ActionSink(_ => { });
        for (int i = 0; i < MaxLines + 500; i++)
        {
            sink.WriteLine("line " + i);
        }

        Assert.Equal(MaxLines, sink.Count);
        string[] lines = sink.Finish();

        Assert.Equal(MaxLines + 1, lines.Length);
        Assert.Equal("line 0", lines[0]);
        Assert.Equal("line " + (MaxLines - 1), lines[MaxLines - 1]);
        Assert.Equal("... 500 more lines were dropped", lines[^1]);
    }

    [Fact]
    public void AnUnderTheCapRun_HasNoDroppedNote()
    {
        var sink = new ActionSink(_ => { });
        for (int i = 0; i < MaxLines; i++)
        {
            sink.WriteLine("line " + i);
        }

        Assert.Equal(MaxLines, sink.Finish().Length);
    }

    [Fact]
    public void LateLines_AreForwardedUpToTheCap_ThenOneNoteAndNothingMore()
    {
        var late = new List<string>();
        var sink = new ActionSink(late.Add);
        sink.Finish();

        for (int i = 0; i < MaxLateLines + 300; i++)
        {
            sink.WriteLine("late " + i);
        }

        Assert.Equal(MaxLateLines + 1, late.Count);
        Assert.Equal("late 0", late[0]);
        Assert.Equal("late " + (MaxLateLines - 1), late[MaxLateLines - 1]);
        Assert.StartsWith("...", late[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWriters_LoseNoLine_AndKeepEachLineWhole()
    {
        var sink = new ActionSink(_ => { });
        Task[] writers = [.. Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                sink.Write($"w{w}-{i}\nw{w}-{i}b\n");
            }
        }))];

        await Task.WhenAll(writers);

        string[] lines = sink.Finish();
        Assert.Equal(800, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^w[0-3]-\d+b?$", l));
    }
}
