using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>How a bot regards another character, from the bot's memory of it (<see cref="PlayerbotRivalryRules.StanceOf"/>).</summary>
public enum PlayerbotStance : byte
{
    /// <summary>A stranger, or known with an affinity from 0 up to the friendly threshold.</summary>
    Neutral,

    /// <summary>Known, with an affinity at or above <c>Rivalry:Emotes:FriendlyAffinity</c>.</summary>
    Friendly,

    /// <summary>Known, with an affinity below 0, and not a rival.</summary>
    Disliked,

    /// <summary>The layer's Rival flag is set.</summary>
    Rival,
}

/// <summary>The layer of a bot's memory a decision reads: the normal one, or the war layer (read while War Mode is on).</summary>
internal enum PlayerbotRivalryLayer : byte
{
    Normal,
    War,
}

/// <summary>
/// The pure rules of the rivalry stage (docs/areas/playbots-social.md, Rivalry): the stance, the greeting and rude picks, the stance-aware reply
/// table, the hash rolls and the quiet places. No world and no clock; every choice is a function of its arguments, so it is deterministic and
/// testable. A roll is a double in [0, 1); a pick takes <c>(int)(roll * choices)</c> of its list.
/// </summary>
internal static class PlayerbotRivalryRules
{
    /// <summary>The length of the window a roll stands for, in milliseconds (thirty minutes): inside it a bot's answer to one character does not change.</summary>
    internal const long BucketMs = 30 * 60_000L;

    private static readonly uint[] FriendlyGreetings =
    [
        PlayerbotSocialEmotes.TextEmotes.Wave, PlayerbotSocialEmotes.TextEmotes.Bow, PlayerbotSocialEmotes.TextEmotes.Salute,
        PlayerbotSocialEmotes.TextEmotes.Cheer, PlayerbotSocialEmotes.TextEmotes.Hail,
    ];

    private static readonly uint[] NeutralGreetings =
        [PlayerbotSocialEmotes.TextEmotes.Wave, PlayerbotSocialEmotes.TextEmotes.Nod, PlayerbotSocialEmotes.TextEmotes.Bow];

    private static readonly uint[] RudeEmotes =
    [
        PlayerbotSocialEmotes.TextEmotes.Rude, PlayerbotSocialEmotes.TextEmotes.Chicken, PlayerbotSocialEmotes.TextEmotes.Threaten,
        PlayerbotSocialEmotes.TextEmotes.Mock, PlayerbotSocialEmotes.TextEmotes.Violin,
    ];

    private const ulong GreetSalt = 0x6A09E667F3BCC908UL, PickSalt = 0xBB67AE8584CAA73BUL, ReplySalt = 0x3C6EF372FE94F82BUL;

    // --- stance -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The stance of a bot toward a character it holds <paramref name="relation"/> with (null: a stranger), reading <paramref name="layer"/> only: the
    /// layer's Rival flag is a Rival; an affinity below 0 a Disliked; at least <paramref name="friendlyAffinity"/> a Friendly; the rest Neutral. A relation
    /// the normal rules have evicted and only the war layer keeps is a stranger to the normal layer.
    /// </summary>
    internal static PlayerbotStance StanceOf(PlayerbotRelation? relation, PlayerbotRivalryLayer layer, int friendlyAffinity)
    {
        if (relation is null) return PlayerbotStance.Neutral;
        int affinity;
        bool rival;
        if (layer == PlayerbotRivalryLayer.War)
        {
            affinity = relation.WarAffinity;
            rival = (relation.WarFlags & PlayerbotRelationFlags.Rival) != 0;
        }
        else
        {
            // Under War Mode TryGet also returns the blank row of a relation only the war layer keeps (the normal rules evicted it): a stranger here.
            if (!IsKnown(relation, layer)) return PlayerbotStance.Neutral;
            affinity = relation.Affinity;
            rival = (relation.Flags & PlayerbotRelationFlags.Rival) != 0;
        }

        if (rival) return PlayerbotStance.Rival;
        if (affinity < 0) return PlayerbotStance.Disliked;
        return affinity >= friendlyAffinity ? PlayerbotStance.Friendly : PlayerbotStance.Neutral;
    }

