using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Why a bot opens a conversation.</summary>
internal enum PlayerbotOpeningKind : byte
{
    /// <summary>The other is on the bot's quest objective: "You on the X too? Want to team up?".</summary>
    Objective,

    /// <summary>A familiar character the bot last talked to about something.</summary>
    FollowUp,

    /// <summary>A familiar character who has just arrived.</summary>
    Greeting,
}

/// <summary>The pure rules of initiative (docs/areas/playbots-social.md, Initiative). Every roll is a hash, so a choice is repeatable.</summary>
internal static class PlayerbotInitiativeRules
{
    /// <summary>At or above this affinity (or after opting in to AI chat) a bot may whisper a player unprompted.</summary>
    internal const int WhisperAffinity = 10;

    /// <summary>The most the cooldown for a player who ignores openings is multiplied by.</summary>
    internal const int MaxBackoff = 16;

    /// <summary>The salt of the hash roll of an opening of a kind.</summary>
    internal static ulong SaltOf(PlayerbotOpeningKind kind) => kind switch
    {
        PlayerbotOpeningKind.Objective => 0x0B1EC71EUL,
        PlayerbotOpeningKind.FollowUp => 0xF0110D0UL,
        _ => 0x6EE7ED0UL,
    };

    /// <summary>The salt of the hash roll of a surroundings comment (rolled on the bot and its zone).</summary>
    internal const ulong CommentSalt = 0xC033E27UL;

    /// <summary>The chance a free bot remarks on its surroundings in a given minute: 0.35, times 0.5 + chattiness, times the mood's multiplier, at most 1.</summary>
    internal static double CommentChance(float chattiness, float mood) => Math.Min(1.0, 0.35 * (0.5 + chattiness) * PlayerbotMood.ChanceMultiplier(mood));

    /// <summary>The chance of an opening of a kind, before the bot's sociability and mood scale it.</summary>
    internal static double BaseChance(PlayerbotOpeningKind kind) => kind switch
    {
        PlayerbotOpeningKind.Objective => 0.5,
        PlayerbotOpeningKind.FollowUp => 0.3,
        _ => 0.25,
    };

    /// <summary>The chance this bot opens with a kind now: the base, times 0.5 + sociability, times the mood's multiplier, at most 1.</summary>
    internal static double Chance(PlayerbotOpeningKind kind, float sociability, float mood)
        => Math.Min(1.0, BaseChance(kind) * (0.5 + sociability) * PlayerbotMood.ChanceMultiplier(mood));

    /// <summary>Whether the hash of the bot, the other and the minute is below <paramref name="chance"/>.</summary>
    internal static bool Rolls(ulong botGuid, ulong otherGuid, long nowMs, ulong salt, double chance)
        => (PlayerbotContention.Mix(PlayerbotContention.Mix(botGuid, otherGuid), salt ^ (ulong)(nowMs / 60_000L)) >> 11) * (1.0 / 9007199254740992.0) < chance;

    /// <summary>The channel of an unprompted opening: a whisper to a player who opted in to AI chat or whom the bot likes, else a public say.</summary>
    internal static BotSpeechChannel ChannelFor(bool optedIn, int affinity) => optedIn || affinity >= WhisperAffinity ? BotSpeechChannel.Whisper : BotSpeechChannel.Say;

    /// <summary>The milliseconds before any bot opens with a player again: the cooldown, doubled for each opening they ignored (at most <see cref="MaxBackoff"/> times).</summary>
    internal static long CooldownMs(int cooldownMinutes, int ignored) => cooldownMinutes * 60_000L * Math.Min(MaxBackoff, 1 << Math.Min(ignored, 4));

    private static readonly string[] Yes = ["yes", "yeah", "yep", "yup", "sure", "ok", "okay", "ye", "y", "team", "group", "invite", "please", "gladly", "definitely"];
    private static readonly string[] No = ["no", "nope", "nah", "dont", "don't", "never", "busy", "alone"];

