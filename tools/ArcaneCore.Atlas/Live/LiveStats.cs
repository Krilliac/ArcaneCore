using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Counters of one connection: what arrived, and what was refused and why. Bad input is counted and reported through
/// <c>live.status</c>, never thrown. Written by the connection's reader loop, read from any thread.
/// </summary>
internal sealed class LiveStats
{
    private long _frames;
    private long _bytes;
    private long _badMessages;
    private long _badItems;
    private long _oversizeLines;
    private long _truncatedLines;
    private long _unknownOps;
    private long _serverErrors;
    private long _lateResults;
    private long _ignoredFrames;
    private string? _lastProblem;
    private string? _lastServerError;

    public long Frames => Interlocked.Read(ref _frames);

    public long BadMessages => Interlocked.Read(ref _badMessages);

    public long BadItems => Interlocked.Read(ref _badItems);

    public long OversizeLines => Interlocked.Read(ref _oversizeLines);

    public long TruncatedLines => Interlocked.Read(ref _truncatedLines);

    public long UnknownOps => Interlocked.Read(ref _unknownOps);

    public long ServerErrors => Interlocked.Read(ref _serverErrors);

    public long LateResults => Interlocked.Read(ref _lateResults);

    public long IgnoredFrames => Interlocked.Read(ref _ignoredFrames);

    public string? LastProblem => Volatile.Read(ref _lastProblem);

    public string? LastServerError => Volatile.Read(ref _lastServerError);

    public void FrameAccepted(int rejectedItems, string? firstProblem)
    {
        Interlocked.Increment(ref _frames);
        ItemsRejected(rejectedItems, firstProblem);
    }

    /// <summary>Counts entries dropped from an otherwise accepted message (entities and grid pairs of a frame, maps of a hello).</summary>
    public void ItemsRejected(int count, string? firstProblem)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _badItems, count);
            Volatile.Write(ref _lastProblem, Clip(firstProblem));
        }
    }

    /// <summary>Counts a well-formed frame that was not applied because it is for a map the client no longer watches.</summary>
    public void IgnoredFrame() => Interlocked.Increment(ref _ignoredFrames);

    public void BadMessage(string problem)
    {
        Interlocked.Increment(ref _badMessages);
        Volatile.Write(ref _lastProblem, Clip(problem));
    }

    public void Oversize(long length, int limit)
    {
        Interlocked.Increment(ref _oversizeLines);
        Volatile.Write(ref _lastProblem, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"a line of {length} bytes exceeded the {limit} byte limit and was skipped"));
    }

    public void Truncated(long length)
    {
        Interlocked.Increment(ref _truncatedLines);
        Volatile.Write(ref _lastProblem, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the connection ended inside a line ({length} bytes) that was never completed"));
    }

    public void UnknownOp(string op)
    {
        Interlocked.Increment(ref _unknownOps);
        Volatile.Write(ref _lastProblem, Clip($"unknown op '{op}'"));
    }

    public void ServerError(string text)
    {
        Interlocked.Increment(ref _serverErrors);
        Volatile.Write(ref _lastServerError, Clip(text));
    }

    public void LateResult() => Interlocked.Increment(ref _lateResults);

    public void BytesRead(long total) => Interlocked.Exchange(ref _bytes, total);

    public JsonObject ToJson(long missedFrames, long droppedForCapacity) => new()
    {
        ["frames"] = Frames,
        ["bytes"] = Interlocked.Read(ref _bytes),
        ["missedFrames"] = missedFrames,
        ["badMessages"] = BadMessages,
        ["badItems"] = BadItems,
        ["oversizeLines"] = OversizeLines,
        ["truncatedLines"] = TruncatedLines,
        ["unknownOps"] = UnknownOps,
        ["lateResults"] = LateResults,
        ["ignoredFrames"] = IgnoredFrames,
        ["droppedForCapacity"] = droppedForCapacity,
        ["serverErrors"] = ServerErrors,
        ["lastProblem"] = LastProblem,
        ["lastServerError"] = LastServerError,
    };

    private static string? Clip(string? text)
        => text is { Length: > 300 } ? string.Concat(text.AsSpan(0, 300), "...") : text;
}
