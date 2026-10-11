namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The social rate limits, on world time (milliseconds, never wall-clock): a sliding window per key ("at most N in the last
/// <see cref="HourMs"/>"; the oldest slot frees when it leaves the window) and a minimum gap per key (a speaker). World thread only;
/// no allocation once a key's queue exists. Used for guild chat (key: guild id, speaker: bot id) and emotes (keys: bot, player, pair).
/// </summary>
internal sealed class PlayerbotSocialRateLimiter<TKey> where TKey : notnull
{
    /// <summary>One hour in milliseconds: the window of the guild chat cap.</summary>
    internal const long HourMs = 3_600_000;

    private readonly Dictionary<TKey, Queue<long>> _windows = [];
    private readonly Dictionary<TKey, long> _last = [];

    /// <summary>Whether <paramref name="key"/> has used fewer than <paramref name="cap"/> slots in the last <paramref name="windowMs"/> (nothing is taken).</summary>
    internal bool HasRoom(TKey key, int cap, long nowMs, long windowMs = HourMs)
    {
        if (cap <= 0) return false;
        return Used(key, nowMs, windowMs) < cap;
    }

    /// <summary>Take one slot of <paramref name="key"/>'s window. False (nothing taken) when the cap is spent or is not positive.</summary>
    internal bool TryTake(TKey key, int cap, long nowMs, long windowMs = HourMs)
    {
        if (!HasRoom(key, cap, nowMs, windowMs)) return false;
        if (!_windows.TryGetValue(key, out Queue<long>? window)) _windows[key] = window = new Queue<long>();
        Prune(window, nowMs, windowMs);
        window.Enqueue(nowMs);
        return true;
    }

    /// <summary>
    /// The slots <paramref name="key"/> has used in the last <paramref name="windowMs"/>. A question, not an event: it counts and removes
    /// nothing, so asking about an earlier moment than a previous question still answers for that moment. Slots are pruned only when one is taken or on <see cref="Sweep"/>.
    /// </summary>
    internal int Used(TKey key, long nowMs, long windowMs = HourMs)
    {
        if (!_windows.TryGetValue(key, out Queue<long>? window)) return 0;
        int used = 0;
        foreach (long taken in window)
            if (nowMs - taken < windowMs) used++;
        return used;
    }

    /// <summary>Whether <paramref name="key"/> last spoke at least <paramref name="gapMs"/> ago (or never). Nothing is recorded.</summary>
    internal bool GapElapsed(TKey key, long gapMs, long nowMs)
        => !_last.TryGetValue(key, out long last) || nowMs - last >= gapMs;

    /// <summary>Record that <paramref name="key"/> spoke now.</summary>
    internal void MarkSpoke(TKey key, long nowMs) => _last[key] = nowMs;

    /// <summary>Forget everything about <paramref name="key"/> (a guild that is gone).</summary>
    internal void Forget(TKey key)
    {
        _windows.Remove(key);
        _last.Remove(key);
    }

    /// <summary>Drop the keys with nothing inside <paramref name="windowMs"/>, so a long session does not grow the tables.</summary>
    internal void Sweep(long nowMs, long windowMs = HourMs)
    {
        foreach (TKey key in _windows.Keys.ToArray())
        {
            if (!_windows.TryGetValue(key, out Queue<long>? window)) continue;
            Prune(window, nowMs, windowMs);
            if (window.Count == 0) _windows.Remove(key);
        }

        foreach (KeyValuePair<TKey, long> entry in _last.ToArray())
            if (nowMs - entry.Value >= windowMs)
                _last.Remove(entry.Key);
    }

    private static void Prune(Queue<long> window, long nowMs, long windowMs)
    {
        while (window.Count > 0 && nowMs - window.Peek() >= windowMs) window.Dequeue();
    }
}
