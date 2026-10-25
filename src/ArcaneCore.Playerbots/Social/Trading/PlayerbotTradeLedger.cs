using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Each managed bot's trade ledger (P4 S3, design 4.4): per partner, the fair deals and scams counted, the last scam and ninja times and the last UTC day a gift counted.
/// Counts and times only. The scam and ninja windows (<see cref="PlayerbotSocialTradingOptions.ScamMemoryDays"/>, <see cref="PlayerbotSocialTradingOptions.NinjaMemoryDays"/>)
/// are applied when a row is read, from the live options: the stored times never change with them. With War Mode on and <see cref="PlayerbotWarModeOptions.NeverForget"/>
/// the windows are ignored at read time (a scam or ninja loot is held against the partner for good) and switching War Mode off restores them; the ledger itself is normal-only
/// and War Mode has no layer of its own in it. A bot holds at most <see cref="PlayerbotTradeRecord.MaxRowsPerBot"/> partners: the one that makes room is a partner with no scam or ninja
/// inside its window and the fewest fair deals (then the least recently written), so a row that still matters is kept. Thread-safe (one lock; every call is a few dictionary
/// operations, and only a full bot scans its rows, once per new partner). It is fed by the social recorder whether or not <c>Social:Trading:Enabled</c> or <c>Social:Memory:Enabled</c> is on
/// (counts and times only; the switches gate what the bots do with them, not what is counted). <see cref="Snapshot"/> returns the stored rows (what <see cref="Load"/> expects back).
/// </summary>
internal sealed class PlayerbotTradeLedger(PlayerbotSocialTradingOptions options, PlayerbotWarModeOptions war, Func<long> clock)
{
    private const long DayMs = 86_400_000L;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, BotLedger> _bots = [];
    private long _versionSeq;
    private long _touchSeq;

    /// <summary>One more fair deal with <paramref name="other"/> (a completed deal at the agreed terms).</summary>
    public void NoteFairDeal(Guid botId, int characterId, ulong other)
        => Write(botId, characterId, other, static (entry, _) =>
        {
            if (entry.FairDeals < int.MaxValue) entry.FairDeals++;
            return true;
        });

    /// <summary>A scam by <paramref name="other"/>: counted and timed now. It is stored even when the window is 0 days (a 0 reads as forgotten at once).</summary>
    public void NoteScam(Guid botId, int characterId, ulong other)
        => Write(botId, characterId, other, static (entry, now) =>
        {
            if (entry.Scams < int.MaxValue) entry.Scams++;
            entry.LastScamUnixMs = now;
            return true;
        });

    /// <summary>A ninja loot by <paramref name="other"/>: timed now.</summary>
    public void NoteNinja(Guid botId, int characterId, ulong other)
        => Write(botId, characterId, other, static (entry, now) =>
        {
            entry.LastNinjaUnixMs = now;
            return true;
        });

    /// <summary>True the first time for this pair in the current UTC day (the gift counts and the day is stamped), false after (nothing changes).</summary>
    public bool NoteGiftReceived(Guid botId, int characterId, ulong other)
        => Write(botId, characterId, other, static (entry, now) =>
        {
            long day = now / DayMs;
            if (entry.LastGiftDay >= day) return false;
            entry.LastGiftDay = (int)Math.Min(day, int.MaxValue);
            return true;
        });

    public bool TryGet(Guid botId, ulong other, out PlayerbotTradeRecord record)
    {
        lock (_gate)
        {
            if (_bots.TryGetValue(botId, out BotLedger? bot) && bot.Rows.TryGetValue(other, out Entry? entry))
            {
                record = ToRecord(botId, bot, other, entry);
                return true;
            }
        }

        record = null!;
        return false;
    }

    /// <summary>A scam within ScamMemoryDays; with War Mode on and NeverForget the window is ignored at read time.</summary>
    public bool IsScammer(Guid botId, ulong other)
    {
        long now = clock();
        lock (_gate) return _bots.TryGetValue(botId, out BotLedger? bot) && bot.Rows.TryGetValue(other, out Entry? entry) && Scammer(entry, now);
    }

    /// <summary>A ninja loot within NinjaMemoryDays; the same War Mode read rule.</summary>
    public bool IsNinja(Guid botId, ulong other)
    {
        long now = clock();
        lock (_gate) return _bots.TryGetValue(botId, out BotLedger? bot) && bot.Rows.TryGetValue(other, out Entry? entry) && Ninja(entry, now);
    }

    /// <summary>Load stored rows (startup or a feature handover). Does not change any bot's <see cref="Version"/>, so a loaded bot is not saved straight back.</summary>
    public void Load(IEnumerable<PlayerbotTradeRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
            foreach (PlayerbotTradeRecord row in rows)
            {
                if (row is null || row.BotId == Guid.Empty) continue;
                BotLedger bot = BotFor(row.BotId);
                if (row.CharacterId > 0) bot.CharacterId = row.CharacterId;
                if (!bot.Rows.ContainsKey(row.OtherGuid) && bot.Rows.Count >= PlayerbotTradeRecord.MaxRowsPerBot) continue;
                bot.Rows[row.OtherGuid] = new Entry
                {
                    FairDeals = Math.Max(0, row.FairDeals), Scams = Math.Max(0, row.Scams),
                    LastScamUnixMs = Math.Max(0, row.LastScamUnixMs), LastNinjaUnixMs = Math.Max(0, row.LastNinjaUnixMs),
                    LastGiftDay = Math.Max(0, row.LastGiftDay),
                };
            }
    }

