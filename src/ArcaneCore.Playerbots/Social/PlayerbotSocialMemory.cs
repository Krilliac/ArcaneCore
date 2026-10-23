using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What happened between a managed bot and another character (append-only).</summary>
internal enum PlayerbotSocialEvent
{
    ChatExchange,
    Grouped,
    GroupSucceeded,
    GroupWiped,
    Kicked,
    TradeCompleted,
    HelpedThem,
    HelpedBy,
    ChatStrike,
    KilledBy,
    KilledThem,
    FriendlyEmote,
    RudeEmote,
    WatchedMeDie,
}

/// <summary>
/// Each managed bot's bounded memory of the characters it has dealt with. Thread-safe (one lock; every call is a few dictionary operations).
/// Familiarity decays lazily: a stored familiarity is the value at <see cref="PlayerbotRelation.LastSeenUnixMs"/> and reads halve it per
/// <see cref="PlayerbotSocialMemoryOptions.FamiliarityHalfLifeDays"/> since then without changing the entry; a write folds the decay in
/// first. <see cref="Snapshot"/> returns the stored values (what <see cref="Load"/> expects back); <see cref="TryGet"/> and
/// <see cref="Top"/> return the effective ones.
/// </summary>
internal sealed class PlayerbotSocialMemory
{
    private const long DayMs = 86_400_000L;
    private const int ProtectedAffinity = 50;

    private readonly PlayerbotSocialMemoryOptions _options;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BotMemory> _bots = [];
    private long _versionSeq;

    public PlayerbotSocialMemory(PlayerbotSocialMemoryOptions options, Func<long> clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
    }

    internal bool Enabled => _options.Enabled;

