using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>
/// The social lane "events" (docs/areas/playbots-social.md, World events): what the bots held at a festival spot say and do there. For every
/// live claim of the board whose bot is held at its spot, alive and out of combat, it makes
/// <list type="bullet">
/// <item>one arrival line per claim (a new place is a new arrival): a /say line <c>event.&lt;flavour&gt;.arrive</c> with the chance
/// <c>0.6 x (0.5 + chattiness) x mood</c>, through the listener filter (real players of either faction in /say range; none, an ignoring
/// or a cut-off one: no line), the initiative's per-player ledger (festival lines and openings together reach a player once per opening
/// cooldown), the bot and zone gaps, and built-in text only when a listener said "ai off";</item>
/// <item>an untargeted text emote every <c>EmoteMinSeconds..EmoteMaxSeconds</c> (shorter for sociable bots) from the festival's emote set,
/// sent only when every real player in text-emote range has room in its per-minute budget and the anchor has room in its own.</item>
/// </list>
/// Nothing here is aimed at a bot, so festivals add no affinity source (a targeted friendly emote at a bot would feed the memory). All of it
/// reads the options live every pass. Thread affinity: the world thread (the social pass). State: per-bot arrival and emote clocks, the bot
/// and zone gap tables and the emote budget, all pruned once a minute; the time base is only <see cref="PlayerbotSocialPass.NowMs"/>.
/// </summary>
internal sealed class PlayerbotEventLane(PlayerbotInitiativeLane initiative)
{
    private const long PruneGapMs = 60_000;
    private const double ArrivalBase = 0.6;
    private const ulong ArrivalSalt = 0xA771_FA11UL;
    private const ulong EmoteSalt = 0xE307_0BE5UL;
    private const ulong PickSalt = 0x91C4_3AA7UL;
    private const int SightLimit = 96;

    private sealed class BotState
    {
        internal long ArrivedSerial = -1;
        internal long NextEmoteMs;
        internal long Draws;
    }

    private readonly Dictionary<Guid, BotState> _states = [];
    private readonly Dictionary<Guid, PlayerbotSocialBot> _byId = [];
    private readonly Dictionary<Guid, long> _botNext = [];
    private readonly Dictionary<uint, long> _zoneNext = [];
    private readonly Dictionary<string, int> _suppressed = [];
    private readonly PlayerbotEmoteBudget _budget = new();
    private long _prunedAtMs;

    /// <summary>A roll in [0, 1) for a bot and a salt, replacing the hash (tests make a lane eager with <c>(_, _) =&gt; 0.0</c>).</summary>
    internal Func<ulong, long, double>? RollOverride { get; set; }

    /// <summary>Arrival lines queued so far (tests and status).</summary>
    internal int Lines { get; private set; }

    /// <summary>Celebration emotes sent so far (tests and status).</summary>
    internal int EmotesSent { get; private set; }

    /// <summary>How often a line or emote was held back for a reason (<see cref="PlayerbotEventMeters"/> names).</summary>
    internal int Suppressed(string reason) => _suppressed.GetValueOrDefault(reason);

    private void Hold(string reason)
    {
        _suppressed[reason] = _suppressed.GetValueOrDefault(reason) + 1;
        PlayerbotEventMeters.Suppress(reason);
    }

    private double Roll(ulong botGuid, long salt)
        => RollOverride is { } fixedRoll ? fixedRoll(botGuid, salt)
            : (PlayerbotContention.Mix(PlayerbotContention.Mix(botGuid, (ulong)salt), ArrivalSalt) >> 11) * (1.0 / 9007199254740992.0);

    internal void Run(PlayerbotSocialPass pass)
    {
        if (pass.Events is not { } board) return;
        PlayerbotSocialEventsOptions events = pass.Options.Events;
        long now = pass.NowMs;
        if (!events.Enabled || !events.Holidays.Enabled) return;
        IReadOnlyList<PlayerbotFestivalClaim> claims = board.Claims.Active(now);
        if (claims.Count == 0)
        {
            Prune(board, now);
            return;
        }

        _byId.Clear();
        foreach (PlayerbotSocialBot bot in pass.Bots) _byId[bot.Id] = bot;
        PlayerbotHolidayOptions holidays = events.Holidays;
        for (int i = 0; i < claims.Count; i++)
        {
            PlayerbotFestivalClaim claim = claims[i];
            if (!_byId.TryGetValue(claim.Bot, out PlayerbotSocialBot? bot) || board.FestivalOf(claim.Anchor.Root) is not { } festival) continue;
            Player self = bot.Player;
            if (!self.IsAlive || self.Combat.IsInCombat || self.Map is null) continue;
            if (!PlayerbotFestivalRules.IsHeldAtSpot(Distance2D(self, claim.Spot), self.Z - claim.Spot.Z, Distance2D(self, claim.Anchor.Position))) continue;
            if (!_states.TryGetValue(claim.Bot, out BotState? state)) _states[claim.Bot] = state = new BotState();
            if (state.ArrivedSerial != claim.Serial)
            {
                state.ArrivedSerial = claim.Serial;
                if (holidays.TalkEnabled) Arrive(pass, board, holidays, bot, claim, festival);
            }

            Celebrate(pass, holidays, bot, claim, festival, state, now);
        }

        Prune(board, now);
    }

