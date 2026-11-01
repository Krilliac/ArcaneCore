namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// The GM per-session trace (<c>.debug trace session</c>): one traced character writes a timeline of its packets, movement, auras, deaths and
/// anti-cheat findings to the log category <see cref="SessionTraceFeature.LogCategory"/>. Bound from <c>Diagnostics:SessionTrace</c> when the
/// feature is created; a change needs a restart.
/// </summary>
public sealed class SessionTraceOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Diagnostics:SessionTrace";

    /// <summary>Permit a Game Master to start session traces. When false, <c>.debug trace session ... on</c> is refused and nothing is ever written. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes a trace runs when <c>on</c> is given no minutes. Clamped to 1 to <see cref="MaxMinutes"/>. Default 30.</summary>
    public int DefaultMinutes { get; set; } = 30;

    /// <summary>Longest a trace may run in minutes; a longer request is clamped and the reply says so. Default 240.</summary>
    public int MaxMinutes { get; set; } = 240;

    /// <summary>Lines per second one trace may write on average (the refill rate of its token bucket); lines over the budget are dropped and counted. Default 50.</summary>
    public int LinesPerSecond { get; set; } = 50;

    /// <summary>Lines one trace may write in a burst (the capacity of its token bucket, full when the trace starts). Default 200.</summary>
    public int BurstLines { get; set; } = 200;

    /// <summary>A packet handler is logged (<c>handled</c> line) only when it ran longer than this many microseconds. Default 1000.</summary>
    public int SlowHandlerMicroseconds { get; set; } = 1000;

    /// <summary>Most characters that can be traced at the same time; turning a further one on is refused. Re-tracing a traced character is not a further one. Default 16.</summary>
    public int MaxTracedCharacters { get; set; } = 16;

    /// <summary>Puts every value in the range the feature can work with (a zero or negative count would stop the bucket or the registry from working).</summary>
    internal void Normalize()
    {
        MaxMinutes = Math.Max(1, MaxMinutes);
        DefaultMinutes = Math.Clamp(DefaultMinutes, 1, MaxMinutes);
        LinesPerSecond = Math.Max(1, LinesPerSecond);
        BurstLines = Math.Max(1, BurstLines);
        SlowHandlerMicroseconds = Math.Max(0, SlowHandlerMicroseconds);
        MaxTracedCharacters = Math.Max(1, MaxTracedCharacters);
    }
}
