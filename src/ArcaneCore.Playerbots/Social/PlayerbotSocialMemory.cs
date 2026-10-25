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
    Apologized,
    NinjaLoot,
    KillSteal,
    AbandonedRun,
    DiedFirstInWipe,

    /// <summary>A bot's unprompted opening got no answer: affinity -1 and no familiarity (<c>PlayerbotInitiativeLane</c>).</summary>
    OpeningIgnored,
}

/// <summary>
/// Each managed bot's bounded memory of the characters it has dealt with. Thread-safe (one lock; every call is a few dictionary operations).
/// Familiarity and affinity decay lazily: an entry keeps exact values as of its decay anchor (persisted with the row, see <see cref="PlayerbotRelation.DecayAnchorUnixMs"/>),
/// a read halves them per <see cref="PlayerbotSocialMemoryOptions.FamiliarityHalfLifeDays"/> (and the affinity half-lives) for the whole days since the anchor without
/// changing the entry, and a write folds those days in and moves the anchor by exactly them. <see cref="Snapshot"/> returns the stored rows (what
/// <see cref="Load"/> expects back); <see cref="TryGet"/> and <see cref="Top"/> return the effective ones.
/// </summary>
internal sealed partial class PlayerbotSocialMemory
{
    private const long DayMs = 86_400_000L;
    private const int ProtectedAffinity = 50;

    private readonly PlayerbotSocialMemoryOptions _options;
    private readonly PlayerbotWarModeOptions _war;
    private readonly Func<long> _clock;
    private readonly Microsoft.Extensions.Logging.ILogger? _log;
    private bool _ceilingLogged;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BotMemory> _bots = [];
    private long _versionSeq;

    public PlayerbotSocialMemory(PlayerbotSocialMemoryOptions options, Func<long> clock, PlayerbotWarModeOptions? warMode = null, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        _log = log;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _war = warMode ?? new PlayerbotWarModeOptions();
        _clock = clock;
    }

    internal bool Enabled => _options.Enabled;

    /// <summary>The memory's own clock, in unix ms (what every age and decay here is measured against).</summary>
    internal long NowMs => _clock();