    /// <summary>Whether a reply to an offer to team up is a yes: a yes word and no no word.</summary>
    internal static bool IsPositiveReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool yes = false;
        foreach (string word in text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(No, word) >= 0) return false;
            if (Array.IndexOf(Yes, word) >= 0) yes = true;
        }

        return yes;
    }
}

/// <summary>
/// Bots starting conversations and commenting on their surroundings (<see cref="PlayerbotSocialInitiativeOptions"/>, docs/areas/playbots-social.md, Initiative). Run
/// from the social pass on the world thread; at most <c>MaxOpeningsPerPass</c> openings per pass, each through <see cref="Chat.PlayerbotChat.TryAnnounce"/>.
/// <list type="bullet">
/// <item>A round of free bots (a bounded slice per pass) looks at the characters near it. An opening needs a reason: the same quest objective, a familiar character
/// to follow up with, or a familiar character who has just arrived. A bot opens with another bot only where a real player can hear.</item>
/// <item>A real player hears from bots once per cooldown; one who answers resets the back-off, one who does not (120 seconds) costs the bot 1 affinity and doubles the cooldown.</item>
/// <item>A yes to "want to team up?" raises a group signal (the coordinator matches by objective) and tells the coordinator the player is welcome.</item>
/// <item>A free bot also remarks, rarely, on a rare creature in sight, a world event that started, an enemy raid, or a friend's first mount: once per bot per
/// <c>CommentBotGapMinutes</c> and per zone per <c>CommentZoneGapSeconds</c>.</item>
/// </list>
/// All of it is memory only and bounded. Never to someone the bot dislikes or holds as a rival, never to a player who ignores it.
/// </summary>
internal sealed class PlayerbotInitiativeLane
{
    internal const int BotsPerPass = 24;
    internal const int SightLimit = 48;
    internal const float TalkYards = 20f;
    internal const long BotGapMs = 20_000;
    internal const long PairGapMs = 6 * 3_600_000L;
    internal const long ReplyWaitMs = 120_000;
    internal const long ArrivalGapMs = 10 * 60_000L;
    internal const float RareYards = 60f;
    internal const float RaidYards = 80f;
    internal const int RaidPlayers = 4;
    private const int Bound = 4096;

    private sealed record Pending(ObjectGuid BotGuid, long AtMs, PlayerbotOpeningKind Kind, uint Entry, uint Quest);

    private readonly Dictionary<ulong, long> _playerNext = [];
    private readonly Dictionary<ulong, int> _ignored = [];
    private readonly Dictionary<(Guid Bot, ulong Other), Pending> _pending = [];
    private readonly Dictionary<(Guid Bot, ulong Other, PlayerbotOpeningKind Kind), long> _pairNext = [];
    private readonly Dictionary<(Guid Bot, ulong Other), long> _seen = [];
    private readonly Dictionary<Guid, long> _botNext = [];
    private readonly Dictionary<Guid, long> _commentBot = [];
    private readonly Dictionary<uint, long> _commentZone = [];
    private readonly HashSet<ulong> _mounted = [];
    private readonly HashSet<ushort> _events = [];
    private readonly HashSet<ushort> _newEvents = [];
    private bool _eventsKnown;
    private int _cursor;
    private long _sweptAtMs;

    /// <summary>Openings made (tests and status).</summary>
    internal int Openings { get; private set; }

    /// <summary>Openings by kind.</summary>
    internal int[] ByKind { get; } = new int[3];

    /// <summary>Openings made as a whisper.</summary>
    internal int Whispers { get; private set; }

    /// <summary>Openings answered with a yes to teaming up.</summary>
    internal int Accepted { get; private set; }

    /// <summary>Openings nobody answered.</summary>
    internal int Ignored { get; private set; }

    /// <summary>Surroundings comments made.</summary>
    internal int Comments { get; private set; }