    /// <summary>
    /// Whether the bot knows the character in <paramref name="layer"/>: the war layer sees every relation row; the normal layer only a row that has
    /// familiarity or an affinity of its own (the blank row of a relation only the war layer keeps is a stranger).
    /// A known row at affinity 0 with no familiarity is a stranger here even when <c>Memory:RivalAffinity</c> is 0 (the range ends there) and memory calls it a rival.
    /// </summary>
    internal static bool IsKnown(PlayerbotRelation? relation, PlayerbotRivalryLayer layer)
        => relation is not null && (layer == PlayerbotRivalryLayer.War || relation.Familiarity > 0 || relation.Affinity != 0);

    // --- picks and chances --------------------------------------------------------------------------------------------------------

    /// <summary>The greeting a bot sends: for a Friendly one of wave, bow, salute, cheer and hail; for any other stance one of wave, nod and bow.</summary>
    internal static uint PickGreeting(PlayerbotStance stance, double roll) => Pick(stance == PlayerbotStance.Friendly ? FriendlyGreetings : NeutralGreetings, roll);

    /// <summary>The rude emote a bot sends a rival: one of rude, chicken, threaten, mock and violin (all of which <see cref="PlayerbotEmoteMoods.Classify"/> calls Rude).</summary>
    internal static uint PickRude(double roll) => Pick(RudeEmotes, roll);

    /// <summary>The chance that a bot greets a character: a friend always; a known neutral half the bot's sociability; a stranger <paramref name="strangerChance"/> times it.</summary>
    internal static double GreetChance(PlayerbotStance stance, bool known, double strangerChance, double sociability)
    {
        if (stance == PlayerbotStance.Friendly) return 1.0;
        double chance = known ? 0.5 * sociability : strangerChance * sociability;
        return Math.Clamp(chance, 0.0, 1.0);
    }

    // --- rolls --------------------------------------------------------------------------------------------------------------------

    /// <summary>The roll that decides whether <paramref name="bot"/> greets <paramref name="other"/> in the thirty-minute bucket of <paramref name="nowMs"/>.</summary>
    internal static double GreetRoll(ulong bot, ulong other, long nowMs) => Unit(Hash(GreetSalt, bot, other, Bucket(nowMs)));

    /// <summary>The roll that picks the emote <paramref name="bot"/> sends <paramref name="other"/> in the bucket of <paramref name="nowMs"/> (a stream apart from <see cref="GreetRoll"/>).</summary>
    internal static double PickRoll(ulong bot, ulong other, long nowMs) => Unit(Hash(PickSalt, bot, other, Bucket(nowMs)));

    /// <summary>The roll that decides how <paramref name="bot"/> answers <paramref name="emote"/> from <paramref name="emoter"/> in the bucket of <paramref name="nowMs"/>.</summary>
    internal static double ReplyRoll(ulong bot, ulong emoter, uint emote, long nowMs) => Unit(Hash(ReplySalt ^ emote, bot, emoter, Bucket(nowMs)));

    private static ulong Bucket(long nowMs) => (ulong)(nowMs >= 0 ? nowMs / BucketMs : -1 - (-nowMs - 1) / BucketMs);

    private static ulong Hash(ulong salt, ulong a, ulong b, ulong c) => PlayerbotContention.Mix(PlayerbotContention.Mix(PlayerbotContention.Mix(salt, a), b), c);

    private static double Unit(ulong hash) => (hash >> 11) * (1.0 / 9007199254740992.0);

    private static uint Pick(uint[] choices, double roll) => choices[Math.Min(choices.Length - 1, (int)(roll * choices.Length))];

