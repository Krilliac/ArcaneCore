namespace ArcaneCore.Protocol;

/// <summary>The primitive a <see cref="PacketReader"/> read performed.</summary>
internal enum PacketReadKind : byte
{
    Byte,
    UInt16,
    UInt32,
    UInt64,
    Single,
    FiniteSingle,
    PackedGuid,
    Bytes,
    Skip,
    Count,
    ByteCount,
    CString,
    ToEnd,
}

/// <summary>
/// One primitive read. <paramref name="Offset"/> is the cursor before the read. <paramref name="Width"/> is the bytes consumed
/// when <paramref name="Ok"/> (a CString counts its terminator when present; ToEnd is the remaining bytes); on failure it is the
/// bytes the read needed (fixed width), or for CString / Bytes / Skip the string length found / the count requested.
/// </summary>
internal readonly record struct PacketReadEvent(PacketReadKind Kind, int Offset, int Width, bool Ok);

/// <summary>
/// A test seam: lets a test learn which reads a handler performs on a payload. It is inert unless a test calls
/// <see cref="Begin"/>; with no scope open anywhere in the process a <see cref="PacketReader"/> read pays one static int load and a
/// not-taken branch, and nothing allocates. Events are recorded only on the thread that opened the scope (the sink is
/// thread-static), so concurrent sessions on other threads never write to it.
/// </summary>
internal static class PacketReadTrace
{
    [ThreadStatic]
    private static List<PacketReadEvent>? t_events;

    private static int s_tracers;

    /// <summary>True while any thread in the process has a scope open (the cheap pre-check the reader makes).</summary>
    internal static bool Active => Volatile.Read(ref s_tracers) != 0;

    /// <summary>
    /// Start collecting this thread's reads into <paramref name="sink"/>. Dispose the scope on the same thread; scopes nest and
    /// the previous sink is restored on dispose.
    /// </summary>
    internal static Scope Begin(List<PacketReadEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        List<PacketReadEvent>? previous = t_events;
        t_events = sink;
        Interlocked.Increment(ref s_tracers);
        return new Scope(previous);
    }

    /// <summary>Append one event when this thread has a sink; a no-op otherwise.</summary>
    internal static void Record(PacketReadKind kind, int offset, int width, bool ok)
        => t_events?.Add(new PacketReadEvent(kind, offset, width, ok));

    /// <summary>The open scope; disposing it (once) restores the previous sink and releases the process-wide count.</summary>
    internal struct Scope : IDisposable
    {
        private readonly List<PacketReadEvent>? _previous;
        private bool _disposed;

        internal Scope(List<PacketReadEvent>? previous)
        {
            _previous = previous;
            _disposed = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            t_events = _previous;
            Interlocked.Decrement(ref s_tracers);
        }
    }
}
