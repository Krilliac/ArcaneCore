using SessionTraceRecord = ArcaneCore.World.Net.SessionTrace.SessionTrace;

namespace ArcaneCore.World.Net;

/// <summary>
/// The session's side of the GM per-session trace (<c>.debug trace session</c>, ArcaneCore.World.Net.SessionTrace): one field that holds the
/// trace while this connection belongs to a traced character. Every hook is <c>Volatile.Read(ref _sessionTrace) is { } trace</c>, a single
/// read and branch that allocates nothing when the session is untraced.
/// </summary>
public sealed partial class WorldSession
{
    private SessionTraceRecord? _sessionTrace;

    /// <summary>The trace this connection feeds, or null (the common case). Any thread.</summary>
    internal SessionTraceRecord? SessionTrace => Volatile.Read(ref _sessionTrace);

    /// <summary>The trace of this session's character now follows this connection. Called by the trace when it attaches.</summary>
    internal void AttachSessionTrace(SessionTraceRecord trace) => Volatile.Write(ref _sessionTrace, trace);

    /// <summary>This connection stops feeding <paramref name="trace"/> (a different trace, or none, stays). Called by the trace when it detaches.</summary>
    internal void DetachSessionTrace(SessionTraceRecord trace) => Interlocked.CompareExchange(ref _sessionTrace, null, trace);
}