    private static float Distance2D(Player player, System.Numerics.Vector3 spot)
    {
        float dx = player.X - spot.X, dy = player.Y - spot.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    // --- the arrival line ---------------------------------------------------------------------------------------------------

    private void Arrive(PlayerbotSocialPass pass, PlayerbotWorldEventBoard board, PlayerbotHolidayOptions holidays, PlayerbotSocialBot bot, PlayerbotFestivalClaim claim, PlayerbotFestival festival)
    {
        if (pass.Chat is not { IsActive: true } chat) return;
        Player self = bot.Player;
        PlayerbotTraits traits = PlayerbotTraits.For(self.Guid.Value);
        float mood = pass.Recorder?.Mood.Get(bot.Id) ?? 0f;
        double chance = Math.Min(1.0, ArrivalBase * (0.5 + traits.Chattiness) * PlayerbotMood.ChanceMultiplier(mood));
        if (!(Roll(self.Guid.Value, claim.Serial) < chance)) return;

        PlayerbotListenerResult heard = ListenersOf(pass, self, pass.World.Options.ListenRangeSay);
        if (heard.Verdict != PlayerbotListenVerdict.Heard)
        {
            Hold(heard.Verdict switch
            {
                PlayerbotListenVerdict.Ignored => PlayerbotEventMeters.Ignored,
                PlayerbotListenVerdict.CutOff => PlayerbotEventMeters.CutOff,
                _ => PlayerbotEventMeters.NoListener,
            });
            return;
        }

        long now = pass.NowMs;
        foreach (ulong listener in heard.Listeners)
        {
            if (initiative.MayAddress(new ObjectGuid(listener), now)) continue;
            Hold(PlayerbotEventMeters.ListenerCap);
            return;
        }

        // The bot and zone gaps are quiet: a second line the same minute is not an event worth a count.
        if (_botNext.TryGetValue(bot.Id, out long botUntil) && botUntil > now) return;
        if (_zoneNext.TryGetValue(self.ZoneId, out long zoneUntil) && zoneUntil > now) return;

        ObjectGuid first = new(heard.Listeners[0]);
        string name = pass.World.FindOnlinePlayer(first)?.Name ?? string.Empty;
        string key = $"event.{festival.Flavour.Key}.arrive";
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, "event-arrive", BotSpeechPriority.Low) { TargetGuid = first };
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), first, name, ChatType.Say, string.Empty, FromMaster: false, InviteAllowed: false,
            SenderAccountId: 0,
            Topic: new BotChatTopic(key, $"You have just joined the {festival.Flavour.Name} celebrations beside a festival host. Greet the people around you in one short, cheerful line.",
                new Dictionary<string, string> { ["event"] = festival.Flavour.Name }, string.Empty),
            Speech: speech)
        { BuiltinOnly = heard.BuiltinOnly };
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return;

        Lines++;
        PlayerbotEventMeters.Line(PlayerbotEventMeters.Arrive);
        // The ledger is charged when the line is admitted (not when it is said), so a second bot of the same pass finds the listeners taken.
        foreach (ulong listener in heard.Listeners) initiative.NoteAddressed(new ObjectGuid(listener), now, pass.Options.Initiative.OpeningCooldownMinutes);
        if (_botNext.Count >= 8192) _botNext.Clear();
        if (_zoneNext.Count >= 1024) _zoneNext.Clear();
        _botNext[bot.Id] = now + (holidays.TalkBotGapMinutes * 60_000L);
        _zoneNext[self.ZoneId] = now + (holidays.TalkZoneGapSeconds * 1000L);
    }

    /// <summary>
    /// The real players of either faction within <paramref name="range"/> of <paramref name="self"/> on its map, judged by
    /// <see cref="PlayerbotEventListeners.Evaluate"/>: the chat safety of each listener is read by guid, a listener who ignores the bot is
    /// found through the social context. Bots are never listeners.
    /// </summary>
    internal static PlayerbotListenerResult ListenersOf(PlayerbotSocialPass pass, Player self, float range)
    {
        var candidates = new List<PlayerbotListenerCandidate>();
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == self.Guid) continue;
            if (++looked > SightLimit) break;
            if (pass.World.FindOnlinePlayer(guid) is not { } other || !ReferenceEquals(other.Map, self.Map)) continue;
            bool real = other.Session is WorldSession { IsManaged: false };
            candidates.Add(new PlayerbotListenerCandidate(guid.Value, real, Distance(self, other), real && pass.Social.IsIgnoring(other, self.Guid)));
        }

        PlayerbotChat? chat = pass.Chat;
        return PlayerbotEventListeners.Evaluate(candidates, range, guid => chat?.Safety.Player(guid));
    }

    // --- the celebration emotes -----------------------------------------------------------------------------------------------

    private void Celebrate(PlayerbotSocialPass pass, PlayerbotHolidayOptions holidays, PlayerbotSocialBot bot, PlayerbotFestivalClaim claim, PlayerbotFestival festival, BotState state, long now)
    {
        IReadOnlyList<uint> emotes = festival.Flavour.Emotes;
        if (emotes.Count == 0) return;
        ulong guid = bot.Player.Guid.Value;
        if (state.NextEmoteMs == 0)
        {
            state.NextEmoteMs = now + EmoteGapMs(holidays, guid, ++state.Draws);
            return;
        }

        if (now < state.NextEmoteMs) return;
        state.NextEmoteMs = now + EmoteGapMs(holidays, guid, ++state.Draws);

        PlayerbotListenerResult heard = ListenersOf(pass, bot.Player, pass.World.Options.ListenRangeTextEmote);
        // An untargeted emote reaches everyone in range, so every real player there is charged; who ignores the bot does not change that.
        PlayerbotEmoteVerdict verdict = _budget.TryTake(claim.Anchor.Guid, heard.Listeners, now, holidays.EmotesPerListenerMinute, holidays.EmotesPerAnchorMinute);
        if (verdict != PlayerbotEmoteVerdict.Sent)
        {
            Hold(verdict == PlayerbotEmoteVerdict.ListenerBudget ? PlayerbotEventMeters.EmoteListenerBudget : PlayerbotEventMeters.EmoteAnchorBudget);
            return;
        }

        uint emote = emotes[(int)(PlayerbotContention.Mix(PlayerbotContention.Mix(guid, (ulong)state.Draws), PickSalt) % (ulong)emotes.Count)];
        if (!PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgTextEmote, PlayerbotSocialPackets.TextEmote(emote, ObjectGuid.Empty))) return;
        EmotesSent++;
        PlayerbotEventMeters.Line(PlayerbotEventMeters.Emote);
    }

    // A gap in [EmoteMinSeconds, EmoteMaxSeconds]: a draw of the bot, stretched less for a sociable bot (they emote more often).
    private long EmoteGapMs(PlayerbotHolidayOptions holidays, ulong botGuid, long draw)
    {
        double sociability = PlayerbotTraits.For(botGuid).Sociability;
        double u = RollOverride is { } fixedRoll ? fixedRoll(botGuid, draw) : (PlayerbotContention.Mix(PlayerbotContention.Mix(botGuid, (ulong)draw), EmoteSalt) >> 11) * (1.0 / 9007199254740992.0);
        int min = holidays.EmoteMinSeconds, max = Math.Max(min, holidays.EmoteMaxSeconds);
        double seconds = min + ((max - min) * Math.Clamp(u, 0.0, 1.0) * (1.2 - (0.4 * sociability)));
        return (long)(Math.Clamp(seconds, min, max) * 1000.0);
    }

    // --- bounds -----------------------------------------------------------------------------------------------------------------

    private void Prune(PlayerbotWorldEventBoard board, long now)
    {
        if (now - _prunedAtMs < PruneGapMs && now >= _prunedAtMs) return;
        _prunedAtMs = now;
        _budget.Sweep(now);
        if (_states.Count > 0)
        {
            List<Guid>? gone = null;
            foreach (Guid bot in _states.Keys)
                if (board.Claims.ClaimOf(bot, now) is null) (gone ??= []).Add(bot);
            if (gone is not null) foreach (Guid bot in gone) _states.Remove(bot);
        }

        PruneGaps(_botNext, now);
        PruneGaps(_zoneNext, now);
    }

    private static void PruneGaps<TKey>(Dictionary<TKey, long> map, long now) where TKey : notnull
    {
        if (map.Count == 0) return;
        foreach (KeyValuePair<TKey, long> entry in map)
            if (entry.Value <= now) map.Remove(entry.Key);
    }
}
