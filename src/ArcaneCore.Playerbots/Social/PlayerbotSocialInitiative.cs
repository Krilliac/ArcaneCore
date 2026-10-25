using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Events;
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

    /// <summary>
    /// The channel of an unprompted opening: a whisper to a player who opted in to AI chat, or whom the bot likes <b>and</b> who has done something toward it
    /// (<see cref="PlayerbotRelationFlags.Interacted"/>: affinity the bot earned alone, by helping, is not an invitation), else a public say.
    /// </summary>
    internal static BotSpeechChannel ChannelFor(bool optedIn, int affinity, bool interacted)
        => optedIn || (affinity >= WhisperAffinity && interacted) ? BotSpeechChannel.Whisper : BotSpeechChannel.Say;

    /// <summary>The milliseconds before any bot opens with a player again: the cooldown, doubled for each opening they ignored (at most <see cref="MaxBackoff"/> times).</summary>
    internal static long CooldownMs(int cooldownMinutes, int ignored) => cooldownMinutes * 60_000L * Math.Min(MaxBackoff, 1 << Math.Min(ignored, 4));

    private static readonly string[] Yes = ["yes", "yeah", "yep", "yup", "sure", "ok", "okay", "ye", "y", "team", "group", "invite", "please", "gladly", "definitely"];
    private static readonly string[] No = ["no", "nope", "nah", "dont", "don't", "never", "busy", "alone"];

    /// <summary>How a reply to an opening reads.</summary>
    internal enum Reply : byte
    {
        /// <summary>Neither: a remark, a greeting, a question.</summary>
        Other,

        /// <summary>A yes word and no no word.</summary>
        Yes,

        /// <summary>A no word, or a "not" ("not sure", "not now"; "why not" is a yes).</summary>
        No,
    }

    /// <summary>Read a reply to an opening: a no word or a negation ("not", "can't") is a no; else a yes word is a yes.</summary>
    internal static Reply Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Reply.Other;
        bool yes = false;
        string? previous = null;
        foreach (string word in text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(No, word) >= 0) return Reply.No;
            if ((word == "not" && previous != "why") || word.EndsWith("n't", StringComparison.Ordinal) || word is "cant" or "wont") return Reply.No;
            if (Array.IndexOf(Yes, word) >= 0) yes = true;
            previous = word;
        }

        return yes ? Reply.Yes : Reply.Other;
    }

    /// <summary>Whether a reply to an offer to team up is a yes.</summary>
    internal static bool IsPositiveReply(string? text) => Classify(text) == Reply.Yes;

    /// <summary>
    /// Whether the word is one the reply rules read or an ordinary reply filler: a yes or no word, "not", "cant"/"wont", a greeting, thanks, "why", or a filler
    /// such as "now", "later", "maybe", "thx", "ty", "np". Such a word is never taken for a character's name, so a character called Sure or Maybe swallows no answer.
    /// </summary>
    internal static bool IsReplyWord(ReadOnlySpan<char> word)
    {
        foreach (string w in Yes) if (word.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string w in No) if (word.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string w in ReplyFillers) if (word.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] ReplyFillers = ["not", "cant", "wont", "why", "now", "later", "maybe", "thanks", "thank", "thx", "ty", "np", "hey", "hello", "hi"];
}

