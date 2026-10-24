using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Insight;

/// <summary>One entity of a recorded frame (columnar fields; strings are interned in the recorder).</summary>
internal readonly record struct RecordedEntity(ulong Guid, byte Kind, float X, float Y, float Z, byte Hp, uint Flags, uint D);

/// <summary>A frame reconstructed from the recording.</summary>
internal sealed record ReplayFrame(long Seq, long ClockMs, uint Map, uint Instance, IReadOnlyList<RecordedEntity> Entities);

/// <summary>What changed between two recorded frames.</summary>
internal sealed record ReplayDiff(IReadOnlyList<ulong> Appeared, IReadOnlyList<ulong> Vanished, IReadOnlyList<ulong> Died, IReadOnlyList<ulong> Moved);

/// <summary>Bounded ring of compact live frames. Stub: filled in by the implementation commit.</summary>
internal sealed class InsightRecorder(long maxBytes, TimeSpan maxAge)
{
    public const uint FlagCombat = 1;

    public static IReadOnlyList<string> ReplayFields { get; } = [];

    public long MaxBytes { get; } = maxBytes;

    public TimeSpan MaxAge { get; } = maxAge;

    public long Bytes => throw new NotImplementedException();

    public int FrameCount => throw new NotImplementedException();

    public long OldestSeq => throw new NotImplementedException();

    public long NewestSeq => throw new NotImplementedException();

    public long EvictedFrames => throw new NotImplementedException();

    public void Add(long seq, long clockMs, uint map, uint instance, ReadOnlySpan<RecordedEntity> entities) => throw new NotImplementedException();

    public bool AddJson(JsonNode? frame, out string? problem) => throw new NotImplementedException();

    public ReplayFrame? Exact(long seq) => throw new NotImplementedException();

    public ReplayFrame? AtClock(long clockMs) => throw new NotImplementedException();

    public ReplayFrame? Latest() => throw new NotImplementedException();

    public ReplayDiff Diff(long a, long b, double movedYards) => throw new NotImplementedException();

    public IReadOnlyList<RecordedEntity> Deaths(long windowMs, uint map) => throw new NotImplementedException();

    public string? MotionName(uint flags) => throw new NotImplementedException();
}