    // --- replies --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// How a bot of <paramref name="stance"/> answers the text emote <paramref name="emote"/> aimed at it (null: not at all). Toward a real player: a
    /// Rival answers a friendly emote with a rude one or nothing and a rude one rudely; a Disliked answers a friendly emote with a shrug or nothing and a
    /// rude one rudely; a Friendly answers a rude emote with a shrug or a sigh; every other pairing is <see cref="PlayerbotSocialEmotes.AnswersFor"/>,
    /// as before the stance existed. Toward another bot nothing that classifies Rude is sent unless <paramref name="rudeAtBots"/> (a shrug instead),
    /// so no exchange between two bots can lower an affinity. An opted-out player is not answered.
    /// </summary>
    internal static uint? ReplyTo(PlayerbotStance stance, uint emote, bool toBot, double roll, bool rudeAtBots = false, bool optedOut = false)
    {
        if (optedOut) return null;
        bool rudeAllowed = !toBot || rudeAtBots;
        PlayerbotEmoteMood mood = PlayerbotEmoteMoods.Classify(emote);
        uint? answer;
        if (mood == PlayerbotEmoteMood.Friendly && stance == PlayerbotStance.Rival)
            answer = roll < 0.5 ? null : rudeAllowed ? PickRude((roll - 0.5) * 2.0) : PlayerbotSocialEmotes.TextEmotes.Shrug;
        else if (mood == PlayerbotEmoteMood.Friendly && stance == PlayerbotStance.Disliked)
            answer = roll < 0.5 ? null : PlayerbotSocialEmotes.TextEmotes.Shrug;
        else if (mood == PlayerbotEmoteMood.Rude && stance is PlayerbotStance.Rival or PlayerbotStance.Disliked)
            answer = rudeAllowed ? PickRude(roll) : ShrugOrSigh(roll);
        else if (mood == PlayerbotEmoteMood.Rude && stance == PlayerbotStance.Friendly)
            answer = ShrugOrSigh(roll);
        else if (emote is PlayerbotSocialEmotes.TextEmotes.Rude or PlayerbotSocialEmotes.TextEmotes.Chicken && stance == PlayerbotStance.Neutral && !rudeAllowed)
            answer = ShrugOrSigh(roll); // toward a bot the table's [rude, shrug, sigh] would fold the rude pick into a shrug (a sigh a third of the time); design 4.2 says shrug or sigh
        else
            answer = PlayerbotSocialEmotes.AnswersFor(emote) is { } answers ? Pick(answers, roll) : null;
        // Whatever the table said, a bot is never sent a rude answer without RudeAtBots (today's rude-for-rude and chicken-for-threaten included).
        if (!rudeAllowed && answer is { } given && PlayerbotEmoteMoods.Classify(given) == PlayerbotEmoteMood.Rude) return PlayerbotSocialEmotes.TextEmotes.Shrug;
        return answer;
    }

    private static uint ShrugOrSigh(double roll) => roll < 0.5 ? PlayerbotSocialEmotes.TextEmotes.Shrug : PlayerbotSocialEmotes.TextEmotes.Sigh;

    // --- quiet places -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a zone is one where no rude emote is sent. Fails closed: a zone id of 0, no area table, or a zone the table does not hold is quiet, and
    /// so is every zone flagged <see cref="AreaFlags.Capital"/>.
    /// </summary>
    internal static bool IsQuietZone(AreaTable? areas, uint zoneId)
    {
        if (zoneId == 0 || areas is null || areas.Count == 0) return true;
        return areas.GetById(zoneId) is not { } zone || (zone.Flags & (uint)AreaFlags.Capital) != 0;
    }

    /// <summary>Whether a place is quiet: its zone is (<see cref="IsQuietZone"/>) or the area it stands in (an area id, not a zone) is one of <paramref name="safeAreas"/>.</summary>
    internal static bool IsQuietPlace(AreaTable? areas, uint zoneId, uint areaId, IReadOnlySet<uint> safeAreas)
        => IsQuietZone(areas, zoneId) || safeAreas.Contains(areaId);

    /// <summary>
    /// Whether <paramref name="player"/> stands in a quiet place (<see cref="IsQuietPlace"/>). The area under it is only looked up for a zone that is not
    /// already quiet and when <paramref name="safeAreas"/> is not empty. World thread.
    /// </summary>
    internal static bool IsQuiet(Player player, AreaTable? areas, IReadOnlySet<uint> safeAreas)
    {
        uint zone = player.ZoneId;
        uint area = 0;
        if (!IsQuietZone(areas, zone) && safeAreas.Count > 0 && player.Map is { } map)
            area = map.GetZoneAndAreaId(player.X, player.Y, player.Z).AreaId;
        return IsQuietPlace(areas, zone, area, safeAreas);
    }
}