    /// <summary>
    /// Record one event. No-op when disabled or when the bot and the other are the same character (see <see cref="SetSelf"/>).
    /// <paramref name="otherFaction"/> marks the other as being of the opposing team.
    /// </summary>
    public void Note(Guid botId, int characterId, ulong other, bool otherIsBot, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!_options.Enabled || botId == Guid.Empty) return;
        long now = _clock();
        long day = now / DayMs;
        lock (_gate)
        {
            BotMemory bot = BotFor(botId);
            if (characterId > 0) bot.CharacterId = characterId;
            if (other == bot.SelfGuid) return;
            bool warOn = _war.Enabled;
            bool normalGrudge = false, warGrudge = false;
            if (!bot.Relations.TryGetValue(other, out Entry? entry))
            {
                // An apology means something only to a bot that holds something against the apologiser.
                if (e == PlayerbotSocialEvent.Apologized) return;
                entry = new Entry { OtherGuid = other, TrustPermille = 500, LastSeen = now, Anchor = now, WarAnchor = now, LastExchange = long.MinValue };
                bot.Relations[other] = entry;
            }
            else
            {
                // The normal rules evicted this relation and only the war layer kept it: to them it is a stranger, so a normal event starts it afresh.
                if (entry.WarOnly && e != PlayerbotSocialEvent.Apologized) Revive(entry, now);
                if (e == PlayerbotSocialEvent.Apologized)
                {
                    normalGrudge = !entry.WarOnly && EffectiveAffinity(entry, now) < 0;
                    warGrudge = warOn && EffectiveWarAffinity(entry, now) < 0;
                    if (!normalGrudge && !warGrudge) return;
                    if (!normalGrudge)
                    {
                        // The normal layer is a function of its own state alone: a grudge only the war layer holds leaves it (and LastSeen) untouched.
                        FoldWar(entry, now);
                        ApplyWar(entry, e, _options.AffinityPerApology, entry.Affinity, warGrudge, day);
                        bot.Version = ++_versionSeq;
                        return;
                    }
                }

                Decay(entry, now);
            }

            entry.OtherIsBot = otherIsBot;

            bool shared = e is PlayerbotSocialEvent.ChatExchange or PlayerbotSocialEvent.FriendlyEmote or PlayerbotSocialEvent.RudeEmote;
            bool cooled = !shared || entry.LastExchange == long.MinValue || now - entry.LastExchange >= _options.ChatFamiliarityCooldownMinutes * 60_000L;
            if (cooled && shared) entry.LastExchange = now;

            if (cooled && e != PlayerbotSocialEvent.OpeningIgnored) entry.Familiarity = Math.Min(entry.Familiarity + 1, int.MaxValue - 1);
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
                PlayerbotSocialEvent.Apologized => _options.AffinityPerApology,
                PlayerbotSocialEvent.NinjaLoot => _options.AffinityPerNinjaLoot,
                PlayerbotSocialEvent.KillSteal => _options.AffinityPerKillSteal,
                PlayerbotSocialEvent.AbandonedRun => _options.AffinityPerAbandonedRun,
                PlayerbotSocialEvent.OpeningIgnored => -1,
                _ => 0,
            };
            double before = entry.Affinity;
            entry.Affinity = Math.Clamp(entry.Affinity + AdjustNormal(entry, e, delta, normalGrudge, day), -100, 100);
            // The war layer exists only for the war rules: it is fed from the same event while War Mode is on, and never touches the normal fields above.
            if (warOn)
            {
                FoldWar(entry, now);
                ApplyWar(entry, e, delta, before, warGrudge, day);
            }
            int trust = e switch
            {
                PlayerbotSocialEvent.GroupSucceeded => _options.TrustPerGroupSuccess,
                PlayerbotSocialEvent.DiedFirstInWipe => _options.TrustPerFirstDeathInWipe,
                _ => 0,
            };
            if (trust != 0) entry.TrustPermille = (short)Math.Clamp(entry.TrustPermille + trust, 0, 1000);
            entry.LastSeen = now;
            if (e is PlayerbotSocialEvent.Grouped or PlayerbotSocialEvent.GroupSucceeded or PlayerbotSocialEvent.GroupWiped)
                entry.Flags |= PlayerbotRelationFlags.Grouped;
            // Something the other side did toward the bot (the bot's own HelpedThem or a rude word is not an overture).
            if (e is PlayerbotSocialEvent.ChatExchange or PlayerbotSocialEvent.TradeCompleted or PlayerbotSocialEvent.HelpedBy or PlayerbotSocialEvent.FriendlyEmote
                or PlayerbotSocialEvent.Apologized or PlayerbotSocialEvent.Grouped or PlayerbotSocialEvent.GroupSucceeded or PlayerbotSocialEvent.GroupWiped)
                entry.Flags |= PlayerbotRelationFlags.Interacted;
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

    // SetFlags, SetRole and SetTopic act on the relations a reader can see (Visible): a war-only row is one only while War Mode is on, exactly as TryGet and Top
    // show it, so what is set can be read back. The normal layer stays pure: these fields are reset on a war-only row when it is made (BecomeWarOnly) and when a
    // normal event revives it (Revive), so nothing set on a war-only row ever reaches the normal view.