    /// <summary>The player's cooldown for openings ends at (world milliseconds), 0 when none runs (tests).</summary>
    internal long NextOpeningFor(ObjectGuid player) => _playerNext.GetValueOrDefault(player.Value);

    internal void Run(PlayerbotSocialPass pass)
    {
        PlayerbotSocialRecorder? recorder = pass.Recorder;
        if (recorder is null) return;
        PlayerbotSocialInitiativeOptions options = pass.Options.Initiative;
        if (!options.Enabled || !recorder.Enabled)
        {
            while (recorder.TryTakeReply(out _, out _, out _)) { }
            _pending.Clear();
            return;
        }

        Sweep(pass.NowMs);
        Replies(pass, recorder);
        Unanswered(pass, recorder);
        if (options.CommentsEnabled) NoteEvents(pass);
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count == 0) return;
        int budget = options.MaxOpeningsPerPass;
        int count = Math.Min(BotsPerPass, bots.Count);
        for (int i = 0; i < count && budget > 0; i++)
        {
            PlayerbotSocialBot bot = bots[(_cursor + i) % bots.Count];
            if (_botNext.TryGetValue(bot.Id, out long next) && next > pass.NowMs) continue;
            if (!PlayerbotHelpLane.IsFree(bot)) continue;
            if (LookAround(pass, recorder, options, bot, ref budget)) _botNext[bot.Id] = pass.NowMs + BotGapMs;
        }

