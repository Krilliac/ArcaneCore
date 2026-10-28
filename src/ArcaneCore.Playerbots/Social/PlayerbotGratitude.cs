using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>How a bot was helped (the thanks it owes, <see cref="PlayerbotSocialRecorder.NoteHelp"/>).</summary>
internal enum PlayerbotThanksKind : byte
{
    /// <summary>A rescue in a fight: an assist, a heal, a hit on the mob that fights it.</summary>
    Help,

    /// <summary>A resurrection by someone outside its group.</summary>
    Resurrect,

    /// <summary>A buff from someone outside its group.</summary>
    Buff,
}

/// <summary>What a grateful, generous bot does for a helper it likes, besides thanking.</summary>
internal enum PlayerbotFavour : byte
{
    /// <summary>Nothing more.</summary>
    None,

    /// <summary>Casts its class buff on the helper.</summary>
    Buff,

    /// <summary>Resurrects the helper if they die nearby (the help lane honours the debt, <see cref="PlayerbotHelpLane.Owe"/>).</summary>
    ResurrectLater,

    /// <summary>Trades the helper a little gold.</summary>
    Tip,
}

/// <summary>The pure rules of gratitude (docs/areas/playbots-social.md, Gratitude). Every choice is a hash of the bot, the helper and the fight, so it is repeatable.</summary>
internal static class PlayerbotGratitude
{
    /// <summary>The Generosity trait from which a bot gives more than thanks.</summary>
    internal const float GenerousAt = 0.6f;

    /// <summary>The affinity a bot needs toward a helper before it gives more than thanks.</summary>
    internal const int FavourAffinity = 30;

    /// <summary>How long (milliseconds) a favour for a resurrection owed stays good.</summary>
    internal const long OweMs = 30 * 60_000L;

    /// <summary>The text emote id of <c>/thank</c> (vmangos TEXTEMOTE_THANK).</summary>
    internal const uint ThankEmote = PlayerbotSocialEmotes.TextEmotes.Thank;

    /// <summary>The farthest (yards) a bot thanks a helper from.</summary>
    internal const float ThankYards = 30f;

    /// <summary>The farthest (yards) a bot walks no step to tip: the helper must be this close, inside the trade distance.</summary>
    internal const float TipYards = 8f;

    /// <summary>Whether the bot gives a favour as well: generous (high Generosity) and fond of the helper.</summary>
    internal static bool Generous(float generosity, int affinity) => generosity >= GenerousAt && affinity >= FavourAffinity;

    /// <summary>Whether a chatty bot says a line besides the emote: a hash below its Chattiness.</summary>
    internal static bool SaysThanks(ulong botGuid, ulong helperGuid, uint fight, float chattiness)
        => Roll(botGuid, helperGuid, 0x7A4A4B5UL ^ fight) < chattiness;

    /// <summary>The favour this bot gives this helper (<see cref="PlayerbotFavour.None"/> unless <see cref="Generous"/>): a tip with the chance <c>TipChance x Generosity</c>, else a buff or a debt of resurrection.</summary>
    internal static PlayerbotFavour FavourOf(ulong botGuid, ulong helperGuid, uint fight, float generosity, int affinity, float tipChance, bool canTip, bool canBuff, bool canResurrect)
    {
        if (!Generous(generosity, affinity)) return PlayerbotFavour.None;
        if (canTip && Roll(botGuid, helperGuid, 0x71BC0FFUL ^ fight) < tipChance * generosity) return PlayerbotFavour.Tip;
        bool first = Roll(botGuid, helperGuid, 0xFA70B2UL ^ fight) < 0.5;
        if (first && canBuff) return PlayerbotFavour.Buff;
        if (canResurrect) return PlayerbotFavour.ResurrectLater;
        return canBuff ? PlayerbotFavour.Buff : PlayerbotFavour.None;
    }

