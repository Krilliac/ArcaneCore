using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The conversation director (<see cref="PlayerbotConversationOptions"/>): now and then two bots talk with each other for a few lines
/// (<see cref="PlayerbotConversationScripts"/>) about something real: a ding, a boss their group killed, loot, a death, food one has and
/// the other lacks, a quest, a dungeon, what each is up to. Partners are bots of one group (party chat), bots standing close together
/// (say), or guildmates (guild chat). Run from the social pass (world thread), bounded per pass:
/// <list type="bullet">
/// <item>a conversation starts with the chance <see cref="PlayerbotConversationOptions.ConversationsPerHour"/> per hour, looks at most at
/// <see cref="PlayerbotConversationOptions.CandidatesPerPass"/> bots, and only where a real player hears it (a real group member, a real
/// player in say range, a real guildmate online);</item>
/// <item>every line goes through <see cref="PlayerbotChat.TrySpeak"/> (the safety filter, the limiter, the bot-to-bot turns, the place's
/// budget and its real-listener check) and is said by the bot's own session; a line refused for a moment is tried again until its deadline,
/// one that cannot be said ends the conversation;</item>
/// <item>while it runs, the two bots do not answer each other on their own (<see cref="PlayerbotChat.ClaimExchange"/>), and a real player
/// speaking to either ends it at once, so the player's answer comes first and the bots never talk over them;</item>
/// <item>pair and bot cooldowns, at most <see cref="PlayerbotConversationOptions.MaxActive"/> at once, at most
/// <see cref="PlayerbotConversationOptions.MaxLines"/> lines (and never past the speech options' bot-to-bot turns).</item>
/// </list>
/// Quests handed in and boss kills reach the bots' chat events (<see cref="PlayerbotChatEvents"/>) through here as well.
/// </summary>
internal sealed class PlayerbotConversations
{
    /// <summary>How fresh a ding, a loot or a boss kill must be to be talked about.</summary>
    internal const long NewsMs = 5 * 60_000;

    /// <summary>How long a line may wait for its fate (said or dropped at delivery) before the conversation is given up.</summary>
    internal const long SettleMs = 20_000;

    /// <summary>A line refused for the moment (a cooldown, a budget) is tried again after this long.</summary>
    internal const long RetryMs = 3_000;

    private const int MaxNoticed = 64;
    private const long HourMs = 3_600_000;

    private readonly List<Exchange> _active = [];
    private readonly Dictionary<(ulong, ulong), long> _pairs = [];
    private readonly Dictionary<ulong, long> _bots = [];
    private readonly Queue<long> _starts = new();
    private readonly HashSet<ulong> _interrupted = [];
    private readonly Dictionary<(ulong Bot, BotChatEventKind Kind), long> _talkedAbout = [];
    private readonly ConcurrentQueue<(ulong Player, BotChatEventKind Kind, string Value, string? Where)> _noticed = new();
    private PlayerbotChat? _chat;
    private QuestNpcServices? _quests;

    /// <summary>Conversations running now (tests).</summary>
    internal int Active => _active.Count;
    /// <summary>The remarks waiting to be told (tests).</summary>
    internal IReadOnlyCollection<(ulong Player, BotChatEventKind Kind, string Value, string? Where)> Noticed => _noticed;


    /// <summary>Conversations started so far.</summary>
    internal int Started { get; private set; }

    /// <summary>Conversation lines said so far.</summary>
    internal int LinesSaid { get; private set; }

    /// <summary>Conversations a real player's line ended.</summary>
    internal int Interrupted { get; private set; }

    /// <summary>The social pass: subscribe, take the noticed events, advance running conversations, perhaps start one.</summary>
    internal void Run(PlayerbotSocialPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Chat is not { } chat)
        {
            Clear();
            return;
        }

        Attach(chat, pass);
        TakeNoticed(pass);
        PlayerbotConversationOptions options = chat.ConversationOptions;
        if (!options.Enabled || !chat.IsActive || !chat.SpeechOptions.Enabled)
        {
            Clear();
            return;
        }