        _cursor = (_cursor + count) % bots.Count;
        _newEvents.Clear();
    }

    // --- replies and silences ------------------------------------------------------------------------------------------------

    private void Replies(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder)
    {
        for (int budget = 32; budget > 0 && recorder.TryTakeReply(out Guid botId, out ObjectGuid sender, out string? text); budget--)
        {
            if (!_pending.Remove((botId, sender.Value), out Pending? pending)) continue;
            _ignored.Remove(sender.Value);
            if (pending.Kind != PlayerbotOpeningKind.Objective || !PlayerbotInitiativeRules.IsPositiveReply(text)) continue;
            Accepted++;
            if (!pass.ByCharacter.TryGetValue(pending.BotGuid.Low, out PlayerbotSocialBot? bot) || pending.Entry == 0 || bot.Brain is null) continue;
            Player self = bot.Player;
            // The coordinator matches by objective; the player is welcome whatever the bot's affinity toward them is.
            bot.Brain.Risk.RaiseGroupSignal(new PlayerbotGroupSignal(pending.Entry, 2, self.MapId, new System.Numerics.Vector3(self.X, self.Y, self.Z), pass.World.NowMs,
                PlayerbotGroupSignal.ContentionSource, pending.Quest));
            pass.Coordinator?.Welcome(sender, unchecked(pass.World.NowMs + 5 * 60_000u));
        }
    }

    private void Unanswered(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder)
    {
        if (_pending.Count == 0) return;
        foreach (KeyValuePair<(Guid Bot, ulong Other), Pending> entry in _pending)
        {
            if (pass.NowMs - entry.Value.AtMs < ReplyWaitMs) continue;
            _pending.Remove(entry.Key);
            Ignored++;
            _ignored[entry.Key.Other] = _ignored.GetValueOrDefault(entry.Key.Other) + 1;
            _playerNext[entry.Key.Other] = pass.NowMs + PlayerbotInitiativeRules.CooldownMs(pass.Options.Initiative.OpeningCooldownMinutes, _ignored[entry.Key.Other]);
            recorder.NoteById(entry.Key.Bot, entry.Value.BotGuid, new ObjectGuid(entry.Key.Other), PlayerbotSocialEvent.OpeningIgnored);
        }
    }

    // --- one bot ---------------------------------------------------------------------------------------------------------------

    private bool LookAround(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialInitiativeOptions options, PlayerbotSocialBot bot, ref int budget)
    {
        Player self = bot.Player;
        PlayerbotTraits traits = PlayerbotTraits.For(self.Guid.Value);
        float mood = recorder.Mood.Get(bot.Id);
        Player? listener = null;
        bool listenerLooked = false;
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == self.Guid) continue;
            if (++looked > SightLimit) break;
            if (pass.World.FindOnlinePlayer(guid) is not { } other || !ReferenceEquals(other.Map, self.Map) || other.Team != self.Team || !other.IsAlive || other.Combat.IsInCombat) continue;
            if (Distance(self, other) > TalkYards) continue;
            (Guid, ulong) key = (bot.Id, guid.Value);
            bool arrival = !_seen.TryGetValue(key, out long last) || pass.NowMs - last > ArrivalGapMs;
            _seen[key] = pass.NowMs;
            bool real = other.Session is WorldSession { IsManaged: false };
            if (real)
            {
                if (_playerNext.TryGetValue(guid.Value, out long until) && until > pass.NowMs) continue;
                if (pass.Social.IsIgnoring(other, self.Guid)) continue;
            }
            else
            {
                if (!listenerLooked)
                {
                    listener = RealListener(pass, self);
                    listenerLooked = true;
                }

                if (listener is null) continue; // bots talk to each other only where a real player can hear
            }

            bool known = recorder.Memory.TryGet(bot.Id, guid.Value, out PlayerbotRelation relation);
            if (known && recorder.Memory.IsRival(relation)) continue;
            int affinity = known ? recorder.Memory.EffectiveAffinity(relation) : 0;
            if (affinity < 0) continue;
            PlayerbotOpeningKind? kind = KindFor(pass, bot, other, known ? relation : null, arrival, traits.Sociability, mood);
            if (kind is not { } chosen) continue;
            if (Open(pass, recorder, options, bot, other, real, known ? relation : null, affinity, chosen))
            {
                budget--;
                return true;
            }
        }

        return budget > 0 && options.CommentsEnabled && TryComment(pass, recorder, options, bot, traits, mood);
    }

    private PlayerbotOpeningKind? KindFor(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player other, PlayerbotRelation? relation, bool arrival, float sociability, float mood)
    {
        Player self = bot.Player;
        if (bot.Brain!.QuestId != 0 && bot.Brain.TargetEntry != 0 && pass.Coordinator?.SharesGoal(bot.Id, other) == true && Ready(bot.Id, other, PlayerbotOpeningKind.Objective, pass.NowMs)
            && PlayerbotInitiativeRules.Rolls(self.Guid.Value, other.Guid.Value, pass.NowMs, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Objective), PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, sociability, mood)))
            return PlayerbotOpeningKind.Objective;
        if (relation is null) return null;
        if (relation.Familiarity >= PlayerbotBuiltinChat.WelcomeBackFamiliarity && relation.LastTopic.Length > 0 && Ready(bot.Id, other, PlayerbotOpeningKind.FollowUp, pass.NowMs)
            && PlayerbotInitiativeRules.Rolls(self.Guid.Value, other.Guid.Value, pass.NowMs, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.FollowUp), PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.FollowUp, sociability, mood)))
            return PlayerbotOpeningKind.FollowUp;
        if (arrival && relation.Familiarity >= 2 && Ready(bot.Id, other, PlayerbotOpeningKind.Greeting, pass.NowMs)
            && PlayerbotInitiativeRules.Rolls(self.Guid.Value, other.Guid.Value, pass.NowMs, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Greeting), PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, sociability, mood)))
            return PlayerbotOpeningKind.Greeting;
        return null;
    }

    private bool Ready(Guid bot, Player other, PlayerbotOpeningKind kind, long nowMs)
        => !_pairNext.TryGetValue((bot, other.Guid.Value, kind), out long until) || until <= nowMs;

    private static Player? RealListener(PlayerbotSocialPass pass, Player self)
    {
        foreach (ObjectGuid guid in self.VisibleObjects)
            if (guid.IsPlayer && pass.World.FindOnlinePlayer(guid) is { Session: WorldSession { IsManaged: false } } real && ReferenceEquals(real.Map, self.Map)
                && real.Team == self.Team && Distance(self, real) <= pass.Options.Memory.BystanderRadiusYards && !pass.Social.IsIgnoring(real, self.Guid))
                return real;
        return null;
    }

    private bool Open(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialInitiativeOptions options, PlayerbotSocialBot bot, Player other, bool real,
        PlayerbotRelation? relation, int affinity, PlayerbotOpeningKind kind)
    {
        if (pass.Chat is not { IsActive: true } chat) return false;
        Player self = bot.Player;
        BotSpeechChannel channel = !real ? BotSpeechChannel.Say
            : PlayerbotInitiativeRules.ChannelFor(chat.Safety.Player(other.Name)?.AiReplies == true, affinity);
        var values = new Dictionary<string, string>();
        string instruction;
        string key;
        uint entry = 0, quest = 0;
        switch (kind)
        {
            case PlayerbotOpeningKind.Objective:
                entry = bot.Brain!.TargetEntry;
                quest = bot.Brain.QuestId;
                values["objective"] = pass.Quests?.Quests.Get(quest)?.Title is { Length: > 0 } title ? title : "same quest";
                key = "social.team";
                instruction = $"You are working on the same quest as {other.Name}, who stands nearby: {values["objective"]}. Ask in one short line whether they want to team up.";
                break;
            case PlayerbotOpeningKind.FollowUp:
                values["topic"] = relation!.Summary.Length > 0 ? relation.Summary : relation.LastTopic;
                key = "social.followup";
                instruction = $"You know {other.Name} and last talked with them about {values["topic"]}. Ask in one short line how that went.";
                break;
            default:
                key = "social.arrive";
                instruction = $"You know {other.Name} and they have just arrived nearby. Greet them warmly in one short line.";
                break;
        }

        var speech = new BotSpeech(channel, string.Empty, "initiative-" + kind.ToString().ToLowerInvariant(), BotSpeechPriority.Low) { TargetGuid = other.Guid };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), other.Guid, other.Name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0, Topic: new BotChatTopic(key, instruction, values, string.Empty), Speech: speech);
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;

        Openings++;
        ByKind[(int)kind]++;
        if (channel == BotSpeechChannel.Whisper) Whispers++;
        _pairNext[(bot.Id, other.Guid.Value, kind)] = pass.NowMs + PairGapMs;
        if (real)
        {
            _playerNext[other.Guid.Value] = pass.NowMs + PlayerbotInitiativeRules.CooldownMs(options.OpeningCooldownMinutes, _ignored.GetValueOrDefault(other.Guid.Value));
            Expect(bot.Id, self.Guid, other.Guid, kind, entry, quest, pass.NowMs);
        }

        return true;
    }

    /// <summary>Wait for the answer of <paramref name="other"/> to an opening (also the tests' way to stage one).</summary>
    internal void Expect(Guid bot, ObjectGuid botGuid, ObjectGuid other, PlayerbotOpeningKind kind, uint entry, uint quest, long nowMs)
    {
        if (_pending.Count < Bound) _pending[(bot, other.Value)] = new Pending(botGuid, nowMs, kind, entry, quest);
    }

    // --- surroundings comments -------------------------------------------------------------------------------------------------

    private void NoteEvents(PlayerbotSocialPass pass)
    {
        if (pass.Services.GetService<GameEventFeature>() is not { } feature) return;
        HashSet<ushort> now = [.. feature.ActiveEvents];
        if (_eventsKnown)
            foreach (ushort id in now)
                if (!_events.Contains(id)) _newEvents.Add(id);
        _events.Clear();
        foreach (ushort id in now) _events.Add(id);
        _eventsKnown = true;
    }

    private bool TryComment(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialInitiativeOptions options, PlayerbotSocialBot bot, PlayerbotTraits traits, float mood)
    {
        Player self = bot.Player;
        if (_commentBot.TryGetValue(bot.Id, out long botUntil) && botUntil > pass.NowMs) return false;
        if (_commentZone.TryGetValue(self.ZoneId, out long zoneUntil) && zoneUntil > pass.NowMs) return false;
        if (!PlayerbotInitiativeRules.Rolls(self.Guid.Value, (ulong)self.ZoneId, pass.NowMs, PlayerbotInitiativeRules.CommentSalt, PlayerbotInitiativeRules.CommentChance(traits.Chattiness, mood))) return false;
        if (RealListener(pass, self) is not { } listener) return false; // a remark is for someone to hear

        string? key = null;
        string instruction = string.Empty;
        string? mounted = null;
        int enemies = 0;
        bool rare = false;
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (++looked > SightLimit * 2) break;
            if (guid.IsPlayer)
            {
                if (guid == self.Guid || pass.World.FindOnlinePlayer(guid) is not { } other || !ReferenceEquals(other.Map, self.Map)) continue;
                if (other.Team != self.Team)
                {
                    if (other.IsAlive && Distance(self, other) <= RaidYards) enemies++;
                }
                else if (mounted is null && other.Session is WorldSession { IsManaged: false } && other.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0 && !_mounted.Contains(guid.Value)
                    && recorder.Memory.TryGet(bot.Id, guid.Value, out _))
                {
                    mounted = other.Name;
                    if (_mounted.Count > Bound) _mounted.Clear();
                    _mounted.Add(guid.Value);
                }

                continue;
            }

            if (!rare && self.Map?.FindObject(guid) is Creature { IsAlive: true } creature && creature.Template.Rank is 2 or 4 && Distance(self, creature) <= RareYards) rare = true;
        }

        if (rare)
        {
            key = "comment.rare";
            instruction = "A rare creature is near you. Remark on it in one short line.";
        }
        else if (_newEvents.Count > 0)
        {
            key = "comment.event";
            instruction = "A world event has just started. Remark on it in one short line.";
        }
        else if (enemies >= RaidPlayers)
        {
            key = "comment.raid";
            instruction = "Many players of the enemy faction are around you. Remark on it in one short line.";
        }
        else if (mounted is not null)
        {
            key = "comment.mount";
            instruction = $"{mounted} has just got a first mount. Congratulate them in one short line.";
        }

        if (key is null || pass.Chat is not { IsActive: true } chat) return false;
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, key.Replace('.', '-'), BotSpeechPriority.Low) { TargetGuid = listener.Guid };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), listener.Guid, mounted ?? listener.Name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0, Topic: new BotChatTopic(key, instruction, new Dictionary<string, string>(), string.Empty), Speech: speech);
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;
        Comments++;
        _commentBot[bot.Id] = pass.NowMs + options.CommentBotGapMinutes * 60_000L;
        _commentZone[self.ZoneId] = pass.NowMs + options.CommentZoneGapSeconds * 1000L;
        return true;
    }

    private void Sweep(long nowMs)
    {
        if (nowMs - _sweptAtMs < 60_000) return;
        _sweptAtMs = nowMs;
        Prune(_playerNext, static (until, now) => until <= now, nowMs);
        Prune(_pairNext, static (until, now) => until <= now, nowMs);
        Prune(_botNext, static (until, now) => until <= now, nowMs);
        Prune(_commentBot, static (until, now) => until <= now, nowMs);
        Prune(_commentZone, static (until, now) => until <= now, nowMs);
        Prune(_seen, static (at, now) => now - at > ArrivalGapMs, nowMs);
        if (_ignored.Count > Bound) _ignored.Clear();
    }

    private static void Prune<TKey>(Dictionary<TKey, long> map, Func<long, long, bool> stale, long now) where TKey : notnull
    {
        if (map.Count == 0) return;
        foreach (KeyValuePair<TKey, long> entry in map)
            if (stale(entry.Value, now)) map.Remove(entry.Key);
    }
}
