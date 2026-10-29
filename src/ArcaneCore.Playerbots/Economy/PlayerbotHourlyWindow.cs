namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bounded queue of world-time stamps counting what happened in the last <see cref="WindowMs"/> (an hour). Stamps arrive in world-time
/// order, so the oldest is always at the head; ages are compared as unsigned differences, which survives the 32-bit clock wrapping.
/// Ownership: the instance owns its queue; it holds at most max(limit, 1) stamps, so it never grows without bound. World thread only.
/// </summary>
internal sealed class PlayerbotHourlyWindow
{
    /// <summary>The window length in milliseconds of world time.</summary>
    internal const uint WindowMs = 3_600_000;

    private readonly Queue<uint> _stamps = new();

    /// <summary>How many stamps lie within <see cref="WindowMs"/> before <paramref name="now"/>; older stamps are dropped.</summary>
    internal int CountAt(uint now)
    {
        Expire(now);
        return _stamps.Count;
    }

    /// <summary>Whether another event fits under <paramref name="limit"/> (a limit of 0 or less never has room).</summary>
    internal bool HasRoom(uint now, int limit) => limit > 0 && CountAt(now) < limit;

    /// <summary>Notes an event at <paramref name="now"/>, keeping at most max(<paramref name="limit"/>, 1) stamps (the oldest go first).</summary>
    internal void Note(uint now, int limit)
    {
        Expire(now);
        _stamps.Enqueue(now);
        int keep = Math.Max(limit, 1);
        while (_stamps.Count > keep) _stamps.Dequeue();
    }

    private void Expire(uint now)
    {
        while (_stamps.TryPeek(out uint oldest) && unchecked(now - oldest) >= WindowMs) _stamps.Dequeue();
    }
}