/// <summary>
/// Bots starting conversations and commenting on their surroundings (<see cref="PlayerbotSocialInitiativeOptions"/>, docs/areas/playbots-social.md, Initiative). Run
/// from the social pass on the world thread; at most <c>MaxOpeningsPerPass</c> openings per pass, each through <see cref="Chat.PlayerbotChat.TryAnnounce"/>.
/// <list type="bullet">
/// <item>A round of free bots (a bounded slice per pass) looks at the characters near it. An opening needs a reason: the same quest objective, a familiar character
/// to follow up with, or a familiar character who has just arrived. A bot opens with another bot only where a real player can hear.</item>
/// <item>A real player hears from bots once per cooldown. While an opening is pending the player's next say (or a line to the bot) within hearing is the reply, named or
/// not. A positive reply clears the back-off; any other reply (a no included) doubles the cooldown but costs no affinity; silence for 120 seconds costs the bot
/// 1 affinity and doubles the cooldown.</item>
/// <item>A yes to "want to team up?" raises a group signal and asks the coordinator to match that player (<c>Volunteer</c>: the invitation is the leader's ordinary
/// CMSG_GROUP_INVITE); a bot's yes to another bot's offer is a recruit through the coordinator (<c>OfferLfg</c>).</item>
/// <item>A free bot also remarks, rarely, on a rare creature in sight, a world event that started, an enemy raid of real players, or a friend's first mount (one it
/// saw unmounted at level 40 or more before; memory only): once per bot per
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

    /// <summary>The longest a queued real-player opening holds that player for other bots when neither <see cref="BotSpeech.Sent"/> nor <see cref="BotSpeech.Dropped"/> is ever called (a safety net only: the chat worker takes up to the provider timeout per provider, with a queue in front of it).</summary>
    internal const long UnsaidHoldMs = 10 * 60_000;
    internal const long PairGapMs = 6 * 3_600_000L;
    internal const long ReplyWaitMs = 120_000;
    internal const long ArrivalGapMs = 10 * 60_000L;
    internal const float RareYards = 60f;
    internal const float RaidYards = 80f;
    internal const int RaidPlayers = 4;
    internal const int FirstMountLevel = 40;
    private const int Bound = 4096;

    private sealed record Pending(ObjectGuid BotGuid, long AtMs, PlayerbotOpeningKind Kind, uint Entry, uint Quest);

    // A team-up the coordinator is to act on: a player's yes (Answerer empty) or another bot's.
    private sealed record Follow(Guid Bot, Guid Answerer, ObjectGuid Player, long UntilMs);

    internal const long FollowMs = 2 * 60_000L;

    private readonly List<Follow> _follow = [];
    private readonly HashSet<(Guid Bot, ulong Other)> _unmounted = [];
    private readonly Queue<(Guid Bot, ulong Other)> _unmountedOrder = [];
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

    /// <summary>Bots that said yes to another bot's offer to team up and were taken into its group's plan by the coordinator.</summary>
    internal int BotOffers { get; private set; }

    /// <summary>Openings nobody answered.</summary>
    internal int Ignored { get; private set; }

    /// <summary>Surroundings comments made.</summary>
    internal int Comments { get; private set; }

    /// <summary>How many openings wait for their answer (tests).</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>Whether an opening of the bot to the player still waits for its answer (tests).</summary>
    internal bool PendingFor(Guid bot, ObjectGuid player) => _pending.ContainsKey((bot, player.Value));

    /// <summary>How many openings in a row the player has ignored or turned down (the cooldown doubles with each, tests).</summary>
    internal int BackoffOf(ObjectGuid player) => _ignored.GetValueOrDefault(player.Value);

    /// <summary>The player's cooldown for openings ends at (world milliseconds), 0 when none runs (tests).</summary>
    internal long NextOpeningFor(ObjectGuid player) => _playerNext.GetValueOrDefault(player.Value);

    /// <summary>Whether bots may address the real player at <paramref name="nowMs"/>: no cooldown runs for them (the one ledger of openings, event remarks and festival arrival lines).</summary>
    internal bool MayAddress(ObjectGuid player, long nowMs) => !_playerNext.TryGetValue(player.Value, out long until) || until <= nowMs;

    /// <summary>
    /// A bot addressed the real player at <paramref name="nowMs"/> (a festival line or an event remark): the player is left alone for the
    /// opening cooldown, doubled for each opening they ignored, as after an opening. Never shortens a longer cooldown that runs.
    /// </summary>
    internal void NoteAddressed(ObjectGuid player, long nowMs, int openingCooldownMinutes)
    {
        long until = nowMs + PlayerbotInitiativeRules.CooldownMs(openingCooldownMinutes, _ignored.GetValueOrDefault(player.Value));
        _playerNext[player.Value] = Math.Max(_playerNext.GetValueOrDefault(player.Value), until);
    }

    private long _passNowMs;

    internal void Run(PlayerbotSocialPass pass)
    {
        _passNowMs = pass.NowMs;
        PlayerbotSocialRecorder? recorder = pass.Recorder;
        if (recorder is null) return;
        PlayerbotSocialInitiativeOptions options = pass.Options.Initiative;
        if (!options.Enabled || !recorder.Enabled)
        {
            while (recorder.TryTakeReply(out _, out _, out _)) { }
            _pending.Clear();
            _follow.Clear();
            recorder.ClearExpected();
            return;
        }

        Sweep(pass.NowMs);
        Replies(pass, recorder, options);
        Unanswered(pass, recorder);
        FollowThrough(pass);
        if (options.CommentsEnabled) NoteEvents(pass);
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count == 0)
        {
            _newEvents.Clear();
            return;
        }

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

    private void Replies(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialInitiativeOptions options)
    {
        for (int budget = 32; budget > 0 && recorder.TryTakeReply(out Guid botId, out ObjectGuid sender, out string? text); budget--)
        {
            if (!_pending.TryGetValue((botId, sender.Value), out Pending? waiting)) continue;
            if (NamesAnother(pass, text, sender, waiting.BotGuid))
            {
                recorder.Expect(botId, sender); // a line for someone else: keep listening for the real answer
                continue;
            }

            _pending.Remove((botId, sender.Value));
            Pending pending = waiting;
            recorder.Unexpect(botId, sender);
            PlayerbotInitiativeRules.Reply reply = PlayerbotInitiativeRules.Classify(text);
            // An offer to team up wants a yes; a greeting or a follow-up is answered by anything but a no.
            bool positive = pending.Kind == PlayerbotOpeningKind.Objective ? reply == PlayerbotInitiativeRules.Reply.Yes : reply != PlayerbotInitiativeRules.Reply.No;
            if (positive) _ignored.Remove(sender.Value);
            else
            {
                // Only a positive reply clears the back-off: a no (or any other answer) backs the bots off, with no penalty in affinity (the player did answer).
                int ignored = _ignored[sender.Value] = _ignored.GetValueOrDefault(sender.Value) + 1;
                _playerNext[sender.Value] = pass.NowMs + PlayerbotInitiativeRules.CooldownMs(options.OpeningCooldownMinutes, ignored);
            }

            if (pending.Kind != PlayerbotOpeningKind.Objective || reply != PlayerbotInitiativeRules.Reply.Yes) continue;
            Accepted++;
            if (!pass.ByCharacter.TryGetValue(pending.BotGuid.Low, out PlayerbotSocialBot? bot) || pending.Entry == 0 || bot.Brain is null) continue;
            RaiseSignal(pass, bot, pending.Entry, pending.Quest);
            // The coordinator matches that very player ahead of recruiting free bots (its checks still apply); until the bot waits for a group the offer is retried.
            if (_follow.Count < Bound) _follow.Add(new Follow(bot.Id, Guid.Empty, sender, pass.NowMs + FollowMs));
            FollowThrough(pass);
        }
    }

    /// <summary>
    /// Whether the line names a character the speaker could be talking to instead of the bot ("hey Bob, sure"): one in the bot's sight, on its map and within
    /// <c>BystanderRadiusYards</c> (earshot), other than the speaker and the bot. A reply word ("sure", "no", "thanks") is never a name, so a character called
    /// Sure swallows nothing; a character elsewhere in the realm is never considered.
    /// </summary>
    private static bool NamesAnother(PlayerbotSocialPass pass, string? text, ObjectGuid speaker, ObjectGuid botGuid)
    {
        if (string.IsNullOrEmpty(text) || !HasNameCandidate(text) || !pass.ByCharacter.TryGetValue(botGuid.Low, out PlayerbotSocialBot? bot)) return false; // a plain reply costs no scan
        Player self = bot.Player;
        HashSet<string>? near = null;
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == speaker || guid == botGuid) continue;
            if (++looked > SightLimit) break;
            if (pass.World.FindOnlinePlayer(guid) is { } other && ReferenceEquals(other.Map, self.Map) && Distance(self, other) <= pass.Options.Memory.BystanderRadiusYards)
                (near ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(other.Name);
        }

        if (near is null) return false;
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> names = near.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (Range range in text.AsSpan().SplitAny(NameSeparators))
        {
            ReadOnlySpan<char> word = text.AsSpan(range);
            if (IsNameCandidate(word) && names.Contains(word)) return true;
        }

        return false;
    }

    private static readonly char[] NameSeparators = [' ', ',', '.', '!', '?', ';', ':'];

    /// <summary>A word that could be a character's name: 3 to 12 letters long and not a reply word.</summary>
    private static bool IsNameCandidate(ReadOnlySpan<char> word) => word.Length is >= 3 and <= 12 && !PlayerbotInitiativeRules.IsReplyWord(word);

    private static bool HasNameCandidate(string text)
    {
        foreach (Range range in text.AsSpan().SplitAny(NameSeparators))
            if (IsNameCandidate(text.AsSpan(range))) return true;
        return false;
    }

    private static void RaiseSignal(PlayerbotSocialPass pass, PlayerbotSocialBot bot, uint entry, uint quest)
    {
        Player self = bot.Player;
        bot.Brain!.Risk.RaiseGroupSignal(new PlayerbotGroupSignal(entry, 2, self.MapId, new System.Numerics.Vector3(self.X, self.Y, self.Z), pass.World.NowMs,
            PlayerbotGroupSignal.ContentionSource, quest));
    }

    // A yes the coordinator could not act on yet (the bot does not wait for a group until its next tick) is offered again for a while.
    private void FollowThrough(PlayerbotSocialPass pass)
    {
        if (_follow.Count == 0) return;
        if (pass.Coordinator is not { } coordinator)
        {
            _follow.Clear();
            return;
        }

        for (int i = _follow.Count - 1; i >= 0; i--)
        {
            Follow follow = _follow[i];
            bool done = pass.NowMs >= follow.UntilMs;
            if (!done && follow.Answerer != Guid.Empty)
            {
                PlayerbotLfgOfferOutcome outcome = coordinator.OfferLfg(follow.Answerer, follow.Bot).Outcome;
                done = outcome != PlayerbotLfgOfferOutcome.NotWaiting;
                if (outcome == PlayerbotLfgOfferOutcome.Pledged) BotOffers++;
            }
            else if (!done)
            {
                done = pass.World.FindOnlinePlayer(follow.Player) is not { } player || coordinator.Volunteer(follow.Bot, player).Outcome != PlayerbotVolunteerOutcome.NotWaiting;
            }

            if (done) _follow.RemoveAt(i);
        }
    }

    private void Unanswered(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder)
    {
        if (_pending.Count == 0) return;
        foreach (KeyValuePair<(Guid Bot, ulong Other), Pending> entry in _pending)
        {
            if (pass.NowMs - entry.Value.AtMs < ReplyWaitMs) continue;
            _pending.Remove(entry.Key);
            recorder.Unexpect(entry.Key.Bot, new ObjectGuid(entry.Key.Other));
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
                NoteMount(bot.Id, other);
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
            // A greeting or a follow-up to a player who never opted in and never did anything toward the bot is unprompted: only a reason (an objective) justifies a line.
            bool mayGreet = !real || (known && (relation.Flags & PlayerbotRelationFlags.Interacted) != 0) || pass.Chat?.Safety.Player(other.Name)?.AiReplies == true;
            PlayerbotOpeningKind? kind = KindFor(pass, bot, other, known ? relation : null, arrival, traits.Sociability, mood, mayGreet);
            if (kind is not { } chosen) continue;
            if (Open(pass, recorder, options, bot, other, real, known ? relation : null, affinity, chosen))
            {
                budget--;
                return true;
            }
        }

        return budget > 0 && options.CommentsEnabled && TryComment(pass, recorder, options, bot, traits, mood);
    }

    /// <summary>The quest and creature a bot works on: its brain's, or, while it waits for a group, the goal it waits for (a waiting bot's own quest id is 0).</summary>
    private static (uint Quest, uint Entry) ObjectiveOf(PlayerbotSocialPass pass, PlayerbotSocialBot bot)
    {
        PlayerbotBrain brain = bot.Brain!;
        if (brain.QuestId != 0 && brain.TargetEntry != 0) return (brain.QuestId, brain.TargetEntry);
        return pass.Coordinator is { } coordinator && coordinator.TryGetWaitingGoal(bot.Id, out PlayerbotGroupGoal? goal) && goal.Kind == PlayerbotGroupGoalKind.Quest && goal.ObjectiveEntry != 0
            ? (goal.QuestId, goal.ObjectiveEntry) : (0, 0);
    }

    private PlayerbotOpeningKind? KindFor(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player other, PlayerbotRelation? relation, bool arrival, float sociability, float mood, bool mayGreet)
    {
        Player self = bot.Player;
        if (ObjectiveOf(pass, bot).Entry != 0 && pass.Coordinator?.SharesGoal(bot.Id, other) == true && Ready(bot.Id, other, PlayerbotOpeningKind.Objective, pass.NowMs)
            && PlayerbotInitiativeRules.Rolls(self.Guid.Value, other.Guid.Value, pass.NowMs, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Objective), PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, sociability, mood)))
            return PlayerbotOpeningKind.Objective;
        if (relation is null || !mayGreet) return null;
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
            : PlayerbotInitiativeRules.ChannelFor(chat.Safety.Player(other.Name)?.AiReplies == true, affinity, relation is not null && (relation.Flags & PlayerbotRelationFlags.Interacted) != 0);
        var values = new Dictionary<string, string>();
        string instruction;
        string key;
        uint entry = 0, quest = 0;
        switch (kind)
        {
            case PlayerbotOpeningKind.Objective:
                (quest, entry) = ObjectiveOf(pass, bot);
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

        // Everything that follows the line happens once it was really said (speech path, world thread): an opening the limiter or the place refused costs the player nothing.
        Guid botId = bot.Id;
        ObjectGuid botGuid = self.Guid, otherGuid = other.Guid;
        PlayerbotSocialBot? answerer = null;
        if (!real && kind == PlayerbotOpeningKind.Objective && entry != 0 && pass.ByCharacter.TryGetValue(otherGuid.Low, out PlayerbotSocialBot? found) && found.Brain is not null) answerer = found;
        long held = 0, heldFrom = 0;
        Action? dropped = null;
        Action sent = () =>
        {
            long said = _passNowMs;
            if (real)
            {
                if (pass.World.FindOnlinePlayer(otherGuid) is null) { dropped!(); return; } // gone since the line was queued: nobody to wait for
                _playerNext[otherGuid.Value] = said + PlayerbotInitiativeRules.CooldownMs(options.OpeningCooldownMinutes, _ignored.GetValueOrDefault(otherGuid.Value));
                Expect(recorder, botId, botGuid, otherGuid, kind, entry, quest, said);
            }
            else if (answerer is not null)
            {
                // The other bot answers the offer too: a hash of the two and the minute against its own sociability and mood. A yes is a recruit for this bot's group.
                PlayerbotTraits theirs = PlayerbotTraits.For(otherGuid.Value);
                if (PlayerbotInitiativeRules.Rolls(otherGuid.Value, botGuid.Value, said, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Objective) ^ 0xA25E12UL,
                        PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, theirs.Sociability, recorder.Mood.Get(answerer.Id))))
                {
                    RaiseSignal(pass, bot, entry, quest);
                    if (_follow.Count < Bound) _follow.Add(new Follow(botId, answerer.Id, otherGuid, said + FollowMs));
                }
            }
        };
        dropped = () =>
        {
            // Give the player back unless something else has set their next opening since (the hold is still exactly what this opening put there).
            if (real && _playerNext.TryGetValue(otherGuid.Value, out long current) && current == held) _playerNext[otherGuid.Value] = heldFrom;
        };
        var speech = new BotSpeech(channel, string.Empty, "initiative-" + kind.ToString().ToLowerInvariant(), BotSpeechPriority.Low) { TargetGuid = other.Guid, Sent = sent, Dropped = dropped };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), other.Guid, other.Name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0, Topic: new BotChatTopic(key, instruction, values, string.Empty), Speech: speech);
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;

        Openings++;
        ByKind[(int)kind]++;
        if (channel == BotSpeechChannel.Whisper) Whispers++;
        _pairNext[(bot.Id, other.Guid.Value, kind)] = pass.NowMs + PairGapMs;
        // Until the line is said or found never to be (BotSpeech.Dropped), a second bot must not pile on: the player is held. Saying the line sets the real cooldown; a dropped line gives the hold back.
        if (real)
        {
            heldFrom = _playerNext.GetValueOrDefault(other.Guid.Value);
            held = Math.Max(heldFrom, pass.NowMs + UnsaidHoldMs);
            _playerNext[other.Guid.Value] = held;
        }

        return true;
    }

    /// <summary>Wait for the answer of <paramref name="other"/> to an opening (also the tests' way to stage one): the player's next say within hearing is the reply.</summary>
    internal void Expect(PlayerbotSocialRecorder recorder, Guid bot, ObjectGuid botGuid, ObjectGuid other, PlayerbotOpeningKind kind, uint entry, uint quest, long nowMs)
    {
        if (!recorder.Expect(bot, other)) return; // too many openings awaited: this one is not waited for
        _pending[(bot, other.Value)] = new Pending(botGuid, nowMs, kind, entry, quest);
    }

    // --- surroundings comments -------------------------------------------------------------------------------------------------

    private void NoteEvents(PlayerbotSocialPass pass)
    {
        if (pass.Services.GetService<GameEventFeature>() is not { } feature) return;
        IReadOnlyCollection<ushort> active = feature.ActiveEvents; // a copy per read: read once a pass
        // Only a festival that starts is remarked on (its root event): the hourly bells, a member event and the war-effort events never are.
        if (_eventsKnown && pass.Events is { } board && pass.Options.Events.Enabled)
            foreach (ushort id in active)
                if (!_events.Contains(id) && board.IsFestivalRoot(id)) _newEvents.Add(id);
        _events.Clear();
        foreach (ushort id in active) _events.Add(id);
        _eventsKnown = true;
    }

    // A real player seen unmounted at level 40 or more by this bot: a later sight of them mounted is a first mount (memory only, so a restart forgets and stays quiet).
    private void NoteMount(Guid bot, Player other)
    {
        if (other.Level < FirstMountLevel || other.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0) return;
        if (_unmounted.Add((bot, other.Guid.Value))) _unmountedOrder.Enqueue((bot, other.Guid.Value));
        // a sighting removed here leaves its queue entry; the queue is capped at 2 x Bound and a stale entry evicts nothing
        while ((_unmounted.Count > Bound || _unmountedOrder.Count > 2 * Bound) && _unmountedOrder.TryDequeue(out (Guid, ulong) oldest)) _unmounted.Remove(oldest); // the oldest sightings go first
    }

    private bool TryComment(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialInitiativeOptions options, PlayerbotSocialBot bot, PlayerbotTraits traits, float mood)
    {
        Player self = bot.Player;
        if (_commentBot.TryGetValue(bot.Id, out long botUntil) && botUntil > pass.NowMs) return false;
        if (_commentZone.TryGetValue(self.ZoneId, out long zoneUntil) && zoneUntil > pass.NowMs) return false;
        if (!PlayerbotInitiativeRules.Rolls(self.Guid.Value, (ulong)self.ZoneId, pass.NowMs, PlayerbotInitiativeRules.CommentSalt, PlayerbotInitiativeRules.CommentChance(traits.Chattiness, mood))) return false;
        Player? listener = RealListener(pass, self);
        if (listener is null && _newEvents.Count == 0) return false; // a remark is for someone to hear

        string? key = null;
        string instruction = string.Empty;
        PlayerbotFestival? festival = null;
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
                    if (other.IsAlive && other.Session is WorldSession { IsManaged: false } && Distance(self, other) <= RaidYards) enemies++;
                }
                else if (mounted is null && other.Session is WorldSession { IsManaged: false } && other.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0 && !_mounted.Contains(guid.Value)
                    && _unmounted.Remove((bot.Id, guid.Value)))
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
        else if (_newEvents.Count > 0 && FestivalHere(pass, self) is { } here)
        {
            festival = here;
            key = "comment.event";
            instruction = $"The {here.Flavour.Name} has just started. Remark on it in one short line.";
        }
        else if (listener is not null && enemies >= RaidPlayers)
        {
            key = "comment.raid";
            instruction = "Many players of the enemy faction are around you. Remark on it in one short line.";
        }
        else if (listener is not null && mounted is not null)
        {
            key = "comment.mount";
            instruction = $"{mounted} has just got a first mount. Congratulate them in one short line.";
        }

        if (key is null || pass.Chat is not { IsActive: true } chat) return false;
        if (festival is not null) return CommentOnFestival(pass, options, bot, chat, festival, instruction);
        if (listener is null) return false;
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, key.Replace('.', '-'), BotSpeechPriority.Low) { TargetGuid = listener.Guid };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), listener.Guid, mounted ?? listener.Name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0, Topic: new BotChatTopic(key, instruction, new Dictionary<string, string>(), string.Empty), Speech: speech);
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;
        Comments++;
        _commentBot[bot.Id] = pass.NowMs + options.CommentBotGapMinutes * 60_000L;
        _commentZone[self.ZoneId] = pass.NowMs + options.CommentZoneGapSeconds * 1000L;
        return true;
    }

    // The first festival that just started and has an anchor NPC in the zone the bot stands in.
    private PlayerbotFestival? FestivalHere(PlayerbotSocialPass pass, Player self)
    {
        if (pass.Events is not { } board) return null;
        uint zone = board.ZoneAt(self.MapId, new System.Numerics.Vector3(self.X, self.Y, self.Z));
        foreach (ushort root in _newEvents)
            if (board.HasAnchorInZone(root, self.MapId, zone) && board.FestivalOf(root) is { } festival)
                return festival;
        return null;
    }

    // The remark on a festival that started: heard by real players of either faction in /say range, so it goes through the listener filter,
    // the one ledger of who was addressed lately, and is built-in when a listener said "ai off".
    private bool CommentOnFestival(PlayerbotSocialPass pass, PlayerbotSocialInitiativeOptions options, PlayerbotSocialBot bot, PlayerbotChat chat, PlayerbotFestival festival, string instruction)
    {
        Player self = bot.Player;
        PlayerbotListenerResult heard = PlayerbotEventLane.ListenersOf(pass, self, pass.World.Options.ListenRangeSay);
        if (heard.Verdict != PlayerbotListenVerdict.Heard)
        {
            PlayerbotEventMeters.Suppress(heard.Verdict switch
            {
                PlayerbotListenVerdict.Ignored => PlayerbotEventMeters.Ignored,
                PlayerbotListenVerdict.CutOff => PlayerbotEventMeters.CutOff,
                _ => PlayerbotEventMeters.NoListener,
            });
            return false;
        }

        foreach (ulong listener in heard.Listeners)
            if (!MayAddress(new ObjectGuid(listener), pass.NowMs))
            {
                PlayerbotEventMeters.Suppress(PlayerbotEventMeters.ListenerCap);
                return false;
            }

        ObjectGuid first = new(heard.Listeners[0]);
        string name = pass.World.FindOnlinePlayer(first)?.Name ?? string.Empty;
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, "comment-event", BotSpeechPriority.Low) { TargetGuid = first };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), first, name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0, Topic: new BotChatTopic("comment.event", instruction, new Dictionary<string, string> { ["event"] = festival.Flavour.Name }, string.Empty), Speech: speech)
        { BuiltinOnly = heard.BuiltinOnly };
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;
        Comments++;
        PlayerbotEventMeters.Line(PlayerbotEventMeters.Comment);
        foreach (ulong listener in heard.Listeners) NoteAddressed(new ObjectGuid(listener), pass.NowMs, options.OpeningCooldownMinutes);
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