    /// <summary>Replace the preference and etiquette flags of an existing relation (the Grouped, OtherFaction and Rival flags are kept as they are). A war-only relation takes it only while War Mode is on.</summary>
    public void SetFlags(Guid botId, ulong other, PlayerbotRelationFlags flags)
    {
        if (!_options.Enabled) return;
        const PlayerbotRelationFlags managed = PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.OtherFaction | PlayerbotRelationFlags.Rival | PlayerbotRelationFlags.Interacted;
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || !bot.Relations.TryGetValue(other, out Entry? entry) || !Visible(entry)) return;
            PlayerbotRelationFlags next = (entry.Flags & managed) | (flags & ~managed);
            if (next == entry.Flags) return;
            entry.Flags = next;
            bot.Version = ++_versionSeq;
        }
    }

    /// <summary>The affinity every decision reads: the war layer while War Mode is on, the normal layer otherwise.</summary>
    public int EffectiveAffinity(PlayerbotRelation relation) => _war.Enabled ? relation.WarAffinity : relation.Affinity;

    /// <summary>Whether every decision treats the other as a rival: the war layer's flag while War Mode is on, the normal flag otherwise.</summary>
    public bool IsRival(PlayerbotRelation relation)
        => ((_war.Enabled ? relation.WarFlags : relation.Flags) & PlayerbotRelationFlags.Rival) != 0;

    /// <summary>
    /// Wipe every war layer and delete the war-only relations of every bot in memory, running or not (the checkpoint does it after War Mode is switched off
    /// with <c>ClearOnDisable</c>); the normal layer is untouched. True if anything changed; <paramref name="changed"/> receives each bot it changed.
    /// </summary>
    public bool ClearWarLayers(ICollection<Guid>? changed = null)
    {
        bool any = false;
        lock (_gate)
            foreach ((Guid botId, BotMemory bot) in _bots)
            {
                bool botChanged = false;
                foreach (Entry warOnly in bot.Relations.Values.Where(entry => entry.WarOnly).ToArray())
                {
                    bot.Relations.Remove(warOnly.OtherGuid);
                    botChanged = true;
                }

                foreach (Entry entry in bot.Relations.Values)
                {
                    if (entry.WarAffinity == 0 && entry.WarFlags == PlayerbotRelationFlags.None) continue;
                    entry.WarAffinity = 0;
                    entry.WarFlags = PlayerbotRelationFlags.None;
                    entry.WarApologyDay = entry.WarGainDay = long.MinValue;
                    entry.WarGained = 0;
                    botChanged = true;
                }

                if (!botChanged) continue;
                bot.Version = ++_versionSeq;
                changed?.Add(botId);
                any = true;
            }

        return any;
    }

    /// <summary>Replace the role the other was seen to play (PrefersTank, PrefersHealer, PrefersDamage) of an existing relation; any other flag is kept. Pass None to clear it.</summary>
    public void SetRole(Guid botId, ulong other, PlayerbotRelationFlags role)
    {
        if (!_options.Enabled) return;
        const PlayerbotRelationFlags roles = PlayerbotRelationFlags.PrefersTank | PlayerbotRelationFlags.PrefersHealer | PlayerbotRelationFlags.PrefersDamage;
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || !bot.Relations.TryGetValue(other, out Entry? entry) || !Visible(entry)) return;
            PlayerbotRelationFlags next = (entry.Flags & ~roles) | (role & roles);
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
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || !bot.Relations.TryGetValue(other, out Entry? entry) || !Visible(entry)) return;
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
            if (_bots.TryGetValue(botId, out BotMemory? bot) && bot.Relations.TryGetValue(other, out Entry? entry) && Visible(entry))
            {
                relation = Effective(botId, bot, entry, now);
                return true;
            }
        }
        relation = null!;
        return false;
    }

    /// <summary>The bot's <paramref name="n"/> best-known relations (effective familiarity, then <see cref="EffectiveAffinity(PlayerbotRelation)"/>, highest first).</summary>
    public IReadOnlyList<PlayerbotRelation> Top(Guid botId, int n)
    {
        if (n <= 0) return [];
        long now = _clock();
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot)) return [];
            return [.. bot.Relations.Values
                .Where(Visible)
                .Select(entry => Effective(botId, bot, entry, now))
                .OrderByDescending(r => r.Familiarity).ThenByDescending(EffectiveAffinity).ThenBy(r => r.OtherGuid)
                .Take(n)];
        }
    }

    /// <summary>
    /// Every relation of the bot a reader can see, as <see cref="TryGet"/> shows it (effective values), in no particular order. For passes
    /// that look over all of a bot's relations at once (friend and ignore lists, guild invitations); sort the result if the order matters.
    /// </summary>
    public IReadOnlyList<PlayerbotRelation> Relations(Guid botId)
    {
        long now = _clock();
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotMemory? bot) || bot.Relations.Count == 0) return [];
            var list = new List<PlayerbotRelation>(bot.Relations.Count);
            foreach (Entry entry in bot.Relations.Values)
                if (Visible(entry)) list.Add(Effective(botId, bot, entry, now));
            return list;
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
                    OtherGuid = row.OtherGuid, OtherIsBot = row.OtherIsBot,
                    // The exact values and their anchors when the row has them (no fraction of a decay is lost), else the whole ones as of the last-seen time.
                    Affinity = double.IsNaN(row.AffinityExact) ? row.Affinity : row.AffinityExact,
                    Familiarity = double.IsNaN(row.FamiliarityExact) ? row.Familiarity : row.FamiliarityExact,
                    WarAffinity = double.IsNaN(row.WarAffinityExact) ? row.WarAffinity : row.WarAffinityExact, WarFlags = row.WarFlags,
                    Anchor = row.DecayAnchorUnixMs > 0 ? row.DecayAnchorUnixMs : row.LastSeenUnixMs,
                    WarAnchor = row.WarDecayAnchorUnixMs > 0 ? row.WarDecayAnchorUnixMs : row.LastSeenUnixMs,
                    TrustPermille = row.TrustPermille, Flags = row.Flags & ~PlayerbotRelationFlags.WarOnly, WarOnly = (row.Flags & PlayerbotRelationFlags.WarOnly) != 0, LastSeen = row.LastSeenUnixMs, LastExchange = long.MinValue,
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
            return [.. bot.Relations.Values.Select(entry => ToStored(botId, bot, entry))
                .OrderBy(r => r.OtherGuid).Take(PlayerbotRelation.MaxRowsPerBot)];
        }
    }

    /// <summary>Every bot's stored rows (a feature handover carries them to the next instance).</summary>
    public PlayerbotRelation[] SnapshotAll()
    {
        lock (_gate)
            return [.. _bots.SelectMany(pair => pair.Value.Relations.Values.Select(entry => ToStored(pair.Key, pair.Value, entry)))];
    }

    /// <summary>Drop one bot's memory (it was unregistered).</summary>
    public void Forget(Guid botId)
    {
        lock (_gate) _bots.Remove(botId);
    }

    /// <summary>
    /// Drop every bot's relation toward <paramref name="other"/> (the character no longer exists) and return the bots that held one; their
    /// <see cref="Version"/> moves, so the next save writes them without it.
    /// </summary>
    public IReadOnlyList<Guid> ForgetOthers(ulong other)
    {
        List<Guid> changed = [];
        lock (_gate)
            foreach ((Guid botId, BotMemory bot) in _bots)
            {
                if (!bot.Relations.Remove(other)) continue;
                bot.Version = ++_versionSeq;
                changed.Add(botId);
            }

        return changed;
    }

    private BotMemory BotFor(Guid botId)
    {
        if (!_bots.TryGetValue(botId, out BotMemory? bot)) _bots[botId] = bot = new BotMemory();
        return bot;
    }

    private static string Cut(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];

    // Decay runs in whole days, but no time is lost: an entry keeps exact (fractional) values as of its decay anchor, a read or a write counts
    // the whole days since the anchor, and a write moves the anchor by exactly those days (never to "now"), so the part of a day not yet
    // counted carries over to the next write. The anchor is separate from LastSeen and is persisted with the exact values (a row without them
    // loads with the anchor at LastSeen). The war layer has its own anchor for the same reason: an event that only reaches the war layer
    // may not touch the normal one.
    private const double Epsilon = 1e-6;

    private double Factor(long days, int halfLifeDays)
        => Math.Pow(0.5, (double)days / Math.Max(1, halfLifeDays));

    private double FamiliarityAt(Entry entry, long at)
    {
        long days = (at - entry.Anchor) / DayMs;
        return days <= 0 || entry.Familiarity <= 0 ? entry.Familiarity : entry.Familiarity * Factor(days, _options.FamiliarityHalfLifeDays);
    }

    private int EffectiveFamiliarity(Entry entry, long now) => (int)Math.Floor(FamiliarityAt(entry, now) + Epsilon);

    // Affinity drifts toward 0; a grudge (Rival) fades on its own half-life.
    private double AffinityAt(Entry entry, long at)
    {
        long days = (at - entry.Anchor) / DayMs;
        if (days <= 0 || entry.Affinity == 0) return entry.Affinity;
        return entry.Affinity * Factor(days, entry.Flags.HasFlag(PlayerbotRelationFlags.Rival) ? _options.RivalHalfLifeDays : _options.AffinityHalfLifeDays);
    }

    private static int ToWhole(double value) => (int)Math.Truncate(value + (value < 0 ? -Epsilon : Epsilon));

    private int EffectiveAffinity(Entry entry, long now) => ToWhole(AffinityAt(entry, now));

    // The war layer drifts like the normal one, except that NeverForget keeps negative war affinity as it is; an unseeded layer is the normal one.
    // Evaluated at any instant, including one before WarAnchor (the stored frame of a row), where it grows back.
    private double WarAffinityAt(Entry entry, long at)
    {
        long days = (at - entry.WarAnchor) / DayMs;
        double affinity = entry.WarAffinity;
        if (days == 0 || affinity == 0 || (affinity < 0 && _war.NeverForget)) return affinity;
        int halfLife = entry.WarFlags.HasFlag(PlayerbotRelationFlags.Rival) ? _options.RivalHalfLifeDays : _options.AffinityHalfLifeDays;
        return Math.Clamp(affinity * Factor(days, halfLife), -100, 100);
    }

    private int EffectiveWarAffinity(Entry entry, long now)
        => (entry.WarFlags & PlayerbotRelationFlags.WarSeeded) == 0 ? EffectiveAffinity(entry, now) : ToWhole(WarAffinityAt(entry, now));

    // A write folds the whole days since the anchor in and moves the anchor by exactly those days.
    private void Decay(Entry entry, long now)
    {
        long days = (now - entry.Anchor) / DayMs;
        if (days <= 0) return;
        entry.Familiarity = FamiliarityAt(entry, now);
        entry.Affinity = AffinityAt(entry, now);
        entry.Anchor += days * DayMs;
    }

    private void FoldWar(Entry entry, long now)
    {
        if ((entry.WarFlags & PlayerbotRelationFlags.WarSeeded) == 0) return;
        long days = (now - entry.WarAnchor) / DayMs;
        if (days <= 0) return;
        entry.WarAffinity = WarAffinityAt(entry, now);
        entry.WarAnchor += days * DayMs;
    }

    // The normal rules: the daily cap drops gains beyond it and one apology counts per pair per day. Returns the delta to apply.
    private int AdjustNormal(Entry entry, PlayerbotSocialEvent e, int delta, bool grudge, long day)
    {
        if (e == PlayerbotSocialEvent.Apologized)
        {
            if (!grudge || entry.ApologyDay == day) return 0;
            entry.ApologyDay = day;
        }

        if (delta > 0 && _options.MaxDailyAffinityGainPerPair > 0)
        {
            if (entry.GainDay != day)
            {
                entry.GainDay = day;
                entry.Gained = 0;
            }

            delta = Math.Min(delta, Math.Max(0, _options.MaxDailyAffinityGainPerPair - entry.Gained));
            entry.Gained += delta;
        }

        return delta;
    }

    // The war rules: negative changes are multiplied, RemoveCaps lifts the daily cap and the apology limit (war counters otherwise), and the war
    // Rival flag follows the war affinity. A layer not yet seeded starts from the normal affinity the event found.
    private void ApplyWar(Entry entry, PlayerbotSocialEvent e, int delta, double normalBefore, bool grudge, long day)
    {
        if ((entry.WarFlags & PlayerbotRelationFlags.WarSeeded) == 0)
        {
            entry.WarAffinity = normalBefore;
            entry.WarAnchor = entry.Anchor;
            entry.WarFlags |= PlayerbotRelationFlags.WarSeeded;
            grudge = normalBefore < 0;
        }

        bool lifted = _war.RemoveCaps;
        if (e == PlayerbotSocialEvent.Apologized)
        {
            if (!grudge || (!lifted && entry.WarApologyDay == day)) delta = 0;
            else entry.WarApologyDay = day;
        }

        if (delta < 0)
            delta = (int)Math.Clamp(Math.Round(delta * (double)_war.NegativeMultiplier, MidpointRounding.AwayFromZero), -200, 0);
        else if (delta > 0 && !lifted && _options.MaxDailyAffinityGainPerPair > 0)
        {
            if (entry.WarGainDay != day)
            {
                entry.WarGainDay = day;
                entry.WarGained = 0;
            }

            delta = Math.Min(delta, Math.Max(0, _options.MaxDailyAffinityGainPerPair - entry.WarGained));
            entry.WarGained += delta;
        }

        entry.WarAffinity = Math.Clamp(entry.WarAffinity + delta, -100, 100);
        entry.WarFlags = ToWhole(entry.WarAffinity) <= _options.RivalAffinity ? entry.WarFlags | PlayerbotRelationFlags.Rival : entry.WarFlags & ~PlayerbotRelationFlags.Rival;
    }

    private void ApplyRival(Entry entry)
        => entry.Flags = ToWhole(entry.Affinity) <= _options.RivalAffinity ? entry.Flags | PlayerbotRelationFlags.Rival : entry.Flags & ~PlayerbotRelationFlags.Rival;

    // Over the limit: drop the lowest (familiarity, then oldest last-seen) entry that is not a strong bond or grudge, and not the entry
    // just written unless nothing else can go. The normal rules alone decide, and see no war-only row, so the normal layer is the same with or
    // without War Mode. What they drop and War Mode (on, RemoveCaps) holds a grudge against stays as a war-only row; the rows of one bot are
    // bounded by four times the limit, the oldest war-only row going first.
    private void Evict(BotMemory bot, Entry touched, long now)
    {
        int max = Math.Clamp(_options.MaxRelationsPerBot, 1, PlayerbotRelation.MaxRowsPerBot / 4);
        if (bot.Relations.Count <= max) return;
        int normal = 0;
        foreach (Entry entry in bot.Relations.Values)
            if (!entry.WarOnly) normal++;
        while (normal > max)
        {
            Entry victim = Lowest(bot, touched, now, protectStrong: true) ?? Lowest(bot, touched, now, protectStrong: false) ?? touched;
            if (_war.Enabled && _war.RemoveCaps && WarWantsToKeep(victim, now)) BecomeWarOnly(victim, now);
            else bot.Relations.Remove(victim.OtherGuid);
            normal--;
        }

        int ceiling = max * 4;
        while (bot.Relations.Count > ceiling)
        {
            Entry? oldest = null;
            foreach (Entry entry in bot.Relations.Values)
                if (entry.WarOnly && (oldest is null || entry.LastSeen < oldest.LastSeen || entry.LastSeen == oldest.LastSeen && entry.OtherGuid < oldest.OtherGuid))
                    oldest = entry;
            if (oldest is null) break;
            bot.Relations.Remove(oldest.OtherGuid);
            if (_ceilingLogged) continue;
            _ceilingLogged = true;
            if (_log is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_log,
                    "Playerbot social memory: a bot reached {Ceiling} relations (four times MaxRelationsPerBot); the oldest war-only relations are dropped.", ceiling);
        }
    }

    // A strong bond or grudge is kept by the normal rules (the war layer is not consulted: a normal decision never depends on War Mode).
    private bool IsProtected(Entry entry, long now) => Math.Abs(EffectiveAffinity(entry, now)) >= ProtectedAffinity;

    private bool WarWantsToKeep(Entry entry, long now)
    {
        int war = EffectiveWarAffinity(entry, now);
        return war < 0 || war <= _options.RivalAffinity;
    }

    // The relation leaves the normal layer but stays for the war layer: the war layer is seeded first (it may still read the normal affinity),
    // then every normal field goes back to a brand-new relation's.
    private void BecomeWarOnly(Entry entry, long now)
    {
        if ((entry.WarFlags & PlayerbotRelationFlags.WarSeeded) == 0)
        {
            entry.WarAffinity = AffinityAt(entry, now);
            entry.WarAnchor = now;
            entry.WarFlags |= PlayerbotRelationFlags.WarSeeded;
            if (ToWhole(entry.WarAffinity) <= _options.RivalAffinity) entry.WarFlags |= PlayerbotRelationFlags.Rival;
        }
        else FoldWar(entry, now);

        ResetNormal(entry, now);
        entry.WarOnly = true;
    }

    private static void Revive(Entry entry, long now)
    {
        ResetNormal(entry, now);
        entry.WarOnly = false;
    }

    private static void ResetNormal(Entry entry, long now)
    {
        entry.Affinity = 0;
        entry.Familiarity = 0;
        entry.TrustPermille = 500;
        entry.Flags = PlayerbotRelationFlags.None;
        entry.Anchor = now;
        entry.LastExchange = long.MinValue;
        entry.ApologyDay = entry.GainDay = long.MinValue;
        entry.Gained = 0;
        entry.LastTopic = entry.Summary = string.Empty;
    }

    // A war-only relation is absent for the normal layer; a war read (War Mode on) sees it.
    private bool Visible(Entry entry) => !entry.WarOnly || _war.Enabled;

    private Entry? Lowest(BotMemory bot, Entry touched, long now, bool protectStrong)
    {
        Entry? best = null;
        int bestFamiliarity = 0;
        foreach (Entry entry in bot.Relations.Values)
        {
            if (entry.WarOnly || ReferenceEquals(entry, touched) || (protectStrong && IsProtected(entry, now))) continue;
            int familiarity = EffectiveFamiliarity(entry, now);
            // The guid breaks a full tie, so the choice does not depend on the order the rows happen to be stored in.
            if (best is null || familiarity < bestFamiliarity || (familiarity == bestFamiliarity
                && (entry.LastSeen < best.LastSeen || entry.LastSeen == best.LastSeen && entry.OtherGuid < best.OtherGuid)))
            {
                best = entry;
                bestFamiliarity = familiarity;
            }
        }
        return best;
    }

    // The values a reader sees: familiarity and affinity as decayed now, and the Rival flag as that affinity implies.
    private PlayerbotRelation Effective(Guid botId, BotMemory bot, Entry entry, long now)
    {
        int affinity = EffectiveAffinity(entry, now);
        PlayerbotRelationFlags flags = affinity <= _options.RivalAffinity ? entry.Flags | PlayerbotRelationFlags.Rival : entry.Flags & ~PlayerbotRelationFlags.Rival;
        int war = EffectiveWarAffinity(entry, now);
        PlayerbotRelationFlags warFlags = (entry.WarFlags & PlayerbotRelationFlags.WarSeeded)
            | (war <= _options.RivalAffinity ? PlayerbotRelationFlags.Rival : PlayerbotRelationFlags.None);
        return ToRecord(botId, bot, entry, EffectiveFamiliarity(entry, now), affinity, flags, war, warFlags);
    }

    // The row as stored: the whole values as of LastSeen (what readers of the row see), and the exact values with their anchors, which
    // Load takes back unchanged.
    private PlayerbotRelation ToStored(Guid botId, BotMemory bot, Entry entry)
        => ToRecord(botId, bot, entry, (int)Math.Floor(FamiliarityAt(entry, entry.LastSeen) + Epsilon), ToWhole(AffinityAt(entry, entry.LastSeen)),
            entry.WarOnly ? entry.Flags | PlayerbotRelationFlags.WarOnly : entry.Flags,
            (entry.WarFlags & PlayerbotRelationFlags.WarSeeded) == 0 ? 0 : ToWhole(WarAffinityAt(entry, entry.LastSeen)), entry.WarFlags)
            with
            {
                FamiliarityExact = entry.Familiarity, AffinityExact = entry.Affinity, WarAffinityExact = entry.WarAffinity,
                DecayAnchorUnixMs = entry.Anchor, WarDecayAnchorUnixMs = entry.WarAnchor,
            };

    private static PlayerbotRelation ToRecord(Guid botId, BotMemory bot, Entry entry, int familiarity, int affinity, PlayerbotRelationFlags flags, int warAffinity, PlayerbotRelationFlags warFlags)
        => new(botId, bot.CharacterId, entry.OtherGuid, entry.OtherIsBot, (short)affinity, familiarity, entry.TrustPermille,
            flags, entry.LastSeen, entry.LastTopic, entry.Summary, (short)warAffinity, warFlags);

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
        public double Affinity;
        public double Familiarity;
        public short TrustPermille;
        public PlayerbotRelationFlags Flags;
        public bool WarOnly;
        public long LastSeen;
        public long LastExchange;
        public long ApologyDay = long.MinValue;
        public long GainDay = long.MinValue;
        public double WarAffinity;
        public long Anchor;
        public long WarAnchor;
        public PlayerbotRelationFlags WarFlags;
        public long WarApologyDay = long.MinValue;
        public long WarGainDay = long.MinValue;
        public int WarGained;
        public int Gained;
        public string LastTopic = string.Empty;
        public string Summary = string.Empty;
    }
}