    /// <summary>
    /// Record one event. No-op when disabled or when the bot and the other are the same character (see <see cref="SetSelf"/>).
    /// <paramref name="otherFaction"/> marks the other as being of the opposing team.
    /// </summary>
    public void Note(Guid botId, int characterId, ulong other, bool otherIsBot, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!_options.Enabled || botId == Guid.Empty) return;
        long now = _clock();
        lock (_gate)
        {
            BotMemory bot = BotFor(botId);
            if (characterId > 0) bot.CharacterId = characterId;
            if (other == bot.SelfGuid) return;
            if (!bot.Relations.TryGetValue(other, out Entry? entry))
            {
                entry = new Entry { OtherGuid = other, TrustPermille = 500, LastSeen = now, LastExchange = long.MinValue };
                bot.Relations[other] = entry;
            }
            else Decay(entry, now);
            entry.OtherIsBot = otherIsBot;

            bool shared = e is PlayerbotSocialEvent.ChatExchange or PlayerbotSocialEvent.FriendlyEmote or PlayerbotSocialEvent.RudeEmote;
            bool cooled = !shared || entry.LastExchange == long.MinValue || now - entry.LastExchange >= _options.ChatFamiliarityCooldownMinutes * 60_000L;
            if (cooled && shared) entry.LastExchange = now;

            if (cooled) entry.Familiarity = (int)Math.Min((long)entry.Familiarity + 1, int.MaxValue - 1);
            int delta = e switch
            {
                PlayerbotSocialEvent.ChatExchange => cooled ? _options.AffinityPerChat : 0,
                PlayerbotSocialEvent.Grouped => _options.AffinityPerGroup,
                PlayerbotSocialEvent.GroupSucceeded => _options.AffinityPerGroupSuccess,
                PlayerbotSocialEvent.GroupWiped => 1,
                PlayerbotSocialEvent.Kicked => _options.AffinityPerKick,
                PlayerbotSocialEvent.TradeCompleted => _options.AffinityPerTrade,
                PlayerbotSocialEvent.HelpedThem or PlayerbotSocialEvent.HelpedBy => _options.AffinityPerHelp,
                PlayerbotSocialEvent.ChatStrike => _options.AffinityPerStrike,
                PlayerbotSocialEvent.KilledBy => _options.AffinityPerKilledBy,
                PlayerbotSocialEvent.KilledThem => _options.AffinityPerKilledThem,
                // A friendly emote can be repeated at no cost, so it counts once per window; a rude one always counts.
                PlayerbotSocialEvent.FriendlyEmote => cooled ? _options.AffinityPerFriendlyEmote : 0,
                PlayerbotSocialEvent.RudeEmote => _options.AffinityPerRudeEmote,
                PlayerbotSocialEvent.WatchedMeDie => _options.AffinityPerWatchedDie,
                _ => 0,
            };
            entry.Affinity = (short)Math.Clamp(entry.Affinity + delta, -100, 100);
            entry.LastSeen = now;
            if (e is PlayerbotSocialEvent.Grouped or PlayerbotSocialEvent.GroupSucceeded or PlayerbotSocialEvent.GroupWiped)
                entry.Flags |= PlayerbotRelationFlags.Grouped;
            if (otherFaction) entry.Flags |= PlayerbotRelationFlags.OtherFaction;
            ApplyRival(entry);
            Evict(bot, entry, now);
            bot.Version = ++_versionSeq;
        }
    }

    /// <summary>Tell the memory which character guid is the bot itself (events toward it are ignored).</summary>
    public void SetSelf(Guid botId, ulong botGuid)
    {
        lock (_gate) BotFor(botId).SelfGuid = botGuid;
    }

    /// <summary>Replace the preference and etiquette flags of an existing relation (the Grouped, OtherFaction and Rival flags are kept as they are).</summary>
    public void SetFlags(Guid botId, ulong other, PlayerbotRelationFlags flags)
    {
        if (!_options.Enabled) return;
        const PlayerbotRelationFlags managed = PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.OtherFaction | PlayerbotRelationFlags.Rival;
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || !bot.Relations.TryGetValue(other, out Entry? entry)) return;
            PlayerbotRelationFlags next = (entry.Flags & managed) | (flags & ~managed);
            if (next == entry.Flags) return;
            entry.Flags = next;
            bot.Version = ++_versionSeq;
        }
    }

    /// <summary>Set the last topic key (cut to 32 characters) and the bot-side summary (cut to 160) of an existing relation.</summary>
    public void SetTopic(Guid botId, ulong other, string topicKey, string summary)
    {
        if (!_options.Enabled) return;
        string topic = Cut(topicKey, PlayerbotRelation.MaxTopicLength);
        string text = Cut(summary, PlayerbotRelation.MaxSummaryLength);
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || !bot.Relations.TryGetValue(other, out Entry? entry)) return;
            if (entry.LastTopic == topic && entry.Summary == text) return;
            entry.LastTopic = topic;
            entry.Summary = text;
            bot.Version = ++_versionSeq;
        }
    }

    public bool TryGet(Guid botId, ulong other, out PlayerbotRelation relation)
    {
        long now = _clock();
        lock (_gate)
        {
            if (_bots.TryGetValue(botId, out BotMemory? bot) && bot.Relations.TryGetValue(other, out Entry? entry))
            {
                relation = ToRecord(botId, bot, entry, EffectiveFamiliarity(entry, now));
                return true;
            }
        }
        relation = null!;
        return false;
    }

    /// <summary>The bot's <paramref name="n"/> best-known relations (effective familiarity, then affinity, highest first).</summary>
    public IReadOnlyList<PlayerbotRelation> Top(Guid botId, int n)
    {
        if (n <= 0) return [];
        long now = _clock();
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot)) return [];
            return [.. bot.Relations.Values
                .Select(entry => ToRecord(botId, bot, entry, EffectiveFamiliarity(entry, now)))
                .OrderByDescending(r => r.Familiarity).ThenByDescending(r => r.Affinity).ThenBy(r => r.OtherGuid)
                .Take(n)];
        }
    }

    /// <summary>Load stored rows (startup). Does not change any bot's <see cref="Version"/>, so a loaded bot is not saved straight back.</summary>
    public void Load(IEnumerable<PlayerbotRelation> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
        {
            foreach (PlayerbotRelation row in rows)
            {
                BotMemory bot = BotFor(row.BotId);
                if (row.CharacterId > 0) bot.CharacterId = row.CharacterId;
                bot.Relations[row.OtherGuid] = new Entry
                {
                    OtherGuid = row.OtherGuid, OtherIsBot = row.OtherIsBot, Affinity = row.Affinity, Familiarity = row.Familiarity,
                    TrustPermille = row.TrustPermille, Flags = row.Flags, LastSeen = row.LastSeenUnixMs, LastExchange = long.MinValue,
                    LastTopic = Cut(row.LastTopic, PlayerbotRelation.MaxTopicLength), Summary = Cut(row.Summary, PlayerbotRelation.MaxSummaryLength),
                };
            }
        }
    }

    /// <summary>A counter that changes whenever the bot's relations change (0 for a bot with no memory); never repeats within a run.</summary>
    public long Version(Guid botId)
    {
        lock (_gate) return _bots.TryGetValue(botId, out BotMemory? bot) ? bot.Version : 0;
    }

    /// <summary>The bot's rows as stored (familiarity as of each row's last-seen time): what the saver writes and <see cref="Load"/> reads back.</summary>
    public IReadOnlyList<PlayerbotRelation> Snapshot(Guid botId)
    {
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot)) return [];
            return [.. bot.Relations.Values.Select(entry => ToRecord(botId, bot, entry, entry.Familiarity))
                .OrderBy(r => r.OtherGuid).Take(PlayerbotRelation.MaxRowsPerBot)];
        }
    }

    /// <summary>Every bot's stored rows (a feature handover carries them to the next instance).</summary>
    public PlayerbotRelation[] SnapshotAll()
    {
        lock (_gate)
            return [.. _bots.SelectMany(pair => pair.Value.Relations.Values.Select(entry => ToRecord(pair.Key, pair.Value, entry, entry.Familiarity)))];
    }

    /// <summary>Drop one bot's memory (it was unregistered).</summary>
    public void Forget(Guid botId)
    {
        lock (_gate) _bots.Remove(botId);
    }

    private BotMemory BotFor(Guid botId)
    {
        if (!_bots.TryGetValue(botId, out BotMemory? bot)) _bots[botId] = bot = new BotMemory();
        return bot;
    }

    private static string Cut(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];

    private int EffectiveFamiliarity(Entry entry, long now)
    {
        // Whole days only: an integer familiarity floored after every small step would drain to nothing within minutes.
        long days = (now - entry.LastSeen) / DayMs;
        if (days <= 0 || entry.Familiarity <= 0) return entry.Familiarity;
        double halves = (double)days / Math.Max(1, _options.FamiliarityHalfLifeDays);
        return (int)Math.Floor(entry.Familiarity * Math.Pow(0.5, halves));
    }

    // A write folds the decay in (the caller then moves the last-seen anchor); inside a day nothing has decayed.
    private void Decay(Entry entry, long now)
    {
        if (now - entry.LastSeen < DayMs) return;
        entry.Familiarity = EffectiveFamiliarity(entry, now);
    }

    private void ApplyRival(Entry entry)
        => entry.Flags = entry.Affinity <= _options.RivalAffinity ? entry.Flags | PlayerbotRelationFlags.Rival : entry.Flags & ~PlayerbotRelationFlags.Rival;

    // Over the limit: drop the lowest (familiarity, then oldest last-seen) entry that is not a strong bond or grudge, and not the entry
    // just written unless nothing else can go.
    private void Evict(BotMemory bot, Entry touched, long now)
    {
        int max = Math.Clamp(_options.MaxRelationsPerBot, 1, PlayerbotRelation.MaxRowsPerBot);
        while (bot.Relations.Count > max)
        {
            Entry victim = Lowest(bot, touched, now, protectStrong: true) ?? Lowest(bot, touched, now, protectStrong: false) ?? touched;
            bot.Relations.Remove(victim.OtherGuid);
        }
    }

    private Entry? Lowest(BotMemory bot, Entry touched, long now, bool protectStrong)
    {
        Entry? best = null;
        int bestFamiliarity = 0;
        foreach (Entry entry in bot.Relations.Values)
        {
            if (ReferenceEquals(entry, touched) || (protectStrong && Math.Abs((int)entry.Affinity) >= ProtectedAffinity)) continue;
            int familiarity = EffectiveFamiliarity(entry, now);
            if (best is null || familiarity < bestFamiliarity || (familiarity == bestFamiliarity && entry.LastSeen < best.LastSeen))
            {
                best = entry;
                bestFamiliarity = familiarity;
            }
        }
        return best;
    }

    private static PlayerbotRelation ToRecord(Guid botId, BotMemory bot, Entry entry, int familiarity)
        => new(botId, bot.CharacterId, entry.OtherGuid, entry.OtherIsBot, entry.Affinity, familiarity, entry.TrustPermille,
            entry.Flags, entry.LastSeen, entry.LastTopic, entry.Summary);

    private sealed class BotMemory
    {
        public readonly Dictionary<ulong, Entry> Relations = [];
        public int CharacterId;
        public ulong SelfGuid = ulong.MaxValue;
        public long Version;
    }

    private sealed class Entry
    {
        public ulong OtherGuid;
        public bool OtherIsBot;
        public short Affinity;
        public int Familiarity;
        public short TrustPermille;
        public PlayerbotRelationFlags Flags;
        public long LastSeen;
        public long LastExchange;
        public string LastTopic = string.Empty;
        public string Summary = string.Empty;
    }
}