    /// <summary>The bot's rows as stored, ordered by partner: what the saver writes and <see cref="Load"/> reads back.</summary>
    public IReadOnlyList<PlayerbotTradeRecord> Snapshot(Guid botId)
    {
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotLedger? bot)) return [];
            return [.. bot.Rows.OrderBy(pair => pair.Key).Select(pair => ToRecord(botId, bot, pair.Key, pair.Value))];
        }
    }

    /// <summary>Every bot's stored rows (a feature handover carries them to the next instance).</summary>
    public PlayerbotTradeRecord[] SnapshotAll()
    {
        lock (_gate)
            return [.. _bots.SelectMany(pair => pair.Value.Rows.Select(row => ToRecord(pair.Key, pair.Value, row.Key, row.Value)))];
    }

    /// <summary>A counter that changes whenever the bot's rows change (0 for a bot with no ledger); never repeats within a run.</summary>
    public long Version(Guid botId)
    {
        lock (_gate) return _bots.TryGetValue(botId, out BotLedger? bot) ? bot.Version : 0;
    }

    /// <summary>Drop one bot's ledger (it was unregistered).</summary>
    public void Forget(Guid botId)
    {
        lock (_gate) _bots.Remove(botId);
    }

    /// <summary>
    /// Drop every bot's row about <paramref name="other"/> (the character no longer exists) and return the bots that held one; their <see cref="Version"/> moves,
    /// so the next save writes them without it.
    /// </summary>
    public IReadOnlyList<Guid> ForgetOthers(ulong other)
    {
        List<Guid> changed = [];
        lock (_gate)
            foreach ((Guid botId, BotLedger bot) in _bots)
            {
                if (!bot.Rows.Remove(other)) continue;
                bot.Version = ++_versionSeq;
                changed.Add(botId);
            }

        return changed;
    }

    private bool Write(Guid botId, int characterId, ulong other, Func<Entry, long, bool> change)
    {
        if (botId == Guid.Empty) return false;
        long now = clock();
        lock (_gate)
        {
            BotLedger bot = BotFor(botId);
            if (characterId > 0) bot.CharacterId = characterId;
            bool created = !bot.Rows.TryGetValue(other, out Entry? entry);
            if (created)
            {
                if (bot.Rows.Count >= PlayerbotTradeRecord.MaxRowsPerBot) MakeRoom(bot, now);
                entry = new Entry();
            }

            if (!change(entry!, now)) return false;
            if (created) bot.Rows[other] = entry!;
            entry!.Touched = ++_touchSeq;
            bot.Version = ++_versionSeq;
            return true;
        }
    }

    // A full bot: drop the partner that matters least. A partner inside a scam or ninja window goes last (only when every row is one).
    private void MakeRoom(BotLedger bot, long now)
    {
        ulong victim = 0;
        Entry? best = null;
        bool bestProtected = true;
        foreach ((ulong guid, Entry entry) in bot.Rows)
        {
            bool isProtected = Scammer(entry, now) || Ninja(entry, now);
            if (best is not null && (isProtected, entry.FairDeals, entry.Touched).CompareTo((bestProtected, best.FairDeals, best.Touched)) >= 0) continue;
            victim = guid;
            best = entry;
            bestProtected = isProtected;
        }

        if (best is not null) bot.Rows.Remove(victim);
    }

    private bool Scammer(Entry entry, long now) => entry.LastScamUnixMs > 0 && InWindow(entry.LastScamUnixMs, options.ScamMemoryDays, now);

    private bool Ninja(Entry entry, long now) => entry.LastNinjaUnixMs > 0 && InWindow(entry.LastNinjaUnixMs, options.NinjaMemoryDays, now);

    // War Mode with NeverForget holds it against the partner for good; otherwise the time since must be inside the days (0 days: forgotten at once).
    private bool InWindow(long at, int days, long now)
        => war.Enabled && war.NeverForget || days > 0 && now - at < days * DayMs;

    private BotLedger BotFor(Guid botId)
    {
        if (!_bots.TryGetValue(botId, out BotLedger? bot)) _bots[botId] = bot = new BotLedger();
        return bot;
    }

    private static PlayerbotTradeRecord ToRecord(Guid botId, BotLedger bot, ulong other, Entry entry)
        => new(botId, bot.CharacterId, other, entry.FairDeals, entry.Scams, entry.LastScamUnixMs, entry.LastNinjaUnixMs, entry.LastGiftDay);

    private sealed class BotLedger
    {
        public int CharacterId;
        public long Version;
        public readonly Dictionary<ulong, Entry> Rows = [];
    }

    private sealed class Entry
    {
        public int FairDeals;
        public int Scams;
        public long LastScamUnixMs;
        public long LastNinjaUnixMs;
        public int LastGiftDay;
        public long Touched;
    }
}