        Advance(pass, chat, options);
        if (_active.Count < options.MaxActive && Roll(pass, options)) TryStart(pass, chat, options);
        Sweep(pass.NowMs);
    }

    /// <summary>Stop: release every exchange and unsubscribe.</summary>
    internal void Stop()
    {
        Clear();
        if (_chat is { } chat)
        {
            chat.LineHeard -= OnLineHeard;
            chat.SpeechSettled -= OnSpeechSettled;
        }

        if (_quests is { } quests) quests.QuestRewarded -= OnQuestRewarded;
        _chat = null;
        _quests = null;
    }

    // --- events ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A unit died (any thread that runs the map): a dungeon or raid boss, or a boss of the world-boss catalogue (<see cref="IsWorldBossEntry"/>) on a continent,
    /// killed by a player's side is news for its group's bots.
    /// </summary>
    internal void OnUnitKilled(Map map, Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || PlayerbotBossKills.PlaceOf(map, creature, IsWorldBossEntry, ZoneOf) is not { } place) return;
        if (killer is null || DuelRules.ControllingPlayer(killer) is not { } player || string.IsNullOrWhiteSpace(creature.Template.Name)) return;
        if (_noticed.Count < MaxNoticed) _noticed.Enqueue((player.Guid.Value, BotChatEventKind.BossKill, creature.Template.Name, place));
    }

    /// <summary>Whether an entry is a catalogued world boss (the group coordinator's frozen snapshot; any thread); null: no catalogue, continent kills stay silent.</summary>
    internal Func<uint, bool>? IsWorldBossEntry { get; set; }

    /// <summary>The name of the zone a creature stands in (null: not known, the map's name is used); set by the social feature.</summary>
    internal Func<Map, Creature, string?>? ZoneOf { get; set; }

    private void OnQuestRewarded(Player player, ObjectGuid questGiver, Quest quest)
    {
        if (_noticed.Count < MaxNoticed && quest.Title is { Length: > 0 } title) _noticed.Enqueue((player.Guid.Value, BotChatEventKind.QuestDone, title, null));
    }

    private void OnLineHeard(BotHeardLine line)
    {
        // A real player speaking to a bot in a conversation ends it (the bot's own answer to the player then comes first).
        if (line.SenderIsBot || !(line.AddressesBot || line.Type == ChatType.Whisper)) return;
        if (_interrupted.Count < 256) _interrupted.Add(line.BotGuid.Value);
    }

    private void OnSpeechSettled(BotSpeech speech, BotSpeechAdmission outcome)
    {
        foreach (Exchange exchange in _active)
            if (exchange.Pending is { } pending && pending.Equals(speech))
            {
                exchange.Outcome = outcome;
                return;
            }
    }

    private void Attach(PlayerbotChat chat, PlayerbotSocialPass pass)
    {
        if (!ReferenceEquals(_chat, chat))
        {
            if (_chat is { } old)
            {
                old.LineHeard -= OnLineHeard;
                old.SpeechSettled -= OnSpeechSettled;
            }

            _chat = chat;
            chat.LineHeard += OnLineHeard;
            chat.SpeechSettled += OnSpeechSettled;
        }

        if (_quests is null && QuestsOf(pass) is { } quests)
        {
            _quests = quests;
            quests.QuestRewarded += OnQuestRewarded;
        }
    }

    private static QuestNpcServices? QuestsOf(PlayerbotSocialPass pass)
    {
        try
        {
            return pass.Quests;
        }
        catch (InvalidOperationException error)
        {
            FaultLedger.Shared.Record("playerbot.social", error);
            AnomalyLedger.Shared.Report("bot-social", "conversation-fault", "social-conversations", error.Message);
            return null; // the quest feature is not attached yet
        }
    }

    private void TakeNoticed(PlayerbotSocialPass pass)
    {
        int budget = MaxNoticed;
        while (budget-- > 0 && _noticed.TryDequeue(out (ulong Player, BotChatEventKind Kind, string Value, string? Where) item))
        {
            var guid = new ObjectGuid(item.Player);
            if (item.Kind == BotChatEventKind.BossKill)
            {
                // The killer's whole group hears of the kill.
                if (pass.ByCharacter.TryGetValue(guid.Low, out PlayerbotSocialBot? killer)) killer.Party.ChatEvents.NoteNow(item.Kind, item.Value, item.Where);
                if (pass.Social.Groups.GetGroup(guid) is { } group)
                    foreach (GroupMemberSlot slot in group.Members)
                        if (slot.Guid != guid && pass.ByCharacter.TryGetValue(slot.Guid.Low, out PlayerbotSocialBot? member))
                            member.Party.ChatEvents.NoteNow(item.Kind, item.Value, item.Where);
            }
            else if (pass.ByCharacter.TryGetValue(guid.Low, out PlayerbotSocialBot? bot))
            {
                bot.Party.ChatEvents.NoteNow(item.Kind, item.Value, item.Where);
            }
        }
    }

    // --- running conversations -------------------------------------------------------------------------------------------

    private void Advance(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotConversationOptions options)
    {
        long now = pass.NowMs;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Exchange exchange = _active[i];
            if (_interrupted.Contains(exchange.Guids[0].Value) || _interrupted.Contains(exchange.Guids[1].Value))
            {
                Interrupted++;
                End(i);
                continue;
            }

            if (!pass.ByCharacter.TryGetValue(exchange.Guids[0].Low, out PlayerbotSocialBot? first)
                || !pass.ByCharacter.TryGetValue(exchange.Guids[1].Low, out PlayerbotSocialBot? second)
                || !Fit(first.Player) || !Fit(second.Player))
            {
                End(i);
                continue;
            }

            if (exchange.Pending is not null)
            {
                if (!Settle(exchange, now, options, pass.Random)) End(i);
                continue;
            }

            if (now < exchange.DueMs) continue;
            if (now > exchange.LineDeadlineMs)
            {
                End(i);
                continue;
            }

            PlayerbotSocialBot speaker = exchange.Steps[exchange.Next].Speaker == 0 ? first : second;
            PlayerbotSocialBot listener = ReferenceEquals(speaker, first) ? second : first;
            int turn = exchange.Next;
            BotSpeech speech = new BotSpeech(exchange.Channel, exchange.Steps[exchange.Next].Text, "convo." + exchange.Topic.ToString().ToLowerInvariant(),
                exchange.Priority)
            {
                TargetGuid = listener.Player.Guid,
                ToBot = turn > 0,
                Turn = turn,
                Guild = exchange.Guild,
            };
            exchange.Pending = speech;
            exchange.Outcome = null;
            exchange.PendingSinceMs = now;
            BotSpeechAdmission admission = chat.TrySpeak(speaker.Id, speaker.Player.Guid, speech);
            if (admission != BotSpeechAdmission.Queued) exchange.Outcome ??= admission;
            if (exchange.Outcome is not null && !Settle(exchange, now, options, pass.Random)) End(i);
        }

        _interrupted.Clear();
    }

    /// <summary>A pending line's fate: said (next line), refused for a moment (retry) or not sayable (false: the conversation ends).</summary>
    private bool Settle(Exchange exchange, long now, PlayerbotConversationOptions options, Random random)
    {
        if (exchange.Outcome is not { } outcome) return now - exchange.PendingSinceMs < SettleMs;
        exchange.Pending = null;
        exchange.Outcome = null;
        switch (outcome)
        {
            case BotSpeechAdmission.Queued:
                LinesSaid++;
                exchange.Next++;
                if (exchange.Next >= exchange.Steps.Count) return false;
                exchange.DueMs = now + (options.LineGapSeconds * 1000L) + random.Next(options.LineJitterSeconds * 1000 + 1);
                exchange.LineDeadlineMs = exchange.DueMs + (options.LineDeadlineSeconds * 1000L);
                return true;
            case BotSpeechAdmission.Cooldown or BotSpeechAdmission.Target or BotSpeechAdmission.Budget or BotSpeechAdmission.QueueFull
                or BotSpeechAdmission.Duplicate:
                exchange.DueMs = now + RetryMs;
                return now + RetryMs <= exchange.LineDeadlineMs;
            default:
                return false; // no listener, not there any more, flagged, bot-to-bot budget spent, speech off
        }
    }

    private void End(int index)
    {
        Exchange exchange = _active[index];
        _active.RemoveAt(index);
        _chat?.ReleaseExchange(exchange.Guids[0], exchange.Guids[1]);
    }

    private void Clear()
    {
        for (int i = _active.Count - 1; i >= 0; i--) End(i);
        _interrupted.Clear();
    }

    // --- starting one ----------------------------------------------------------------------------------------------------

    private bool Roll(PlayerbotSocialPass pass, PlayerbotConversationOptions options)
    {
        if (options.ConversationsPerHour <= 0) return false;
        while (_starts.Count > 0 && pass.NowMs - _starts.Peek() >= HourMs) _starts.Dequeue();
        if (_starts.Count >= options.ConversationsPerHour) return false;
        double chance = options.ConversationsPerHour * (double)Math.Max(pass.ElapsedMs, 1) / HourMs;
        return pass.Random.NextDouble() < chance;
    }

    private void TryStart(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotConversationOptions options)
    {
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count < 2) return;
        int looks = Math.Min(options.CandidatesPerPass, bots.Count);
        int offset = pass.Random.Next(bots.Count);
        for (int look = 0; look < looks; look++)
        {
            PlayerbotSocialBot a = bots[(offset + (look * 7919)) % bots.Count];
            if (!Free(a, pass.NowMs) || !Fit(a.Player)) continue;
            if (Partner(pass, a, options) is not { } found) continue;
            if (chat.NextBotTurn(a.Player.Guid, found.Bot.Player.Guid) != 1) continue;
            if (Begin(pass, chat, options, a, found.Bot, found.Channel, found.Guild)) return;
        }
    }

    /// <summary>A partner for <paramref name="a"/> and where they talk: its group (party), a bot close by (say) or a guildmate (guild).</summary>
    private (PlayerbotSocialBot Bot, BotSpeechChannel Channel, string? Guild)? Partner(PlayerbotSocialPass pass, PlayerbotSocialBot a,
        PlayerbotConversationOptions options)
    {
        Player player = a.Player;
        long now = pass.NowMs;
        if (pass.Social.Groups.GetGroup(player.Guid) is { } group && HasRealMember(pass, group, player.Guid))
            foreach (GroupMemberSlot slot in group.Members)
                if (slot.Guid != player.Guid && pass.ByCharacter.TryGetValue(slot.Guid.Low, out PlayerbotSocialBot? mate) && Usable(a, mate, now))
                    return (mate, BotSpeechChannel.Party, null);

        if (player.Map is { } map)
        {
            PlayerbotSocialBot? near = null;
            float best = options.NearYards;
            foreach (PlayerbotSocialBot other in pass.Bots)
            {
                if (!ReferenceEquals(other.Player.Map, map) || !Usable(a, other, now)) continue;
                float distance = PlayerbotGeometry.Distance(player, other.Player);
                if (distance <= best)
                {
                    best = distance;
                    near = other;
                }
            }

            if (near is not null && HasRealPlayerNear(pass, player)) return (near, BotSpeechChannel.Say, null);
        }

        if (pass.Social.Guilds.GetGuildOf(player) is { } guild && HasGuildListener(pass, guild))
            foreach (GuildMember member in guild.Members)
                if (member.Guid != player.Guid && pass.ByCharacter.TryGetValue(member.Guid.Low, out PlayerbotSocialBot? mate) && Usable(a, mate, now))
                    return (mate, BotSpeechChannel.Guild, guild.Name);
        return null;
    }

    private bool Begin(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotConversationOptions options, PlayerbotSocialBot a, PlayerbotSocialBot b,
        BotSpeechChannel channel, string? guild)
    {
        BotChatPersona pa = a.Party.PersonaFor(a.Player), pb = b.Party.PersonaFor(b.Player);
        if (pa.Facts is null || pb.Facts is null) return false;
        ConversationSide sa = Side(a, pa), sb = Side(b, pb);
        bool sameGroup = channel == BotSpeechChannel.Party;
        int maxLines = Math.Min(options.MaxLines, 1 + chat.SpeechOptions.BotToBotMaxTurns);
        if (maxLines < PlayerbotConversationScripts.MinLines) return false;

        // The subject is whichever side has news; failing that, the one picked.
        foreach ((ConversationSide subject, ConversationSide other, bool swapped) in (ReadOnlySpan<(ConversationSide, ConversationSide, bool)>)
                     [(sa, sb, false), (sb, sa, true)])
        {
            foreach (ConversationTopic topic in PlayerbotConversationScripts.Topics(subject, other, sameGroup, channel == BotSpeechChannel.Guild, pass.Random))
            {
                if (swapped && topic >= ConversationTopic.Quest) break; // small talk is opened by the bot picked
                if (PlayerbotConversationScripts.Script(topic, subject, other, sameGroup, maxLines) is not { } script) continue;
                PlayerbotSocialBot first = swapped ? b : a, second = swapped ? a : b;
                BotChatPersona firstPersona = swapped ? pb : pa, secondPersona = swapped ? pa : pb;
                if (Render(pass, script, channel, guild, first, firstPersona, second, secondPersona) is not { } lines) continue;
                Launch(pass, chat, options, script, lines, channel, guild, first, second);
                Remember(first, second, script.Topic, pass.NowMs, options);
                return true;
            }
        }

        return false;
    }

    /// <summary>Every line of <paramref name="script"/> filled from its speaker's facts; null when one cannot be.</summary>
    private static List<string>? Render(PlayerbotSocialPass pass, ConversationScript script, BotSpeechChannel channel, string? guild,
        PlayerbotSocialBot first, BotChatPersona firstPersona, PlayerbotSocialBot second, BotChatPersona secondPersona)
    {
        var topic = new BotChatTopic("convo." + script.Topic.ToString().ToLowerInvariant(), string.Empty, script.Values, guild ?? string.Empty);
        ChatType type = new BotSpeech(channel, string.Empty, string.Empty).ChatType;
        var lines = new List<string>(script.Steps.Count);
        string? last = null;
        foreach (ConversationStep step in script.Steps)
        {
            (PlayerbotSocialBot speaker, BotChatPersona persona, PlayerbotSocialBot listener) =
                step.Speaker == 0 ? (first, firstPersona, second) : (second, secondPersona, first);
            var ask = new BotChatAsk(speaker.Id, speaker.Player.Guid, persona, listener.Player.Guid, listener.Player.Name, type, string.Empty,
                FromMaster: false, InviteAllowed: false, Topic: topic, SenderIsBot: true);
            if (PlayerbotBuiltinChat.Pick(step.Key, ask, last, pass.Random) is not { } line) return null;
            lines.Add(line);
            last = line;
        }

        return lines;
    }

    private void Launch(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotConversationOptions options, ConversationScript script, List<string> lines,
        BotSpeechChannel channel, string? guild, PlayerbotSocialBot first, PlayerbotSocialBot second)
    {
        var steps = new List<(int Speaker, string Text)>(lines.Count);
        for (int i = 0; i < lines.Count; i++) steps.Add((script.Steps[i].Speaker, lines[i]));
        var exchange = new Exchange
        {
            Guids = [first.Player.Guid, second.Player.Guid],
            Topic = script.Topic,
            Channel = channel,
            Guild = guild,
            Priority = script.Topic <= ConversationTopic.Need ? BotSpeechPriority.Normal : BotSpeechPriority.Low,
            Steps = steps,
            DueMs = pass.NowMs,
            LineDeadlineMs = pass.NowMs + (options.LineDeadlineSeconds * 1000L),
        };
        _active.Add(exchange);
        long span = (long)lines.Count * (options.LineGapSeconds + options.LineJitterSeconds + options.LineDeadlineSeconds) * 1000L;
        chat.ClaimExchange(first.Player.Guid, second.Player.Guid, chat.NowMs + span + SettleMs);
        _starts.Enqueue(pass.NowMs);
        Started++;
    }

    /// <summary>Cooldowns for the pair and both bots, and the news now talked about (never twice).</summary>
    private void Remember(PlayerbotSocialBot first, PlayerbotSocialBot second, ConversationTopic topic, long now, PlayerbotConversationOptions options)
    {
        _pairs[PairOf(first.Player.Guid, second.Player.Guid)] = now + (options.PairCooldownMinutes * 60_000L);
        long rest = now + (options.BotCooldownMinutes * 60_000L);
        _bots[first.Player.Guid.Value] = rest;
        _bots[second.Player.Guid.Value] = rest;
        BotChatEventKind? kind = topic switch
        {
            ConversationTopic.Ding => BotChatEventKind.Ding,
            ConversationTopic.Boss => BotChatEventKind.BossKill,
            ConversationTopic.Loot => BotChatEventKind.Loot,
            ConversationTopic.Death => BotChatEventKind.Died,
            _ => null,
        };
        if (kind is not { } k) return;
        foreach (PlayerbotSocialBot bot in (ReadOnlySpan<PlayerbotSocialBot>)[first, second])
            if (bot.Party.ChatEvents.LatestNow(k, NewsMs) is { } ev) _talkedAbout[(bot.Player.Guid.Value, k)] = ev.AtMs;
    }

    /// <summary>The script's view of one bot: its facts and the fresh news it has not talked about.</summary>
    private ConversationSide Side(PlayerbotSocialBot bot, BotChatPersona persona)
    {
        PlayerbotChatEvents events = bot.Party.ChatEvents;
        string? Fresh(BotChatEventKind kind, out string? where)
        {
            where = null;
            if (events.LatestNow(kind, NewsMs) is not { } ev) return null;
            if (_talkedAbout.TryGetValue((bot.Player.Guid.Value, kind), out long at) && at >= ev.AtMs) return null;
            where = ev.Where;
            return ev.Value ?? string.Empty;
        }

        string? ding = Fresh(BotChatEventKind.Ding, out _);
        string? loot = Fresh(BotChatEventKind.Loot, out _);
        string? boss = Fresh(BotChatEventKind.BossKill, out string? bossWhere);
        bool died = Fresh(BotChatEventKind.Died, out _) is not null;
        return new ConversationSide(persona.Name, persona.Level, persona.Facts!, persona.QuestTitle, ding is { Length: > 0 } ? ding : null,
            loot is { Length: > 0 } ? loot : null, boss is { Length: > 0 } ? boss : null, bossWhere, died);
    }

    private bool Free(PlayerbotSocialBot bot, long now)
    {
        if (_bots.TryGetValue(bot.Player.Guid.Value, out long until) && until > now) return false;
        foreach (Exchange exchange in _active)
            if (exchange.Guids[0] == bot.Player.Guid || exchange.Guids[1] == bot.Player.Guid) return false;
        return true;
    }

    private bool Usable(PlayerbotSocialBot a, PlayerbotSocialBot b, long now)
        => !ReferenceEquals(a, b) && a.Player.Guid != b.Player.Guid && Free(b, now) && Fit(b.Player)
           && !(_pairs.TryGetValue(PairOf(a.Player.Guid, b.Player.Guid), out long until) && until > now);

    /// <summary>A bot that can talk: alive (a ghost's say reaches only ghosts) and out of combat.</summary>
    private static bool Fit(Player player) => player.IsInWorld && player.IsAlive && !player.Combat.IsInCombat;

    private static bool HasRealMember(PlayerbotSocialPass pass, Group group, ObjectGuid self)
    {
        foreach (GroupMemberSlot slot in group.Members)
            if (slot.Guid != self && pass.World.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } }) return true;
        return false;
    }

    private static bool HasGuildListener(PlayerbotSocialPass pass, Guild guild)
    {
        foreach (GuildMember member in guild.Members)
            if (guild.HasRight(member.Rank, GuildRights.GuildChatListen) && pass.World.FindOnlinePlayer(member.Guid) is { Session: WorldSession { IsManaged: false } })
                return true;
        return false;
    }

    /// <summary>Whether a real player stands within say range of <paramref name="player"/> (the delivery checks it again, exactly).</summary>
    private static bool HasRealPlayerNear(PlayerbotSocialPass pass, Player player)
    {
        if (player.Map is not { } map) return false;
        foreach (Player other in map.Players)
            if (other.Session is WorldSession { IsManaged: false } && other.IsInWorld && PlayerbotGeometry.Distance(other, player) <= SayYards)
                return true;
        return false;
    }

    /// <summary>The /say range (vmangos sWorld.getConfig(CONFIG_FLOAT_LISTEN_RANGE_SAY), 25 yards).</summary>
    internal const float SayYards = 25f;

    private void Sweep(long now)
    {
        if (_pairs.Count > 512)
            foreach (KeyValuePair<(ulong, ulong), long> pair in _pairs.Where(p => p.Value <= now).ToArray()) _pairs.Remove(pair.Key);
        if (_bots.Count > 512)
            foreach (KeyValuePair<ulong, long> bot in _bots.Where(p => p.Value <= now).ToArray()) _bots.Remove(bot.Key);
        if (_talkedAbout.Count > 1024) _talkedAbout.Clear();
    }

    private static (ulong, ulong) PairOf(ObjectGuid a, ObjectGuid b) => a.Value <= b.Value ? (a.Value, b.Value) : (b.Value, a.Value);

    private sealed class Exchange
    {
        public ObjectGuid[] Guids { get; init; } = [];
        public ConversationTopic Topic { get; init; }
        public BotSpeechChannel Channel { get; init; }
        public string? Guild { get; init; }
        public BotSpeechPriority Priority { get; init; }
        public List<(int Speaker, string Text)> Steps { get; init; } = [];
        public int Next { get; set; }
        public long DueMs { get; set; }
        public long LineDeadlineMs { get; set; }
        public BotSpeech? Pending { get; set; }
        public BotSpeechAdmission? Outcome { get; set; }
        public long PendingSinceMs { get; set; }
    }
}
