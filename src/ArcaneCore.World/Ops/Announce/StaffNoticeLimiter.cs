namespace ArcaneCore.World.Ops.Announce;

/// <summary>A key whose cooldown window ended with notices held back (<see cref="StaffNoticeLimiter.TakeExpired"/>).</summary>
/// <param name="Label">The label given with the key's last passed notice (for example "account X (7): kick").</param>
/// <param name="Suppressed">Notices held back in that window.</param>
/// <param name="WindowSeconds">The window length.</param>
public readonly record struct SuppressedNotices(string Label, int Suppressed, int WindowSeconds);

/// <summary>
/// A per-key cooldown for the staff lines a feature sends directly, outside <c>World:Announce</c> (Warden and anti-cheat kicks and
/// bans). It is deliberately independent of <see cref="OpsAnnouncer"/>: its switches, severity floors and shared rate bucket never
/// hide a line, and a flood for one key (one account, one action) never holds back another key. The first notice for a key passes
/// and opens a window; repeats inside the window are counted, not sent. The count is reported either on the next notice that passes
/// after the window (<see cref="TryPass"/>'s <c>suppressed</c>) or, when no such notice comes, by <see cref="TakeExpired"/> from the
/// world tick, so a held count is never lost. A cooldown of 0 passes everything. Thread-safe: one lock around a few dictionary
/// operations. At most <see cref="MaxKeys"/> keys are tracked; beyond that, new keys share one overflow key, so a flood of distinct
/// accounts is still bounded.
/// </summary>
public sealed class StaffNoticeLimiter(Func<long> nowMs)
{
    /// <summary>The most keys tracked at once; new keys beyond it share <see cref="OverflowKey"/>.</summary>
    public const int MaxKeys = 4096;

    /// <summary>The shared key of notices that arrive while <see cref="MaxKeys"/> keys are already tracked.</summary>
    public const string OverflowKey = "\0overflow";

    private readonly Func<long> _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>A limiter on the monotonic clock of <paramref name="time"/>.</summary>
    public static StaffNoticeLimiter On(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new StaffNoticeLimiter(() => (long)(time.GetTimestamp() * (1000.0 / time.TimestampFrequency)));
    }

    /// <summary>Keys tracked now (tests, diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Whether a notice for <paramref name="key"/> may be sent now. True opens a new window and gives the count held back in the
    /// previous one (not yet reported by <see cref="TakeExpired"/>); false counts this notice as held back.
    /// </summary>
    public bool TryPass(string key, string label, int cooldownSeconds, out int suppressed)
    {
        ArgumentNullException.ThrowIfNull(key);
        suppressed = 0;
        if (cooldownSeconds <= 0)
        {
            return true;
        }

        long now = _nowMs();
        long window = cooldownSeconds * 1000L;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                if (now - entry.StartMs < entry.WindowMs)
                {
                    entry.Suppressed++;
                    return false;
                }

                suppressed = entry.Suppressed;
                entry.Restart(now, window, label);
                return true;
            }

            if (_entries.Count >= MaxKeys)
            {
                RemoveQuietExpired(now);
            }

            if (_entries.Count >= MaxKeys)
            {
                // Every tracked key is busy: this key shares the overflow window instead of growing the table.
                if (!_entries.TryGetValue(OverflowKey, out Entry? overflow))
                {
                    _entries[OverflowKey] = new Entry(now, window, "other accounts") { Suppressed = 1 };
                }
                else
                {
                    overflow.Suppressed++;
                }

                return false;
            }

            _entries[key] = new Entry(now, window, label);
            return true;
        }
    }

    /// <summary>
    /// Forget every key whose window has ended and return those that held notices back, once each (the world tick sends them as one
    /// summary line per key).
    /// </summary>
    public IReadOnlyList<SuppressedNotices> TakeExpired()
    {
        long now = _nowMs();
        List<SuppressedNotices>? expired = null;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return [];
            }

            List<string>? done = null;
            foreach ((string key, Entry entry) in _entries)
            {
                if (now - entry.StartMs < entry.WindowMs)
                {
                    continue;
                }

                (done ??= []).Add(key);
                if (entry.Suppressed > 0)
                {
                    (expired ??= []).Add(new SuppressedNotices(entry.Label, entry.Suppressed, (int)(entry.WindowMs / 1000)));
                }
            }

            if (done is not null)
            {
                foreach (string key in done)
                {
                    _entries.Remove(key);
                }
            }
        }

        return expired ?? (IReadOnlyList<SuppressedNotices>)[];
    }

    private void RemoveQuietExpired(long now)
    {
        foreach (string key in _entries.Where(p => p.Value.Suppressed == 0 && now - p.Value.StartMs >= p.Value.WindowMs).Select(p => p.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }

    /// <summary>The suffix for a passed notice that follows held-back ones: empty for none.</summary>
    public static string SuffixFor(int suppressed, int cooldownSeconds)
        => suppressed > 0 ? $"; {suppressed} more suppressed in the last {cooldownSeconds} s" : string.Empty;

    /// <summary>The standalone summary line body for an expired key.</summary>
    public static string SummaryFor(SuppressedNotices notices)
        => $"{notices.Label}: {notices.Suppressed} more suppressed in the last {notices.WindowSeconds} s";

    private sealed class Entry(long startMs, long windowMs, string label)
    {
        public long StartMs { get; private set; } = startMs;

        public long WindowMs { get; private set; } = windowMs;

        public string Label { get; private set; } = label;

        public int Suppressed { get; set; }

        public void Restart(long now, long windowMs, string label)
        {
            StartMs = now;
            WindowMs = windowMs;
            Label = label;
            Suppressed = 0;
        }
    }
}