/// <summary>One emote the lane sent (tests and status): who sent it, to whom, whether the target is a bot, which emote and whether it was rude.</summary>
internal sealed record PlayerbotRivalrySend(Guid Bot, ulong Target, bool TargetIsBot, uint Emote, bool Rude);

/// <summary>
/// Lane <c>rivalry</c> of the social pass (<c>World:Playerbots:Social:Rivalry</c>, docs/areas/playbots-social.md, Rivalry): idle bots greet
/// characters of the other faction they are neutral or friendly to, and send a rude emote at a rival. World thread only, no combat, no model.
/// <list type="bullet">
/// <item>Up to <see cref="BotsPerPass"/> autonomous, idle bots a pass, round robin; the cursor moves by the bots looked at, never by what was sent.</item>
/// <item>A target is of the other team, alive, out of combat, on the bot's map, within <c>min(RangeYards, World:ListenRangeTextEmote)</c> (read each
/// pass) and not ignoring the bot. A bot target is only emoted at when <c>BotToBot</c> is on and a real player is within the listen range of the bot.</item>
/// <item>Toward a bot only the normal layer of the memory decides. Toward a real player the layer every memory decision reads does (the war layer
/// under War Mode). Limiters and budgets are kept apart for the two kinds of target, and every emote is a direct, unbudgeted CMSG_TEXT_EMOTE, so
/// nothing the war layer does here can change what a bot sends or says to another bot.</item>
/// <item>A rude emote needs the Rival stance, <c>RivalEmotes</c>, no opt-out of the player (<c>rivals off</c>; with <c>RudeRequiresOptIn</c> a
/// <c>rivals on</c>), <c>RudeAtBots</c> for a bot, and neither character in a capital, a safe area or a place the zone table cannot place. It is
/// limited per pair, per bot (ten minutes) and per target from all bots together.</item>
/// <item>A greeting rolls a hash of bot, target and the half hour; a failed roll leaves the target out for the bucket and the walk goes on to the next.</item>
/// </list>
/// The first rude emote a real player receives in a login is followed by one system notice that says how to stop them.
/// </summary>
internal sealed class PlayerbotRivalryLane(PlayerbotRivalryOptOut optOut)
{
    /// <summary>The most bots looked at per pass (a round robin over the running bots).</summary>
    internal const int BotsPerPass = 24;

    /// <summary>The most players one bot looks at per pass.</summary>
    internal const int SightLimit = 64;

    /// <summary>The sends kept for <see cref="Sends"/>.</summary>
    internal const int SendsKept = 1024;

    /// <summary>The least time between two rude emotes from one bot, in milliseconds.</summary>
    internal const long RudeBotGapMs = 10 * 60_000L;

    private const long MinuteMs = 60_000L;

    private readonly record struct Candidate(Player Player, float Yards, PlayerbotStance Stance, bool Known);

    // One set of limiters per kind of target (a bot, a real player): what is sent to one never changes what may be sent to the other.
    private sealed class Limits
    {
        public readonly PlayerbotSocialRateLimiter<(Guid Bot, ulong Target)> GreetPairs = new();
        public readonly PlayerbotSocialRateLimiter<ulong> GreetTargets = new();
        public readonly PlayerbotSocialRateLimiter<Guid> GreetBots = new();
        public readonly PlayerbotSocialRateLimiter<(Guid Bot, ulong Target)> RudePairs = new();
        public readonly PlayerbotSocialRateLimiter<ulong> RudeTargets = new();
        public readonly PlayerbotSocialRateLimiter<Guid> RudeBots = new();
        public readonly PlayerbotSocialRateLimiter<int> Minute = new();
    }

    private readonly Limits _players = new(), _bots = new();
    private readonly List<Candidate> _playerCandidates = [], _botCandidates = [];
    private readonly Queue<PlayerbotRivalrySend> _sends = new();
    private readonly object _sendsGate = new();
    private int _cursor;
    private long _sweptAtMs;