    /// <summary>
    /// The tip in copper: at most 1% of the bot's gold and at most <paramref name="maxTipCopper"/> at level 60 (a lower level scales down in proportion). 0: no tip.
    /// </summary>
    internal static uint TipCopper(uint money, int level, int maxTipCopper)
    {
        if (maxTipCopper <= 0 || level <= 0) return 0;
        long cap = (long)maxTipCopper * Math.Min(level, 60) / 60;
        return (uint)Math.Min(money / 100UL, (ulong)cap);
    }

    private static double Roll(ulong a, ulong b, ulong salt) => (PlayerbotContention.Mix(PlayerbotContention.Mix(a, b), salt) >> 11) * (1.0 / 9007199254740992.0);
}

/// <summary>
/// Bots thanking whoever helped them (<see cref="PlayerbotSocialGratitudeOptions"/>, docs/areas/playbots-social.md, Gratitude). Run from the social
/// pass on the world thread. It takes the thanks the recorder queued (a bot was helped in a fight, resurrected or buffed by someone outside its group) and,
/// once per helper per fight, sends a <c>thank</c> text emote at them (across factions too), says a short line when the helper is a same-faction player and
/// the bot is chatty, and, for a generous bot that likes the helper, buffs them, owes them a resurrection or tips them by a real trade. The tip is a small
/// state machine over the existing trade handlers (CMSG_INITIATE_TRADE, CMSG_SET_TRADE_GOLD, CMSG_ACCEPT_TRADE) that gives up after <see cref="TipWaitMs"/>.
/// </summary>
internal sealed class PlayerbotGratitudeLane
{
    /// <summary>The most thanks taken per pass.</summary>
    internal const int PerPass = 16;

    /// <summary>How long (milliseconds) a tip waits for the helper to open and accept the trade.</summary>
    internal const long TipWaitMs = 45_000;

    private const long TradeRetryMs = 5_000;
    private const int MaxTips = 32;

    // The bot's session and player are kept so that a tip dropped after the bot left the pass (logged out, no longer managed) can still cancel its window.
    private sealed class Tip(WorldSession session, Player bot, ObjectGuid helper, uint copper, long startedMs)
    {
        public WorldSession Session { get; } = session;

        public Player Bot { get; } = bot;

        public ObjectGuid Helper { get; } = helper;

        public uint Copper { get; } = copper;

        public long StartedMs { get; } = startedMs;

        public long LastInitiateMs { get; set; }

        public bool Seen { get; set; }

        public long ClosedAtMs { get; set; }
    }

    // A thanks owed by a bot that was dead when it came up: tried again every few seconds until the bot lives or the debt is too old.
    private sealed class Owed(ObjectGuid bot, ObjectGuid helper, PlayerbotThanksKind kind, long retryAtMs, long deadlineMs)
    {
        public ObjectGuid Bot { get; } = bot;

        public ObjectGuid Helper { get; } = helper;

        public PlayerbotThanksKind Kind { get; } = kind;

        public long RetryAtMs { get; set; } = retryAtMs;

        public long DeadlineMs { get; } = deadlineMs;
    }

    private const long OwedRetryMs = 5_000;
    private const long OwedDeadlineMs = 2 * 60_000L;
    private const int MaxOwed = 64;
    private const long ClosedGraceMs = 3_000;

    private readonly Dictionary<(Guid Bot, ulong Helper), long> _thanked = [];
    private readonly Dictionary<Guid, Tip> _tips = [];
    private readonly List<Owed> _owed = [];
    private long _sweptAtMs;

    /// <summary>Thank emotes sent (tests and status).</summary>
    internal int Emotes { get; private set; }

    /// <summary>Thank lines offered to the announcement path.</summary>
    internal int Lines { get; private set; }

    /// <summary>Buffs a bot was sent to give out of gratitude.</summary>
    internal int Buffs { get; private set; }

    /// <summary>Resurrections a bot owes out of gratitude.</summary>
    internal int Debts { get; private set; }

    /// <summary>Tips that began (a trade was asked for).</summary>
    internal int TipsStarted { get; private set; }

    /// <summary>Tips that settled (the helper took the gold).</summary>
    internal int TipsPaid { get; private set; }

    /// <summary>Tips under way.</summary>
    internal int TipsPending => _tips.Count;

