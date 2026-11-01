using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The gifts each bot received from each partner inside a sliding hour (<c>Social:Trading:MaxGiftsReceivedPerHour</c>). One book per playerbot feature, shared by every bot's
/// <see cref="PlayerbotTradeGifts"/> (a bot with no feature has a private one), so the counts can be saved and loaded as <see cref="PlayerbotTradeCounter"/> rows
/// (<see cref="Export"/>, <see cref="Import"/>). <see cref="Version"/> changes on every write so the saver can skip an unchanged book. Memory is bounded: at most
/// <see cref="MapCap"/> (bot, partner) pairs and <see cref="PerPartnerCap"/> stamps each. Thread-safe: one lock, a few dictionary operations per call.
/// </summary>
internal sealed class PlayerbotGiftBook
{
    /// <summary>(Bot, partner) pairs the book remembers before the one written longest ago is dropped.</summary>
    internal const int MapCap = 4096;

    /// <summary>Stamps kept per pair (the hourly limit's maximum is 120).</summary>
    internal const int PerPartnerCap = 128;

    private const long HourMs = 3_600_000;

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Bot, ulong Partner), Queue<long>> _received = [];
    private long _version;

    /// <summary>Changes on every write (a note or an import).</summary>
    internal long Version => Interlocked.Read(ref _version);

    /// <summary>The gifts <paramref name="partner"/> gave <paramref name="bot"/> inside the last hour.</summary>
    internal int Within(Guid bot, ulong partner, long now)
    {
        lock (_gate)
        {
            if (!_received.TryGetValue((bot, partner), out Queue<long>? stamps)) return 0;
            while (stamps.Count > 0 && now - stamps.Peek() >= HourMs) stamps.Dequeue();
            if (stamps.Count != 0) return stamps.Count;
            _received.Remove((bot, partner));
            return 0;
        }
    }

    /// <summary>One more gift from <paramref name="partner"/> to <paramref name="bot"/> at <paramref name="now"/>.</summary>
    internal void Note(Guid bot, ulong partner, long now)
    {
        lock (_gate)
        {
            Add(bot, partner, now, now);
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>The unexpired counts as rows (one row per distinct stamp), what the saver writes and <see cref="Import"/> reads back.</summary>
    internal IReadOnlyList<PlayerbotTradeCounter> Export(long now)
    {
        List<PlayerbotTradeCounter> rows = [];
        lock (_gate)
            foreach (((Guid bot, ulong partner), Queue<long> stamps) in _received)
                foreach (IGrouping<long, long> group in stamps.Where(stamp => now - stamp < HourMs).GroupBy(stamp => stamp))
                    rows.Add(new PlayerbotTradeCounter(PlayerbotTradeCounterKind.GiftsReceived, bot, partner, group.Key, group.Count()));
        return rows;
    }

    /// <summary>Replace the book with stored <see cref="PlayerbotTradeCounterKind.GiftsReceived"/> rows (startup or a feature handover; loading twice is harmless); expired stamps and other kinds are skipped.</summary>
    internal void Import(IEnumerable<PlayerbotTradeCounter> rows, long now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
        {
            _received.Clear();
            foreach (PlayerbotTradeCounter row in rows.Where(row => row is { Kind: PlayerbotTradeCounterKind.GiftsReceived } && now - row.Stamp < HourMs).OrderBy(row => row.Stamp))
                for (long i = 0; i < Math.Min(row.Amount, PerPartnerCap); i++) Add(row.BotId, row.Key, row.Stamp, now);
            Interlocked.Increment(ref _version);
        }
    }

    private void Add(Guid bot, ulong partner, long stamp, long now)
    {
        if (!_received.TryGetValue((bot, partner), out Queue<long>? stamps))
        {
            if (_received.Count >= MapCap) DropStalest(now);
            _received[(bot, partner)] = stamps = new Queue<long>();
        }

        stamps.Enqueue(stamp);
        while (stamps.Count > PerPartnerCap) stamps.Dequeue();
    }

    // The pair whose newest stamp is the oldest goes (an expired one first); the others keep their counts.
    private void DropStalest(long now)
    {
        (Guid, ulong) victim = default;
        long victimAt = long.MaxValue;
        bool found = false;
        foreach (((Guid, ulong) key, Queue<long> stamps) in _received)
        {
            long newest = stamps.Count == 0 ? long.MinValue : stamps.Last();
            if (now - newest >= HourMs) newest = long.MinValue;
            if (newest >= victimAt && found) continue;
            victim = key;
            victimAt = newest;
            found = true;
        }

        if (found) _received.Remove(victim);
    }
}