    // The pass in hand (world thread, one Run at a time).
    private PlayerbotSocialPass _pass = null!;
    private PlayerbotSocialRivalryOptions _rivalry = null!;
    private PlayerbotSocialRecorder? _recorder;
    private AreaTable? _areas;
    private IReadOnlySet<uint> _safeAreas = null!;
    private bool _warMode;
    private float _range, _listen;
    private long _now;
    private int _selfQuiet;

    /// <summary>The server-wide opt-out store ("rivals off") this lane honours.</summary>
    internal PlayerbotRivalryOptOut OptOut => optOut;

    /// <summary>Where the next pass starts in the running bots (tests and status): it moves on by the bots looked at, at most <see cref="BotsPerPass"/>.</summary>
    internal int Cursor => _cursor;

    /// <summary>Greetings sent so far (tests and status).</summary>
    internal int Greeted { get; private set; }

    /// <summary>Rude emotes sent so far (tests and status).</summary>
    internal int Taunted { get; private set; }

    /// <summary>The last <see cref="SendsKept"/> emotes the lane sent, oldest first (a snapshot).</summary>
    internal IReadOnlyList<PlayerbotRivalrySend> Sends
    {
        get
        {
            lock (_sendsGate) return _sends.ToArray();
        }
    }

    internal void Run(PlayerbotSocialPass pass)
    {
        PlayerbotSocialRivalryOptions rivalry = pass.Options.Rivalry;
        if (!rivalry.Enabled || !rivalry.Emotes.Enabled) return;
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count == 0) return;
        _pass = pass;
        _rivalry = rivalry;
        _recorder = pass.Recorder is { Enabled: true } recorder ? recorder : null;
        _areas = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(pass.World).Areas;
        _safeAreas = rivalry.Safeguards.SafeAreaIds();
        _warMode = pass.Options.WarMode.Enabled;
        _listen = pass.World.Options.ListenRangeTextEmote;
        _range = Math.Min(rivalry.Emotes.RangeYards, _listen);
        _now = pass.NowMs;
        try
        {
            Sweep();
            int count = Math.Min(BotsPerPass, bots.Count);
            for (int i = 0; i < count; i++)
            {
                PlayerbotSocialBot bot = bots[(_cursor + i) % bots.Count];
                if (!IsIdle(bot)) continue;
                Gather(bot, out bool watched);
                _selfQuiet = 0;
                if (_playerCandidates.Count > 0) Act(bot, _playerCandidates, toBots: false);
                if (watched && rivalry.Emotes.BotToBot && _botCandidates.Count > 0) Act(bot, _botCandidates, toBots: true);
            }

            _cursor = (_cursor + count) % bots.Count;
        }
        finally
        {
            _playerCandidates.Clear();
            _botCandidates.Clear();
            _pass = null!;
            _recorder = null;
        }
    }

    // The filters of PlayerbotSocialEmotes.Run, and no battleground or instance (the battleground AI and the dungeon runs own those).
    private static bool IsIdle(PlayerbotSocialBot bot)
    {
        Player self = bot.Player;
        return !bot.PartyDriven && !bot.GroupDriven && self.IsAlive && !self.Combat.IsInCombat && self.Duel is null
            && self.Map is { } map && map.Template is not { Instanceable: true };
    }

    private void Sweep()
    {
        if (_now - _sweptAtMs < MinuteMs) return;
        _sweptAtMs = _now;
        SweepKind(_players);
        SweepKind(_bots);
    }

    private void SweepKind(Limits limits)
    {
        PlayerbotSocialTimings timings = _pass.Timings;
        limits.GreetPairs.Sweep(_now, timings.EmotePairGapMs);
        limits.GreetTargets.Sweep(_now, timings.EmotePlayerGapMs);
        limits.GreetBots.Sweep(_now, timings.EmoteBotGapMs);
        limits.RudePairs.Sweep(_now, _rivalry.Emotes.RudePairGapMinutes * 60_000L);
        limits.RudeTargets.Sweep(_now, _rivalry.Emotes.RudePerTargetMinutes * 60_000L);
        limits.RudeBots.Sweep(_now, RudeBotGapMs);
        limits.Minute.Sweep(_now, MinuteMs);
    }

    // --- candidates ---------------------------------------------------------------------------------------------------------------------

    private void Gather(PlayerbotSocialBot bot, out bool watched)
    {
        _playerCandidates.Clear();
        _botCandidates.Clear();
        watched = false;
        Player self = bot.Player;
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == self.Guid) continue;
            if (++looked > SightLimit) break;
            if (_pass.World.FindOnlinePlayer(guid) is not { } other || !ReferenceEquals(other.Map, self.Map)) continue;
            bool isBot = other.Session is WorldSession { IsManaged: true };
            float yards = Distance(self, other);
            if (!isBot && yards <= _listen) watched = true;
            if (other.Team == self.Team || !other.IsAlive || other.Combat.IsInCombat || other.Duel is not null || yards > _range) continue;
            if (_pass.Social.IsIgnoring(other, self.Guid)) continue;
            // Toward a bot the normal layer decides, War Mode or not; toward a real player the layer every memory decision reads.
            PlayerbotRivalryLayer layer = !isBot && _warMode ? PlayerbotRivalryLayer.War : PlayerbotRivalryLayer.Normal;
            PlayerbotRelation? relation = _recorder is not null && _recorder.Memory.TryGet(bot.Id, other.Guid.Value, out PlayerbotRelation row) ? row : null;
            PlayerbotStance stance = PlayerbotRivalryRules.StanceOf(relation, layer, _rivalry.Emotes.FriendlyAffinity);
            (isBot ? _botCandidates : _playerCandidates).Add(new Candidate(other, yards, stance, PlayerbotRivalryRules.IsKnown(relation, layer)));
        }

        // Nearest first; the guid breaks a tie, so the order does not depend on how the objects happen to be stored.
        _playerCandidates.Sort(ByDistance);
        _botCandidates.Sort(ByDistance);
    }

    private static int ByDistance(Candidate a, Candidate b)
    {
        int byYards = a.Yards.CompareTo(b.Yards);
        return byYards != 0 ? byYards : a.Player.Guid.Value.CompareTo(b.Player.Guid.Value);
    }

    // --- one bot, one kind of target -------------------------------------------------------------------------------------------------------

    private void Act(PlayerbotSocialBot bot, List<Candidate> candidates, bool toBots)
    {
        PlayerbotSocialRivalryEmoteOptions emotes = _rivalry.Emotes;
        Limits limits = toBots ? _bots : _players;
        if (!limits.Minute.HasRoom(0, emotes.EmotesPerMinute, _now, MinuteMs)) return;
        // A rival comes first, so a stranger standing nearer does not use the bot's turn up.
        if (emotes.RivalEmotes && (!toBots || emotes.RudeAtBots))
            foreach (Candidate candidate in candidates)
                if (TryRude(bot, candidate, limits, toBots)) return;
        foreach (Candidate candidate in candidates)
            if (TryGreet(bot, candidate, limits, toBots)) return;
    }

    private bool TryRude(PlayerbotSocialBot bot, Candidate candidate, Limits limits, bool toBot)
    {
        if (candidate.Stance != PlayerbotStance.Rival) return false;
        PlayerbotSocialRivalryEmoteOptions emotes = _rivalry.Emotes;
        Player target = candidate.Player;
        ulong targetGuid = target.Guid.Value;
        if (!limits.RudeBots.GapElapsed(bot.Id, RudeBotGapMs, _now)
            || !limits.RudeTargets.GapElapsed(targetGuid, emotes.RudePerTargetMinutes * 60_000L, _now)
            || !limits.RudePairs.GapElapsed((bot.Id, targetGuid), emotes.RudePairGapMinutes * 60_000L, _now)) return false;
        if (!toBot)
        {
            // An opt-out is honoured whatever AllowOptOut says; with RudeRequiresOptIn only a player who said "rivals on" is taunted.
            bool? choice = optOut.ChoiceOf(targetGuid, target.AccountId);
            if (choice == false || (emotes.RudeRequiresOptIn && choice != true)) return false;
        }

        if (IsQuiet(bot.Player, self: true) || IsQuiet(target, self: false)) return false;
        uint emote = PlayerbotRivalryRules.PickRude(PlayerbotRivalryRules.PickRoll(bot.Player.Guid.Value, targetGuid, _now));
        if (!Send(bot, target, emote)) return false;
        limits.RudeBots.MarkSpoke(bot.Id, _now);
        limits.RudeTargets.MarkSpoke(targetGuid, _now);
        limits.RudePairs.MarkSpoke((bot.Id, targetGuid), _now);
        limits.Minute.TryTake(0, emotes.EmotesPerMinute, _now, MinuteMs);
        Taunted++;
        Record(new PlayerbotRivalrySend(bot.Id, targetGuid, toBot, emote, Rude: true));
        PlayerbotRivalryMeters.CountEmote("rude");
        if (!toBot && !emotes.RudeRequiresOptIn && optOut.TakeDisclosure(targetGuid, target.AccountId, PlayerbotChatSafety.LoginOf(target)) is { } notice)
            Gm.Core.GmReplies.SendSystemMessage(target, notice);
        return true;
    }

    private bool TryGreet(PlayerbotSocialBot bot, Candidate candidate, Limits limits, bool toBot)
    {
        if (candidate.Stance is PlayerbotStance.Rival or PlayerbotStance.Disliked) return false;
        PlayerbotSocialTimings timings = _pass.Timings;
        Player target = candidate.Player;
        ulong targetGuid = target.Guid.Value, botGuid = bot.Player.Guid.Value;
        double chance = PlayerbotRivalryRules.GreetChance(candidate.Stance, candidate.Known, _rivalry.Emotes.GreetStrangerChance, PlayerbotTraits.For(botGuid).Sociability);
        // A failed roll excludes the target for the bucket: the roll is a hash of the half hour, so the walk goes on to the next one.
        if (PlayerbotRivalryRules.GreetRoll(botGuid, targetGuid, _now) >= chance) return false;
        if (!limits.GreetBots.GapElapsed(bot.Id, timings.EmoteBotGapMs, _now)
            || !limits.GreetTargets.GapElapsed(targetGuid, timings.EmotePlayerGapMs, _now)
            || !limits.GreetPairs.GapElapsed((bot.Id, targetGuid), timings.EmotePairGapMs, _now)) return false;
        uint emote = PlayerbotRivalryRules.PickGreeting(candidate.Stance, PlayerbotRivalryRules.PickRoll(botGuid, targetGuid, _now));
        if (!Send(bot, target, emote)) return false;
        limits.GreetBots.MarkSpoke(bot.Id, _now);
        limits.GreetTargets.MarkSpoke(targetGuid, _now);
        limits.GreetPairs.MarkSpoke((bot.Id, targetGuid), _now);
        limits.Minute.TryTake(0, _rivalry.Emotes.EmotesPerMinute, _now, MinuteMs);
        Greeted++;
        Record(new PlayerbotRivalrySend(bot.Id, targetGuid, toBot, emote, Rude: false));
        PlayerbotRivalryMeters.CountEmote("greet");
        return true;
    }

    private static bool Send(PlayerbotSocialBot bot, Player target, uint emote)
        => PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgTextEmote, PlayerbotSocialPackets.TextEmote(emote, target.Guid));

    // Capitals, the safe areas and any place the zone table cannot place (design: fail closed). The area is only looked up for a zone that is not already quiet.
    private bool IsQuiet(Player player, bool self)
    {
        if (self && _selfQuiet != 0) return _selfQuiet == 1;
        bool quiet = PlayerbotRivalryRules.IsQuiet(player, _areas, _safeAreas);
        if (self) _selfQuiet = quiet ? 1 : 2;
        return quiet;
    }

    private void Record(PlayerbotRivalrySend send)
    {
        lock (_sendsGate)
        {
            if (_sends.Count >= SendsKept) _sends.Dequeue();
            _sends.Enqueue(send);
        }
    }
}