    internal void Run(PlayerbotSocialPass pass, PlayerbotHelpLane help)
    {
        PlayerbotSocialRecorder? recorder = pass.Recorder;
        if (recorder is null) return;
        PlayerbotSocialGratitudeOptions options = pass.Options.Gratitude;
        if (!options.Enabled || !recorder.Enabled)
        {
            while (recorder.TryTakeThanks(out _, out _, out _)) { }
            _owed.Clear();
            CancelAllTips();
            return;
        }

        Sweep(pass.NowMs);
        if (_owed.Count > 0) RetryOwed(pass, recorder, help, options);
        for (int budget = PerPass; budget > 0 && recorder.TryTakeThanks(out ObjectGuid botGuid, out ObjectGuid helperGuid, out PlayerbotThanksKind kind); budget--)
            if (pass.ByCharacter.TryGetValue(botGuid.Low, out PlayerbotSocialBot? bot))
                Thank(pass, recorder, help, options, bot, helperGuid, kind);
        if (_tips.Count > 0) StepTips(pass, recorder);
    }

    private void RetryOwed(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotHelpLane help, PlayerbotSocialGratitudeOptions options)
    {
        for (int i = _owed.Count - 1; i >= 0; i--)
        {
            Owed owed = _owed[i];
            if (pass.NowMs < owed.RetryAtMs) continue;
            if (!pass.ByCharacter.TryGetValue(owed.Bot.Low, out PlayerbotSocialBot? bot) || pass.NowMs >= owed.DeadlineMs)
            {
                _owed.RemoveAt(i);
                continue;
            }

            if (!bot.Player.IsAlive)
            {
                owed.RetryAtMs = pass.NowMs + OwedRetryMs;
                continue;
            }

            _owed.RemoveAt(i);
            Thank(pass, recorder, help, options, bot, owed.Helper, owed.Kind);
        }
    }

    private void Thank(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotHelpLane help, PlayerbotSocialGratitudeOptions options, PlayerbotSocialBot bot, ObjectGuid helperGuid,
        PlayerbotThanksKind kind)
    {
        Player self = bot.Player;
        if (!self.IsAlive)
        {
            // A dead bot says nothing yet: the thanks waits for it to live again (or for the debt to go stale).
            if (_owed.Count < MaxOwed) _owed.Add(new Owed(self.Guid, helperGuid, kind, pass.NowMs + OwedRetryMs, pass.NowMs + OwedDeadlineMs));
            return;
        }

        if (pass.World.FindOnlinePlayer(helperGuid) is not { } helper || ReferenceEquals(helper, self) || !ReferenceEquals(helper.Map, self.Map)) return;
        if (Distance(self, helper) > PlayerbotGratitude.ThankYards) return;
        if (_thanked.TryGetValue((bot.Id, helperGuid.Value), out long at) && pass.NowMs - at < PlayerbotSocialRecorder.HelpWindowMs) return;
        _thanked[(bot.Id, helperGuid.Value)] = pass.NowMs;

        if (PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgTextEmote, PlayerbotSocialPackets.TextEmote(PlayerbotGratitude.ThankEmote, helper.Guid))) Emotes++;

        PlayerbotTraits traits = PlayerbotTraits.For(self.Guid.Value);
        uint fight = (uint)(pass.NowMs / 1000);
        bool sameFaction = helper.Team == self.Team;
        bool person = !recorder.IsManaged(helper.Guid);
        if (sameFaction && person && PlayerbotGratitude.SaysThanks(self.Guid.Value, helperGuid.Value, fight, traits.Chattiness * PlayerbotMood.ChanceMultiplier(recorder.Mood.Get(bot.Id))) && Say(pass, bot, helper)) Lines++;
        if (!sameFaction) return;

        int affinity = 0;
        if (recorder.Memory.TryGet(bot.Id, helperGuid.Value, out PlayerbotRelation relation))
        {
            if (recorder.Memory.IsRival(relation)) return;
            affinity = recorder.Memory.EffectiveAffinity(relation);
        }

        uint tip = PlayerbotGratitude.TipCopper(self.Money, self.Level, options.MaxTipCopper);
        bool canTip = person && tip > 0 && !_tips.ContainsKey(bot.Id) && _tips.Count < MaxTips && !self.Combat.IsInCombat;
        bool canBuff = kind != PlayerbotThanksKind.Buff && PlayerbotHelpRules.BuffOf(self.Class) is not null && PlayerbotHelpLane.IsFree(bot) && helper.IsAlive;
        bool canResurrect = PlayerbotHelpRules.CanResurrect(self.Class);
        switch (PlayerbotGratitude.FavourOf(self.Guid.Value, helperGuid.Value, fight, traits.Generosity, affinity, options.TipChance, canTip, canBuff, canResurrect))
        {
            case PlayerbotFavour.Tip:
                StartTip(pass.Recorder!, bot, helperGuid, tip, pass.NowMs);
                break;
            case PlayerbotFavour.Buff:
                if (bot.Brain!.RequestHelp(PlayerbotHelpKind.Buff, helperGuid)) Buffs++;
                break;
            case PlayerbotFavour.ResurrectLater:
                help.Owe(bot.Id, helperGuid.Value, pass.NowMs + PlayerbotGratitude.OweMs);
                Debts++;
                break;
        }
    }

    /// <summary>Begin a tip of <paramref name="copper"/> from the bot to the helper (the favour above).</summary>
    private void StartTip(PlayerbotSocialRecorder recorder, PlayerbotSocialBot bot, ObjectGuid helper, uint copper, long nowMs)
    {
        recorder.TryTakeTradeDone(bot.Id); // a completion from an earlier trade is not this tip's
        _tips[bot.Id] = new Tip(bot.Session, bot.Player, helper, copper, nowMs);
        TipsStarted++;
    }

    // A short line through the announcement path, so the limiter, the safety screen, the opt-in rules and the AI disclosure apply.
    private static bool Say(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player helper)
    {
        if (pass.Chat is not { IsActive: true } chat) return false;
        Player self = bot.Player;
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, "social-thanks", BotSpeechPriority.Low) { TargetGuid = helper.Guid };
        var topic = new BotChatTopic("social.thanks", "Someone just helped you in a fight, or brought you back to life. Thank them in one short line.",
            new Dictionary<string, string>(), string.Empty);
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), helper.Guid, helper.Name, ChatType.Say, "Thank them.",
            FromMaster: false, InviteAllowed: false, SenderAccountId: 0, Topic: topic, Speech: speech);
        return chat.TryAnnounce(ask) == BotChatAdmission.Queued;
    }

    // --- the tip -------------------------------------------------------------------------------------------------------------

    private readonly List<Guid> _doneScratch = [];

    private void StepTips(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder)
    {
        _doneScratch.Clear();
        foreach ((Guid botId, Tip tip) in _tips)
            if (!Step(pass, recorder, botId, tip)) _doneScratch.Add(botId);
        foreach (Guid botId in _doneScratch)
        {
            if (_tips.Remove(botId, out Tip? ended)) PlayerbotTradeOwners.Of(ended.Session)?.Release(ended.Bot, PlayerbotTradeOwner.Tip);
        }
    }

    /// <summary>Drop every tip under way and close the windows they opened (the feature is switched off, detached or stopped).</summary>
    internal void CancelAllTips()
    {
        if (_tips.Count == 0) return;
        foreach (Tip tip in _tips.Values)
        {
            Cancel(tip);
            PlayerbotTradeOwners.Of(tip.Session)?.Release(tip.Bot, PlayerbotTradeOwner.Tip);
        }

        _tips.Clear();
    }

    // Close the bot's trade window if it is the tip's: a dropped tip never leaves the helper looking at a stalled window, and never touches another driver's window.
    private static void Cancel(Tip tip)
    {
        PlayerbotTradeOwners? owners = PlayerbotTradeOwners.Of(tip.Session);
        EconomyFeature? economy = tip.Session.Services.GetService<EconomyFeature>();
        TradeSession? trade = economy?.TradeOf(tip.Bot);
        if (trade is { Settling: false } && owners?.Is(tip.Bot, PlayerbotTradeOwner.Tip, tip.Helper, trade) != false && trade.OtherSide(tip.Bot).Player.Guid == tip.Helper)
            PlayerbotSocialPackets.Send(tip.Session, WorldOpcode.CmsgCancelTrade, []);
        owners?.Release(tip.Bot, PlayerbotTradeOwner.Tip);
    }

    // True while the tip is still under way. Every other path cancels the window first.
    private bool Step(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, Guid botId, Tip tip)
    {
        if (!pass.ByCharacter.TryGetValue(tip.Bot.Guid.Low, out PlayerbotSocialBot? bot) || bot.Id != botId || pass.World.FindOnlinePlayer(tip.Helper) is not { } helper)
        {
            Cancel(tip);
            return false;
        }

        Player self = bot.Player;
        EconomyFeature? economy = bot.Session.Services.GetService<EconomyFeature>();
        if (economy is not { Enabled: true })
        {
            Cancel(tip);
            return false;
        }

        TradeSession? trade = economy.TradeOf(self);
        PlayerbotTradeOwners? owners = PlayerbotTradeOwners.Of(bot.Session);
        // Only the tip's own window counts as seen: another driver's window completing (a gift, a desk deal) is never a paid tip.
        if (trade is not null && owners?.Is(self, PlayerbotTradeOwner.Tip, tip.Helper, trade) != false && trade.OtherSide(self).Player.Guid == tip.Helper) tip.Seen = true;
        else if (tip.Seen)
        {
            // The window closed. A completed trade (the bot's own trade status, noted by its party AI) is a paid tip; otherwise the helper said no.
            if (recorder.TryTakeTradeDone(botId))
            {
                TipsPaid++;
                return false;
            }

            if (tip.ClosedAtMs == 0) tip.ClosedAtMs = pass.NowMs;
            return pass.NowMs - tip.ClosedAtMs < ClosedGraceMs;
        }

        bool over = pass.NowMs - tip.StartedMs > TipWaitMs;
        bool unfit = !self.IsAlive || self.Combat.IsInCombat || !helper.IsAlive || !ReferenceEquals(helper.Map, self.Map) || Distance(self, helper) > PlayerbotGratitude.TipYards
            || self.Money < tip.Copper;
        if (over || unfit)
        {
            Cancel(tip);
            return false;
        }

        if (trade is null)
        {
            if (tip.LastInitiateMs == 0 || pass.NowMs - tip.LastInitiateMs >= TradeRetryMs)
            {
                if (owners?.TryClaim(self, PlayerbotTradeOwner.Tip, helper.Guid, null) == false) return true; // another driver holds the slot: wait, bounded by TipWaitMs
                tip.LastInitiateMs = pass.NowMs;
                if (!PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgInitiateTrade, PlayerbotNavigation.GuidPayload(helper.Guid.Value)))
                    owners?.Release(self, PlayerbotTradeOwner.Tip);
            }

            return true;
        }

        if (owners?.Is(self, PlayerbotTradeOwner.Tip, tip.Helper, trade) == false || !trade.Involves(helper)) return true; // another driver's window, or somebody else's: wait, bounded by TipWaitMs; that trade is left alone
        if (!trade.Opened || trade.Settling) return true;
        TradeSide mine = trade.SideOf(self);
        if (mine.Gold != tip.Copper) PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgSetTradeGold, BitConverter.GetBytes(tip.Copper));
        else if (!mine.Accepted) PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgAcceptTrade, []);
        return true;
    }

    private void Sweep(long nowMs)
    {
        if (nowMs - _sweptAtMs < 60_000) return;
        _sweptAtMs = nowMs;
        if (_thanked.Count == 0) return;
        foreach (KeyValuePair<(Guid Bot, ulong Helper), long> entry in _thanked)
            if (nowMs - entry.Value > 10 * 60_000L) _thanked.Remove(entry.Key);
    }
}
