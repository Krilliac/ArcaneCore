using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Where a deal stands. <see cref="Settled"/> and <see cref="Failed"/> are final: the desk forgets the deal when it reaches one.</summary>
internal enum PlayerbotDealState : byte
{
    /// <summary>The bot whispered a price; the partner has not agreed.</summary>
    Quoted,

    /// <summary>The partner agreed (a yes, an accepted counter-offer, or opening the trade): the terms are fixed and wait for the window.</summary>
    Agreed,

    /// <summary>The trade window with the partner is open and the desk drives it.</summary>
    Window,

    /// <summary>The window completed at the agreed terms.</summary>
    Settled,

    /// <summary>The deal ended without a trade (cancelled, expired, declined, a scam attempt).</summary>
    Failed,
}

/// <summary>Why the desk did not quote or agree a deal.</summary>
internal enum PlayerbotDealRefusal : byte
{
    None,
    Disabled,
    Scammer,
    Cooldown,
    NoPrice,
    TooMuch,
    BotLimit,
    AccountLimit,
    Busy,
    Outflow,
    NoGoods,
    NoMoney,
}

/// <summary>The desk's answer to <see cref="PlayerbotTradeDesk.Quote"/>: a refusal, or the price (total copper, per unit, the count) and the text the bot whispers.</summary>
internal readonly record struct PlayerbotDealQuote(PlayerbotDealRefusal Refusal, long Price, long PerUnit, uint Count, string PriceText, string ItemName = "")
{
    public bool Ok => Refusal == PlayerbotDealRefusal.None;
}

/// <summary>What <see cref="PlayerbotTradeDesk.Hear"/> made of a partner's line.</summary>
internal enum PlayerbotDealHeardKind : byte
{
    /// <summary>The line is not about the deal; the caller does its normal thing.</summary>
    None,

    /// <summary>A yes: the deal is agreed at the quoted price.</summary>
    Agreed,

    /// <summary>A counter-offer the bot accepted: the deal is agreed at <see cref="PlayerbotDealHeard.Price"/>.</summary>
    CounterAgreed,

    /// <summary>A counter-offer the bot refused: it holds its price (one counter per deal).</summary>
    CounterRefused,

    /// <summary>The partner agreed but a limit stopped the deal (<see cref="PlayerbotDealHeard.Refusal"/>).</summary>
    Refused,
}

/// <summary>The desk's answer to <see cref="PlayerbotTradeDesk.Hear"/>. <see cref="Price"/> and <see cref="PriceText"/> are the deal's current price (the agreed one, or the one the bot holds); <see cref="ItemName"/> and <see cref="Count"/> name the deal's goods.</summary>
internal readonly record struct PlayerbotDealHeard(PlayerbotDealHeardKind Kind, PlayerbotDealRefusal Refusal, long Price, string PriceText, string ItemName = "", uint Count = 0);

/// <summary>What the bot's client does next for a deal. <see cref="None"/> may still carry a <see cref="PlayerbotDealStep.SayKey"/> (the bot says one line).</summary>
internal enum PlayerbotDealStepKind : byte
{
    None,

    /// <summary>CMSG_INITIATE_TRADE with the partner.</summary>
    Initiate,

    /// <summary>CMSG_SET_TRADE_ITEM: trade slot, bag and slot of the item.</summary>
    PutItem,

    /// <summary>CMSG_SET_TRADE_GOLD with <see cref="PlayerbotDealStep.Gold"/> (0 takes the gold back).</summary>
    PutGold,

    /// <summary>CMSG_ACCEPT_TRADE.</summary>
    Accept,

    /// <summary>CMSG_CANCEL_TRADE.</summary>
    Cancel,
}

/// <summary>One step of <see cref="PlayerbotTradeDesk.Drive"/>.</summary>
internal readonly record struct PlayerbotDealStep(
    PlayerbotDealStepKind Kind,
    ObjectGuid Partner = default,
    byte TradeSlot = 0,
    byte Bag = 0,
    byte Slot = 0,
    uint Gold = 0,
    string? SayKey = null,
    string? PartnerName = null);

/// <summary>How a desk window ended (<see cref="PlayerbotTradeDesk.WindowClosed"/>); <see cref="PlayerbotDealOutcome.Entry"/> and <see cref="PlayerbotDealOutcome.Role"/> name the deal's goods, to spend the bot's board post.</summary>
internal enum PlayerbotDealOutcomeKind : byte
{
    /// <summary>No desk deal was in a window.</summary>
    None,

    Settled,

    Failed,
}

/// <summary>The result of <see cref="PlayerbotTradeDesk.WindowClosed"/>.</summary>
internal readonly record struct PlayerbotDealOutcome(PlayerbotDealOutcomeKind Kind, PlayerbotDealRole Role = PlayerbotDealRole.Seller, uint Entry = 0, uint Count = 0);

/// <summary>
/// The playerbot trade desk (design section 2.3; docs/areas/playbots-social.md, Trading): one per world. A bot quotes a price for its WTS or WTB call, a real
/// player agrees (a yes, one counter-offer within <see cref="PlayerbotSocialTradingDealsOptions.CounterTolerancePercent"/>, or by opening the trade), and the desk
/// then drives the bot's side of the trade window through the bot's own client packets (the party AI executes each <see cref="PlayerbotDealStep"/> as the matching
/// CMSG_*). States per (bot, partner): Quoted, Agreed, Window, then Settled or Failed (a final deal is forgotten).
/// <para>
/// Safety: the pure rules are <see cref="PlayerbotDealRules"/> (never a price under the vendor floor, a buyer never pays more than the goods' reference value divided
/// by 0.67, the window must be exactly the agreed deal). The desk adds the limits (<see cref="PlayerbotSocialTradingLimitsOptions"/> and the deal keys), the pair
/// cooldown, the scam memory (a partner the ledger holds as a scammer is refused), and re-reads the live <see cref="TradeSession"/> on every step. Any change of the
/// window clears both accepts on the server (<c>TradeNegotiated</c>), so a bot that accepted a matching window can never complete a changed one.
/// </para>
/// <para>
/// Deviation from the design text: the design says the bot accepts only after the partner has. The desk instead accepts a matching window itself once both sides
/// held still for <see cref="HoldMs"/> (longer than the server's scam-prevention bounce). That is equally safe, because the server clears both accepts on any
/// change, and it is what makes the design's "changed after the bot accepted" rule observable: the bot accepted, the window changed, its accept was cleared, and
/// the partner side differs from what it accepted. A second such change cancels the deal and counts as a scam attempt.
/// </para>
/// <para>
/// Limits (documented in the area doc): the desk never splits a stack (a seller whose stacks cannot sum exactly to the count cancels), and the hourly trade counts
/// and daily outflow counters live in memory only (they reset on a restart). Prices come from <see cref="IPlayerbotDealPriceSource"/> (the reference price until the
/// price book lands). Options are read live on every call; the desk caches none. The desk owns its clock (a Unix-millisecond function, the same base as the chat
/// service's clock): callers never pass a time. World thread; every public member also takes one lock, so a test may read it from anywhere.
/// Memory is bounded: at most <see cref="MaxQuotedPerBot"/> quoted deals per bot, <see cref="MaxDeals"/> deals in all, <see cref="MapCap"/> entries per counter map.
/// </para>
/// </summary>
internal sealed class PlayerbotTradeDesk(PlayerbotOptions options, PlayerbotSocialRecorder recorder, IPlayerbotDealPriceSource prices, Func<long> clockUnixMs, PlayerbotTradeOwners? owners = null)
{
    /// <summary>The window must hold still this long before the bot accepts it (longer than the server's scam-prevention bounce).</summary>
    internal const long HoldMs = 1500;

    /// <summary>The least gap between two trade requests the desk makes to one partner.</summary>
    internal const long InitiateGapMs = 10_000;

    /// <summary>How long a Window deal whose trade is gone waits for its closing status before the desk fails it.</summary>
    internal const long GoneGraceMs = 10_000;

    /// <summary>
    /// How long an agreed deal waits for the bot's trade slot while another driver (a tip, a fee, a gift window) or a stranger's request holds it. The deal-life clock
    /// does not run meanwhile; the bound ends the deal with no blame on the partner.
    /// </summary>
    internal const long DeferMaxMs = 60_000;

    /// <summary>Quoted deals one bot keeps (the oldest is dropped for a new one).</summary>
    internal const int MaxQuotedPerBot = 4;

    /// <summary>Deals the desk holds at all.</summary>
    internal const int MaxDeals = 1024;

    /// <summary>Entries one counter or cooldown map holds before it is trimmed.</summary>
    internal const int MapCap = 4096;

    /// <summary>The say key of the one line a bot says when a deal ends without a trade.</summary>
    internal const string DealOffKey = "voice.deal.off";

    private const long HourMs = 3_600_000;
    private const long DayMs = 86_400_000;
    private const long MinuteMs = 60_000;

    private readonly object _gate = new();
    private readonly Dictionary<DealKey, Deal> _deals = [];
    private readonly Dictionary<Guid, List<Deal>> _byBot = [];
    private readonly Dictionary<(Guid Bot, ulong Partner), long> _cooldowns = [];
    private readonly SlidingCounts<Guid> _botTrades = new();
    private readonly SlidingCounts<int> _accountTrades = new();
    private readonly DailyTotals<Guid> _botOutflow = new();
    private readonly DailyTotals<int> _accountOutflow = new();
    private long _serverDay = -1;
    private long _serverOutflow;
    private long _prunedMs;
    private int _count;
    private Func<long> _clock = clockUnixMs ?? throw new ArgumentNullException(nameof(clockUnixMs));

    /// <summary>Test seam (never configuration-bound): replaces the desk's clock (Unix milliseconds).</summary>
    internal Func<long> Clock
    {
        set => _clock = value ?? throw new ArgumentNullException(nameof(value));
    }

    private PlayerbotSocialTradingOptions Trading => options.Social.Trading;

    /// <summary>
    /// Whether the desk runs: playerbots, <c>Social:Trading:Enabled</c> and <c>Social:Trading:Deals:Enabled</c> are on and <c>Deals:MaxDealCopper</c> is above 0.
    /// Read live: switching it off cancels every window the desk drives at its next <see cref="Drive"/>.
    /// </summary>
    internal bool Enabled => options.Enabled && Trading.Enabled && Trading.Deals.Enabled && Trading.Deals.MaxDealCopper > 0;

    /// <summary>Deals held (any state). Reads without the lock: the fast path of the party AI's intake costs nothing when there are none.</summary>
    internal int Count => Volatile.Read(ref _count);

    /// <summary>Whether <paramref name="botId"/> holds a deal of any state.</summary>
    internal bool HasDeal(Guid botId)
    {
        lock (_gate) return _byBot.ContainsKey(botId);
    }

    /// <summary>The state of the (bot, partner) deal, or null when there is none (a final deal is forgotten).</summary>
    internal PlayerbotDealState? StateOf(Guid botId, ObjectGuid partner)
    {
        lock (_gate) return _deals.TryGetValue(new DealKey(botId, partner.Value), out Deal? deal) ? deal.State : null;
    }

    /// <summary>The agreed price (total copper) of the (bot, partner) deal, or null when there is none; the quoted price while it is only quoted.</summary>
    internal long? PriceOf(Guid botId, ObjectGuid partner)
    {
        lock (_gate) return _deals.TryGetValue(new DealKey(botId, partner.Value), out Deal? deal) ? deal.Price : null;
    }

    /// <summary>
    /// Whether the live <paramref name="trade"/> is a desk window: the deal with its other side is Agreed or in its Window and the trade slot is the desk's
    /// (<see cref="PlayerbotTradeOwners"/>; no registry: the desk is the sole owner). The party AI then leaves the window to the desk. A window another driver owns, even
    /// beside an agreed deal with the same partner (the master's own "trade me"), is not the desk's.
    /// </summary>
    internal bool Owns(Guid botId, Player bot, TradeSession trade)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(trade);
        ObjectGuid partner = trade.OtherSide(bot).Player.Guid;
        lock (_gate)
            return _deals.TryGetValue(new DealKey(botId, partner.Value), out Deal? deal) && deal.State is PlayerbotDealState.Agreed or PlayerbotDealState.Window
                && (owners is null || owners.Is(bot, PlayerbotTradeOwner.Desk, partner, trade));
    }

    // --- quoting -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bot quotes a price to <paramref name="partner"/> for <paramref name="count"/> of <paramref name="entry"/> and the desk keeps a Quoted deal (replacing the
    /// pair's earlier quote). Refused (with the reason) when the desk is off, the partner is a remembered scammer, the pair is in cooldown, the item has no price,
    /// the bot lacks the goods (a seller: tradeable unbound items in its bags) or the money (a buyer), the price is above the deal cap (a quarter of it for a
    /// low-trust partner), an hourly limit is reached (0 allows none), or the bot already has an agreed deal with someone else. A seller asks the reference price
    /// (friend discount or dislike markup by affinity), never under the vendor value; a buyer bids the reference price. The price is a chat-exact amount
    /// (<see cref="PlayerbotDealRules.RoundUpToQuote"/> for an ask, <see cref="PlayerbotDealRules.RoundDownToQuote"/> for a bid).
    /// </summary>
    internal PlayerbotDealQuote Quote(Guid botId, Player bot, Player partner, PlayerbotDealRole role, uint entry, uint count)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(partner);
        if (count == 0 || entry == 0) return Refuse(PlayerbotDealRefusal.NoGoods, count);
        lock (_gate)
        {
            if (!Enabled) return Refuse(PlayerbotDealRefusal.Disabled, count);
            long now = _clock();
            PruneExpired(now);
            PlayerbotSocialTradingDealsOptions deals = Trading.Deals;
            ulong other = partner.Guid.Value;
            if (recorder.Ledger?.IsScammer(botId, other) == true) return Refuse(PlayerbotDealRefusal.Scammer, count);
            if (InCooldown(botId, other, now)) return Refuse(PlayerbotDealRefusal.Cooldown, count);
            ItemTemplate? template = bot.Inventory.Templates.Find(entry);
            if (template is null) return Refuse(PlayerbotDealRefusal.NoPrice, count);
            uint reference = prices.ReferencePerUnit(template);
            if (reference == 0) return Refuse(PlayerbotDealRefusal.NoPrice, count);

            (int trust, int affinity) = RelationOf(botId, other);
            long vendorFloor = (long)template.SellPrice * count;
            long referenceValue = (long)reference * count;
            long price;
            if (role == PlayerbotDealRole.Seller)
            {
                if (TradeableCount(bot, entry) < count) return Refuse(PlayerbotDealRefusal.NoGoods, count);
                long ask = PlayerbotDealRules.SellerAsk(reference, count, affinity, deals.FriendDiscountPercent, deals.DislikeMarkupPercent);
                price = PlayerbotDealRules.RoundUpToQuote(Math.Max(Math.Max(ask, vendorFloor), 1));
            }
            else
            {
                price = PlayerbotDealRules.RoundDownToQuote(PlayerbotDealRules.BuyerBid(reference, count));
                if (bot.Money < price) return Refuse(PlayerbotDealRefusal.NoMoney, count);
            }

            if (price > PlayerbotDealRules.DealCap(deals.MaxDealCopper, trust, Trading.LowTrustPermille)) return Refuse(PlayerbotDealRefusal.TooMuch, count);
            var terms = new PlayerbotDealTerms(role, entry, count, price, vendorFloor, referenceValue);
            if (!PlayerbotDealRules.TermsAreSafe(terms)) return Refuse(PlayerbotDealRefusal.NoPrice, count);
            PlayerbotSocialTradingLimitsOptions limits = Trading.Limits;
            if (_botTrades.Count(botId, now) + PendingTrades(botId, null, null) >= limits.MaxTradesPerBotPerHour) return Refuse(PlayerbotDealRefusal.BotLimit, count);
            if (_accountTrades.Count(partner.AccountId, now) + PendingTrades(null, partner.AccountId, null) >= limits.MaxTradesPerAccountPerHour) return Refuse(PlayerbotDealRefusal.AccountLimit, count);

            var key = new DealKey(botId, other);
            bool carriedCounter = false;
            if (_deals.TryGetValue(key, out Deal? existing))
            {
                if (existing.State != PlayerbotDealState.Quoted) return Refuse(PlayerbotDealRefusal.Busy, count);
                carriedCounter = existing.CounterUsed && existing.Role == role && existing.Entry == entry && existing.Count == count; // asking again is not a new counter
                RemoveDeal(existing);
            }

            if (ActiveOf(botId) is not null) return Refuse(PlayerbotDealRefusal.Busy, count);
            if (_count >= MaxDeals && !EvictOldestQuoted()) return Refuse(PlayerbotDealRefusal.Busy, count);
            var deal = new Deal
            {
                BotId = botId, BotGuid = bot.Guid, Partner = partner.Guid, PartnerName = partner.Name, PartnerAccount = partner.AccountId,
                CrossFaction = bot.Team != partner.Team, Role = role, Entry = entry, Count = count, ItemName = template.Name,
                QuotedPrice = price, Price = price, VendorFloor = vendorFloor, ReferenceValue = referenceValue,
                Limit = role == PlayerbotDealRole.Seller ? vendorFloor : PlayerbotDealRules.BuyerCeiling(reference, count, deals.BuyMaxPremiumPercent),
                State = PlayerbotDealState.Quoted, CreatedMs = now, CounterUsed = carriedCounter,
            };
            AddDeal(deal);
            TrimQuoted(botId);
            return new PlayerbotDealQuote(PlayerbotDealRefusal.None, price, price / count, count, PlayerbotVoiceFacts.Price(price), template.Name);
        }
    }

    private static PlayerbotDealQuote Refuse(PlayerbotDealRefusal reason, uint count) => new(reason, 0, 0, count, string.Empty);

    // --- hearing ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A line from the partner of a Quoted deal (not expired). A yes agrees the deal at the quoted price. A price is one counter-offer: accepted
    /// (<see cref="PlayerbotDealRules.AcceptCounter"/> with <c>Deals:CounterTolerancePercent</c>) it becomes the price and the deal is agreed; refused it uses up the
    /// deal's counter and the bot holds its price. Agreeing re-checks the limits (concurrent windows, a bot busy with someone else, the deal cap, the hourly counts
    /// and the daily outflow caps with the discount counted). Anything else is <see cref="PlayerbotDealHeardKind.None"/>: the caller does its normal thing.
    /// </summary>
    internal PlayerbotDealHeard Hear(Guid botId, ObjectGuid partner, string text)
    {
        lock (_gate)
        {
            if (!Enabled || !_deals.TryGetValue(new DealKey(botId, partner.Value), out Deal? deal) || deal.State != PlayerbotDealState.Quoted) return NoHeard;
            long now = _clock();
            if (now - deal.CreatedMs >= DealLifeMs)
            {
                RemoveDeal(deal);
                return NoHeard;
            }

            long? offered = PlayerbotDealRules.ParsePrice(text);
            if (offered is null && !PlayerbotDealRules.IsYes(text)) return NoHeard;
            if (offered is { } counter)
            {
                long? accepted = PlayerbotDealRules.AcceptCounter(deal.Role, deal.QuotedPrice, deal.Limit, counter, Trading.Deals.CounterTolerancePercent, deal.CounterUsed);
                if (accepted is not { } agreedPrice)
                {
                    deal.CounterUsed = true;
                    return Heard(PlayerbotDealHeardKind.CounterRefused, PlayerbotDealRefusal.None, deal, deal.QuotedPrice);
                }

                deal.CounterUsed = true;
                deal.Price = deal.Role == PlayerbotDealRole.Seller ? PlayerbotDealRules.RoundUpToQuote(agreedPrice) : PlayerbotDealRules.RoundDownToQuote(agreedPrice);
                PlayerbotDealRefusal counterRefusal = Agree(deal, now);
                if (counterRefusal != PlayerbotDealRefusal.None)
                {
                    deal.Price = deal.QuotedPrice;
                    return Heard(PlayerbotDealHeardKind.Refused, counterRefusal, deal, deal.QuotedPrice);
                }

                return Heard(PlayerbotDealHeardKind.CounterAgreed, PlayerbotDealRefusal.None, deal, deal.Price);
            }

            deal.Price = deal.QuotedPrice;
            PlayerbotDealRefusal refusal = Agree(deal, now);
            return refusal == PlayerbotDealRefusal.None
                ? Heard(PlayerbotDealHeardKind.Agreed, PlayerbotDealRefusal.None, deal, deal.Price)
                : Heard(PlayerbotDealHeardKind.Refused, refusal, deal, deal.QuotedPrice);
        }
    }

    private static PlayerbotDealHeard Heard(PlayerbotDealHeardKind kind, PlayerbotDealRefusal refusal, Deal deal, long price)
        => new(kind, refusal, price, PlayerbotVoiceFacts.Price(price), deal.ItemName, deal.Count);

    private static readonly PlayerbotDealHeard NoHeard = new(PlayerbotDealHeardKind.None, PlayerbotDealRefusal.None, 0, string.Empty);

    /// <summary>
    /// A stranger's BeginTrade (SMSG_TRADE_STATUS 1) arrived at the bot. True when the bot has an agreed deal with <paramref name="trader"/>, or a Quoted one (opening
    /// the window is agreeing at the quoted price: the same checks as a yes) and is alive and out of combat, and the bot's trade slot is free or already the desk's;
    /// the slot is claimed for the desk before the party AI answers with CMSG_BEGIN_TRADE (a refused agreement releases it). <paramref name="live"/> is the bot's live
    /// trade (the trader's unopened request).
    /// </summary>
    internal bool Claims(Guid botId, ObjectGuid trader, Player bot, TradeSession? live)
    {
        ArgumentNullException.ThrowIfNull(bot);
        lock (_gate)
        {
            if (!Enabled || !bot.IsAlive || bot.Combat.IsInCombat || !_deals.TryGetValue(new DealKey(botId, trader.Value), out Deal? deal)) return false;
            long now = _clock();
            if (deal.State == PlayerbotDealState.Quoted)
            {
                if (now - deal.CreatedMs >= DealLifeMs)
                {
                    RemoveDeal(deal);
                    return false;
                }

                if (owners?.TryClaim(bot, PlayerbotTradeOwner.Desk, trader, live) == false) return false; // another driver holds the slot: no state change
                deal.Price = deal.QuotedPrice;
                if (Agree(deal, now) != PlayerbotDealRefusal.None)
                {
                    owners?.Release(bot, PlayerbotTradeOwner.Desk);
                    return false;
                }

                return true;
            }

            if (deal.State != PlayerbotDealState.Agreed || now - deal.AgreedMs >= DealLifeMs) return false;
            return owners?.TryClaim(bot, PlayerbotTradeOwner.Desk, trader, live) != false;
        }
    }

    private long DealLifeMs => Trading.Deals.DealMinutes * MinuteMs;

    /// <summary>The checks that turn a Quoted deal into an Agreed one (under the lock). Fixes the terms at <see cref="Deal.Price"/>.</summary>
    private PlayerbotDealRefusal Agree(Deal deal, long now)
    {
        if (!Enabled) return PlayerbotDealRefusal.Disabled;
        PlayerbotSocialTradingDealsOptions deals = Trading.Deals;
        PlayerbotSocialTradingLimitsOptions limits = Trading.Limits;
        PruneExpired(now);
        if (ActiveOf(deal.BotId) is { } active && !ReferenceEquals(active, deal)) return PlayerbotDealRefusal.Busy;
        int concurrent = 0;
        foreach (Deal other in _deals.Values)
            if (other.State is PlayerbotDealState.Agreed or PlayerbotDealState.Window && !ReferenceEquals(other, deal)) concurrent++;
        if (concurrent >= limits.MaxConcurrentTrades) return PlayerbotDealRefusal.Busy;
        if (recorder.Ledger?.IsScammer(deal.BotId, deal.Partner.Value) == true) return PlayerbotDealRefusal.Scammer;
        if (InCooldown(deal.BotId, deal.Partner.Value, now)) return PlayerbotDealRefusal.Cooldown;
        if (_botTrades.Count(deal.BotId, now) + PendingTrades(deal.BotId, null, deal) >= limits.MaxTradesPerBotPerHour) return PlayerbotDealRefusal.BotLimit;
        if (_accountTrades.Count(deal.PartnerAccount, now) + PendingTrades(null, deal.PartnerAccount, deal) >= limits.MaxTradesPerAccountPerHour) return PlayerbotDealRefusal.AccountLimit;
        (int trust, _) = RelationOf(deal.BotId, deal.Partner.Value);
        if (deal.Price > PlayerbotDealRules.DealCap(deals.MaxDealCopper, trust, Trading.LowTrustPermille)) return PlayerbotDealRefusal.TooMuch;
        PlayerbotDealTerms terms = TermsOf(deal);
        if (!PlayerbotDealRules.TermsAreSafe(terms)) return PlayerbotDealRefusal.NoPrice;
        long outflow = PlayerbotDealRules.Outflow(terms);
        if (outflow > 0)
        {
            long day = now / DayMs;
            long pendingBot = 0, pendingAccount = 0, pendingServer = 0;
            foreach (Deal other in _deals.Values)
            {
                if (other.State is not (PlayerbotDealState.Agreed or PlayerbotDealState.Window) || ReferenceEquals(other, deal)) continue;
                pendingServer += other.OutflowCopper;
                if (other.BotId == deal.BotId) pendingBot += other.OutflowCopper;
                if (other.PartnerAccount == deal.PartnerAccount) pendingAccount += other.OutflowCopper;
            }

            if (_botOutflow.Get(deal.BotId, day) + pendingBot + outflow > limits.DailyOutflowCopperPerBot
                || _accountOutflow.Get(deal.PartnerAccount, day) + pendingAccount + outflow > limits.DailyOutflowCopperPerAccount
                || ServerOutflow(day) + pendingServer + outflow > limits.DailyOutflowCopperServer) return PlayerbotDealRefusal.Outflow;
        }

        deal.OutflowCopper = outflow;
        deal.State = PlayerbotDealState.Agreed;
        deal.AgreedMs = now;
        return PlayerbotDealRefusal.None;
    }

    // --- driving the window ---------------------------------------------------------------------------------------

    /// <summary>
    /// The next client step for the bot's deal, at most one per call (the party AI calls it on every intake tick of a bot that has a deal, and again on a trade status).
    /// <paramref name="trade"/> is the bot's live trade (null when it has none), read by the caller in the same world-thread step. In order: drop expired deals; cancel and
    /// fail when the desk is off, the bot or the partner is dead, in combat or gone, or the window has been open (adopted as Window) longer than <c>Trading:TradeTimeoutSeconds</c>, counted from the moment it opened; initiate the trade
    /// when the deal is agreed, the partner is within <see cref="PlayerbotPartyAI.TradeReach"/> and the last request is 10 s old; when the live trade is the partner's and
    /// open, the deal is in its Window: the seller puts exactly the agreed stacks in (one SetTradeItem per call), the buyer puts the gold in once the partner side shows
    /// the agreed goods; a change of the partner side after the bot accepted is counted, and the second one cancels the deal as a scam attempt; the bot accepts only a
    /// window <see cref="PlayerbotDealRules.Judge"/> calls the agreed deal that both sides held still for <see cref="HoldMs"/>.
    /// </summary>
    internal PlayerbotDealStep Drive(Guid botId, Player bot, TradeSession? trade)
    {
        ArgumentNullException.ThrowIfNull(bot);
        lock (_gate)
        {
            if (!_byBot.ContainsKey(botId)) return default;
            long now = _clock();
            Deal? active = ActiveOf(botId);
            Player? liveOther = trade is null ? null : trade.OtherSide(bot).Player;
            // A live trade with the partner is the deal's only when the desk owns the bot's trade slot for it (it sent the initiate or the begin): any other window is left alone.
            bool windowIsDeal = active is not null && trade is not null && liveOther?.Guid == active.Partner
                && (owners is null || owners.Is(bot, PlayerbotTradeOwner.Desk, active.Partner, trade));
            if (!Enabled) return Disable(botId, active, windowIsDeal);
            DropExpiredQuoted(botId, now);
            if (active is null) return default;
            if (windowIsDeal && trade!.Opened && active.State == PlayerbotDealState.Agreed)
            {
                active.State = PlayerbotDealState.Window;
                active.WindowMs = now;
            }

            if (active.State == PlayerbotDealState.Window && trade is null)
            {
                // The server closes the trade before it sends the closing status: wait for WindowClosed (8 settles, any other terminal status fails),
                // and fail only when no status comes within the grace.
                if (active.GoneSinceMs == 0) active.GoneSinceMs = now;
                return now - active.GoneSinceMs > GoneGraceMs ? FailDeal(active, cancel: false, say: false, blame: false, now) : default;
            }

            active.GoneSinceMs = 0;
            Player? partner = windowIsDeal ? liveOther : FindPlayer(bot, active.Partner);
            // TradeTimeoutSeconds counts from the moment the window opened (adopted as Window); an unanswered initiate is bounded by DealMinutes and the initiate gap.
            bool timedOut = active.State == PlayerbotDealState.Window && now - active.WindowMs > Trading.TradeTimeoutSeconds * 1000L && trade is not { Settling: true };
            bool botDown = !bot.IsAlive || bot.Combat.IsInCombat;
            if (botDown || partner is null || !partner.IsAlive || partner.Combat.IsInCombat || timedOut)
                return FailDeal(active, cancel: windowIsDeal, say: true, blame: !botDown, now); // the bot's own death or fight carries no blame
            if (active.State == PlayerbotDealState.Agreed && now - active.AgreedMs >= DealLifeMs)
            {
                // Flaked only when the bot was free, or its own request was never opened by the partner; a slot held by another driver or a stranger's request is nobody's flake.
                bool flaked = trade is null || windowIsDeal;
                if (flaked) recorder.NoteById(active.BotId, active.BotGuid, active.Partner, PlayerbotSocialEvent.DealFlaked, active.CrossFaction);
                return FailDeal(active, cancel: windowIsDeal, say: false, blame: flaked, now);
            }

            if (active.State == PlayerbotDealState.Agreed)
            {
                if (trade is not null && !windowIsDeal)
                {
                    // Another driver's window, or a stranger's request: defer. The deal-life clock does not run while it waits, and the wait is bounded.
                    if (active.DeferredSinceMs == 0) active.DeferredSinceMs = now;
                    active.AgreedMs = now;
                    return now - active.DeferredSinceMs > DeferMaxMs ? FailDeal(active, cancel: false, say: true, blame: false, now) : default;
                }

                active.DeferredSinceMs = 0;
                if (trade is not null) return default; // the desk's own request, not yet opened by the partner (the deal-life clock runs)
                if (!ReferenceEquals(bot.Map, partner.Map) || PlayerbotGeometry.Distance(bot, partner) > PlayerbotPartyAI.TradeReach) return default;
                if (active.InitiatedMs != 0 && now - active.InitiatedMs < InitiateGapMs) return default;
                if (owners?.TryClaim(bot, PlayerbotTradeOwner.Desk, active.Partner, null) == false) return default;
                active.InitiatedMs = now;
                return new PlayerbotDealStep(PlayerbotDealStepKind.Initiate, active.Partner, PartnerName: active.PartnerName);
            }

            // State Window.
            if (!windowIsDeal || trade is null) return FailDeal(active, cancel: false, say: false, blame: false, now); // the window vanished without a status
            if (trade.Settling) return default;
            return DriveWindow(active, bot, trade, now);
        }
    }

    private PlayerbotDealStep Disable(Guid botId, Deal? active, bool windowIsDeal)
    {
        // Off: every deal of the bot is dropped; a window the desk drives is cancelled with one line.
        long now = _clock();
        if (active is null)
        {
            ForgetBot(botId);
            return default;
        }

        PlayerbotDealStep step = FailDeal(active, cancel: windowIsDeal, say: true, blame: false, now);
        ForgetBot(botId);
        return step;
    }

    private PlayerbotDealStep DriveWindow(Deal deal, Player bot, TradeSession trade, long now)
    {
        TradeSide mine = trade.SideOf(bot), theirs = trade.OtherSide(bot);
        var botGoods = new List<PlayerbotDealGoods>(TradeRules.TradedSlotCount);
        var partnerGoods = new List<PlayerbotDealGoods>(TradeRules.TradedSlotCount);
        long botFingerprint = Fingerprint(mine, botGoods);
        long partnerFingerprint = Fingerprint(theirs, partnerGoods);
        long both = PlayerbotDealRules.Combine(botFingerprint, partnerFingerprint);
        if (deal.StableFingerprint != both || deal.StableSinceMs == 0)
        {
            deal.StableFingerprint = both;
            deal.StableSinceMs = now;
        }

        // Scam watch: the bot accepted, and its accept is gone. A changed partner side is a change after the bot accepted; the same side is only the server's bounce.
        if (deal.BotAccepted && !mine.Accepted)
        {
            if (partnerFingerprint != deal.PartnerFingerprintAtAccept) deal.Resets++;
            deal.BotAccepted = false;
            if (deal.Resets >= 2)
            {
                recorder.NoteById(deal.BotId, deal.BotGuid, deal.Partner, PlayerbotSocialEvent.TradeScam, deal.CrossFaction);
                return FailDeal(deal, cancel: true, say: true, blame: true, now);
            }
        }

        PlayerbotDealTerms terms = TermsOf(deal);
        var window = new PlayerbotDealWindow(botGoods, mine.Gold, partnerGoods, theirs.Gold);
        if (deal.Role == PlayerbotDealRole.Seller)
        {
            List<Item>? plan = PlanStacks(bot, deal.Entry, deal.Count);
            if (plan is null) return FailDeal(deal, cancel: true, say: true, blame: false, now); // the bot cannot make the count: no blame on the partner
            if (mine.Gold > 0)
            {
                deal.LastPutMs = now;
                return new PlayerbotDealStep(PlayerbotDealStepKind.PutGold, deal.Partner, Gold: 0); // a seller never has gold on its side
            }
            for (int slot = 0; slot < TradeRules.TradedSlotCount; slot++)
            {
                ObjectGuid offered = mine[slot];
                if (slot < plan.Count)
                {
                    if (offered.IsEmpty)
                    {
                        deal.LastPutMs = now;
                        return new PlayerbotDealStep(PlayerbotDealStepKind.PutItem, deal.Partner, (byte)slot, plan[slot].BagSlot, plan[slot].Slot);
                    }

                    if (offered != plan[slot].Guid) return FailDeal(deal, cancel: true, say: true, blame: false, now); // something the plan does not hold on the bot's own side
                }
                else if (!offered.IsEmpty) return FailDeal(deal, cancel: true, say: true, blame: false, now);
            }
        }
        else
        {
            if (botGoods.Count > 0) return FailDeal(deal, cancel: true, say: true, blame: false, now); // a buyer never offers items
            bool partnerOffers = PlayerbotDealRules.Matches(partnerGoods, terms);
            if (partnerOffers && mine.Gold != deal.Price)
            {
                if (bot.Money < deal.Price) return FailDeal(deal, cancel: true, say: true, blame: false, now);
                deal.LastPutMs = now;
                return new PlayerbotDealStep(PlayerbotDealStepKind.PutGold, deal.Partner, Gold: (uint)deal.Price);
            }

            if (!partnerOffers && mine.Gold > 0)
            {
                deal.LastPutMs = now;
                return new PlayerbotDealStep(PlayerbotDealStepKind.PutGold, deal.Partner, Gold: 0);
            }
        }

        PlayerbotDealVerdict verdict = PlayerbotDealRules.Judge(terms, window);
        if (verdict == PlayerbotDealVerdict.Unsafe) return FailDeal(deal, cancel: true, say: true, blame: true, now);
        if (verdict != PlayerbotDealVerdict.Accept || mine.Accepted) return default;
        if (now - deal.StableSinceMs < HoldMs || now - deal.LastPutMs < HoldMs || (deal.LastAcceptMs != 0 && now - deal.LastAcceptMs < HoldMs)) return default;
        deal.BotAccepted = true;
        deal.PartnerFingerprintAtAccept = partnerFingerprint;
        deal.LastAcceptMs = now;
        return new PlayerbotDealStep(PlayerbotDealStepKind.Accept, deal.Partner);
    }

    /// <summary>The goods on one side of the live window and a fingerprint of the side (entries, counts, item guids and gold).</summary>
    private static long Fingerprint(TradeSide side, List<PlayerbotDealGoods> goods)
    {
        Span<ulong> guids = stackalloc ulong[TradeRules.TradedSlotCount];
        int n = 0;
        foreach (ObjectGuid guid in side.TradedItems)
        {
            Item? item = side.Player.Inventory.GetItemByGuid(guid);
            goods.Add(item is null ? new PlayerbotDealGoods(0, 0, true) : new PlayerbotDealGoods(item.Entry, item.Count, item.IsSoulBound));
            if (n < guids.Length) guids[n++] = guid.Value;
        }

        return PlayerbotDealRules.Fingerprint(goods, side.Gold, guids[..n]);
    }

    /// <summary>
    /// The stacks a seller puts in: one stack of exactly <paramref name="count"/>, else the largest stacks first (a stack larger than what remains is skipped; the desk
    /// never splits one) within the six traded slots; null when the count cannot be made exactly. Only tradeable bag items count (not soulbound, equipped or banked).
    /// </summary>
    internal static List<Item>? PlanStacks(Player bot, uint entry, uint count)
    {
        var stacks = new List<Item>();
        foreach (Item item in bot.Inventory.AllItems)
            if (item.Entry == entry && item.Count > 0 && bot.Inventory.CanBeTraded(item) == InventoryResult.Ok) stacks.Add(item);
        stacks.Sort(static (a, b) =>
        {
            int byCount = b.Count.CompareTo(a.Count);
            if (byCount != 0) return byCount;
            int byBag = a.BagSlot.CompareTo(b.BagSlot);
            return byBag != 0 ? byBag : a.Slot.CompareTo(b.Slot);
        });
        for (int i = stacks.Count - 1; i >= 0; i--) // an exact single stack first: the one lowest in the bags
            if (stacks[i].Count == count)
            {
                int best = i;
                for (int j = i - 1; j >= 0 && stacks[j].Count == count; j--) best = j;
                return [stacks[best]];
            }

        var plan = new List<Item>(TradeRules.TradedSlotCount);
        long remaining = count;
        foreach (Item stack in stacks)
        {
            if (remaining == 0 || plan.Count >= TradeRules.TradedSlotCount) break;
            if (stack.Count > remaining) continue;
            plan.Add(stack);
            remaining -= stack.Count;
        }

        return remaining == 0 ? plan : null;
    }

    // --- window closed ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The bot's trade window closed (SMSG_TRADE_STATUS: <paramref name="completed"/> for 8, any other terminal status otherwise). A desk deal in its Window that
    /// completed is Settled: a fair deal is noted, the trade is counted (per bot and per partner account), the outflow joins the daily buckets, and the deal is
    /// forgotten; the outcome names the goods so the caller spends the bot's board post. Not completed, a deal in its Window (never an agreed one: an unrelated close leaves it alone) is
    /// Failed: pair cooldown. A bot with no such deal gets <see cref="PlayerbotDealOutcomeKind.None"/>.
    /// </summary>
    internal PlayerbotDealOutcome WindowClosed(Guid botId, bool completed)
    {
        lock (_gate)
        {
            Deal? deal = ActiveOf(botId);
            if (deal is null) return default;
            long now = _clock();
            if (completed)
            {
                if (deal.State != PlayerbotDealState.Window) return default;
                recorder.NoteById(deal.BotId, deal.BotGuid, deal.Partner, PlayerbotSocialEvent.FairDeal, deal.CrossFaction);
                _botTrades.Add(deal.BotId, now);
                _accountTrades.Add(deal.PartnerAccount, now);
                if (deal.OutflowCopper > 0)
                {
                    long day = now / DayMs;
                    _botOutflow.Add(deal.BotId, day, deal.OutflowCopper);
                    _accountOutflow.Add(deal.PartnerAccount, day, deal.OutflowCopper);
                    if (_serverDay != day)
                    {
                        _serverDay = day;
                        _serverOutflow = 0;
                    }

                    _serverOutflow += deal.OutflowCopper;
                }

                RemoveDeal(deal);
                owners?.Release(deal.BotGuid, PlayerbotTradeOwner.Desk);
                return new PlayerbotDealOutcome(PlayerbotDealOutcomeKind.Settled, deal.Role, deal.Entry, deal.Count);
            }

            if (deal.State != PlayerbotDealState.Window) return default; // an unrelated close (a tip window, the partner bouncing the initiate) leaves an agreed deal alone
            SetCooldown(deal.BotId, deal.Partner.Value, now); // the partner closed a desk window
            RemoveDeal(deal);
            owners?.Release(deal.BotGuid, PlayerbotTradeOwner.Desk);
            return new PlayerbotDealOutcome(PlayerbotDealOutcomeKind.Failed, deal.Role, deal.Entry, deal.Count);
        }
    }

    // --- internals (all under the lock) ----------------------------------------------------------------------------

    /// <summary>Ends a deal. The pair cooldown starts only when <paramref name="blame"/> (the partner caused the failure); the bot's trade slot is released either way.</summary>
    private PlayerbotDealStep FailDeal(Deal deal, bool cancel, bool say, bool blame, long now)
    {
        if (blame) SetCooldown(deal.BotId, deal.Partner.Value, now);
        RemoveDeal(deal);
        owners?.Release(deal.BotGuid, PlayerbotTradeOwner.Desk);
        return new PlayerbotDealStep(cancel ? PlayerbotDealStepKind.Cancel : PlayerbotDealStepKind.None, deal.Partner, SayKey: say ? DealOffKey : null, PartnerName: deal.PartnerName);
    }

    private static PlayerbotDealTerms TermsOf(Deal deal) => new(deal.Role, deal.Entry, deal.Count, deal.Price, deal.VendorFloor, deal.ReferenceValue);

    /// <summary>Agreed and open deals (not yet counted as settled) of one bot or one partner account, other than <paramref name="except"/>: they count against the hourly limits.</summary>
    private int PendingTrades(Guid? botId, int? account, Deal? except)
    {
        int pending = 0;
        foreach (Deal deal in _deals.Values)
            if (deal.State is PlayerbotDealState.Agreed or PlayerbotDealState.Window && !ReferenceEquals(deal, except)
                && (botId is null || deal.BotId == botId) && (account is null || deal.PartnerAccount == account)) pending++;
        return pending;
    }

    private Deal? ActiveOf(Guid botId)
    {
        if (!_byBot.TryGetValue(botId, out List<Deal>? list)) return null;
        foreach (Deal deal in list)
            if (deal.State is PlayerbotDealState.Agreed or PlayerbotDealState.Window) return deal;
        return null;
    }

    private void AddDeal(Deal deal)
    {
        _deals[new DealKey(deal.BotId, deal.Partner.Value)] = deal;
        if (!_byBot.TryGetValue(deal.BotId, out List<Deal>? list)) _byBot[deal.BotId] = list = [];
        list.Add(deal);
        Volatile.Write(ref _count, _deals.Count);
    }

    private void RemoveDeal(Deal deal)
    {
        _deals.Remove(new DealKey(deal.BotId, deal.Partner.Value));
        if (_byBot.TryGetValue(deal.BotId, out List<Deal>? list))
        {
            list.Remove(deal);
            if (list.Count == 0) _byBot.Remove(deal.BotId);
        }

        Volatile.Write(ref _count, _deals.Count);
    }

    private void ForgetBot(Guid botId)
    {
        if (!_byBot.TryGetValue(botId, out List<Deal>? list)) return;
        foreach (Deal deal in list.ToArray()) RemoveDeal(deal);
    }

    private void DropExpiredQuoted(Guid botId, long now)
    {
        if (!_byBot.TryGetValue(botId, out List<Deal>? list)) return;
        long life = DealLifeMs;
        foreach (Deal deal in list.ToArray())
            if (deal.State == PlayerbotDealState.Quoted && now - deal.CreatedMs >= life) RemoveDeal(deal);
    }

    private void TrimQuoted(Guid botId)
    {
        if (!_byBot.TryGetValue(botId, out List<Deal>? list)) return;
        while (true)
        {
            int quoted = 0;
            Deal? oldest = null;
            foreach (Deal deal in list)
            {
                if (deal.State != PlayerbotDealState.Quoted) continue;
                quoted++;
                if (oldest is null || deal.CreatedMs < oldest.CreatedMs) oldest = deal;
            }

            if (quoted <= MaxQuotedPerBot || oldest is null) return;
            RemoveDeal(oldest);
        }
    }

    private bool EvictOldestQuoted()
    {
        Deal? oldest = null;
        foreach (Deal deal in _deals.Values)
            if (deal.State == PlayerbotDealState.Quoted && (oldest is null || deal.CreatedMs < oldest.CreatedMs)) oldest = deal;
        if (oldest is null) return false;
        RemoveDeal(oldest);
        return true;
    }

    /// <summary>At most once a second: drops quoted and agreed deals past their life and windows far past their timeout (a bot that went away never drives its deals).</summary>
    private void PruneExpired(long now)
    {
        if (now - _prunedMs < 1000 && _prunedMs != 0) return;
        _prunedMs = now;
        long life = DealLifeMs;
        long windowLife = (Trading.TradeTimeoutSeconds * 2000L) + 5000;
        foreach (Deal deal in _deals.Values.ToArray())
        {
            switch (deal.State)
            {
                case PlayerbotDealState.Quoted when now - deal.CreatedMs >= life:
                    RemoveDeal(deal);
                    break;
                case PlayerbotDealState.Agreed when now - deal.AgreedMs >= life:
                    recorder.NoteById(deal.BotId, deal.BotGuid, deal.Partner, PlayerbotSocialEvent.DealFlaked, deal.CrossFaction);
                    SetCooldown(deal.BotId, deal.Partner.Value, now);
                    RemoveDeal(deal);
                    break;
                case PlayerbotDealState.Window when now - deal.WindowMs >= windowLife:
                    SetCooldown(deal.BotId, deal.Partner.Value, now);
                    RemoveDeal(deal);
                    break;
            }
        }
    }

    private bool InCooldown(Guid botId, ulong partner, long now)
        => _cooldowns.TryGetValue((botId, partner), out long until) && now < until;

    private void SetCooldown(Guid botId, ulong partner, long now)
    {
        long minutes = Trading.Deals.PairCooldownMinutes;
        if (minutes <= 0) return;
        if (_cooldowns.Count >= MapCap && !_cooldowns.ContainsKey((botId, partner)))
        {
            foreach (KeyValuePair<(Guid Bot, ulong Partner), long> entry in _cooldowns.ToArray())
                if (entry.Value <= now) _cooldowns.Remove(entry.Key);
            if (_cooldowns.Count >= MapCap)
            {
                (Guid Bot, ulong Partner) oldest = default;
                long oldestUntil = long.MaxValue;
                foreach (KeyValuePair<(Guid Bot, ulong Partner), long> entry in _cooldowns)
                    if (entry.Value < oldestUntil) (oldest, oldestUntil) = (entry.Key, entry.Value);
                _cooldowns.Remove(oldest);
            }
        }

        _cooldowns[(botId, partner)] = now + (minutes * MinuteMs);
    }

    private (int Trust, int Affinity) RelationOf(Guid botId, ulong partner)
        => recorder.Memory.TryGet(botId, partner, out PlayerbotRelation relation) ? (relation.TrustPermille, recorder.Memory.EffectiveAffinity(relation)) : (500, 0);

    private long ServerOutflow(long day) => _serverDay == day ? _serverOutflow : 0;

    private static uint TradeableCount(Player bot, uint entry)
    {
        long total = 0;
        foreach (Item item in bot.Inventory.AllItems)
            if (item.Entry == entry && bot.Inventory.CanBeTraded(item) == InventoryResult.Ok) total += item.Count;
        return (uint)Math.Min(total, uint.MaxValue);
    }

    private static Player? FindPlayer(Player bot, ObjectGuid guid)
        => bot.Session is WorldSession session ? session.World.FindOnlinePlayer(guid) : null;

    private readonly record struct DealKey(Guid Bot, ulong Partner);

    private sealed class Deal
    {
        public Guid BotId;
        public ObjectGuid BotGuid;
        public ObjectGuid Partner;
        public string PartnerName = string.Empty;
        public int PartnerAccount;
        public bool CrossFaction;
        public PlayerbotDealRole Role;
        public uint Entry;
        public uint Count;
        public string ItemName = string.Empty;
        public long QuotedPrice;
        public long Price;
        public long VendorFloor;
        public long ReferenceValue;
        public long Limit;
        public long OutflowCopper;
        public bool CounterUsed;
        public PlayerbotDealState State;
        public long CreatedMs;
        public long AgreedMs;
        public long WindowMs;
        public long InitiatedMs;
        public long DeferredSinceMs;
        public bool BotAccepted;
        public long PartnerFingerprintAtAccept;
        public long StableFingerprint;
        public long StableSinceMs;
        public long LastPutMs;
        public long LastAcceptMs;
        public long GoneSinceMs;
        public int Resets;
    }

    /// <summary>Counts events per key inside a sliding hour (bounded: trimmed past <see cref="MapCap"/> keys).</summary>
    private sealed class SlidingCounts<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, Queue<long>> _map = [];

        public int Count(TKey key, long now)
        {
            if (!_map.TryGetValue(key, out Queue<long>? queue)) return 0;
            while (queue.Count > 0 && now - queue.Peek() >= HourMs) queue.Dequeue();
            if (queue.Count == 0)
            {
                _map.Remove(key);
                return 0;
            }

            return queue.Count;
        }

        public void Add(TKey key, long now)
        {
            if (!_map.TryGetValue(key, out Queue<long>? queue))
            {
                if (_map.Count >= MapCap) Trim(now);
                _map[key] = queue = new Queue<long>();
            }

            queue.Enqueue(now);
            while (queue.Count > 256) queue.Dequeue();
        }

        private void Trim(long now)
        {
            foreach (KeyValuePair<TKey, Queue<long>> entry in _map.ToArray())
                if (entry.Value.Count == 0 || (now - entry.Value.Peek() >= HourMs && now - entry.Value.Last() >= HourMs)) _map.Remove(entry.Key);
            if (_map.Count < MapCap) return;
            TKey? oldest = default;
            long oldestAt = long.MaxValue;
            foreach (KeyValuePair<TKey, Queue<long>> entry in _map)
            {
                long at = entry.Value.Count == 0 ? long.MinValue : entry.Value.Last();
                if (at < oldestAt) (oldest, oldestAt) = (entry.Key, at);
            }

            if (oldest is not null) _map.Remove(oldest); // the entry written longest ago goes; the counts of the others are kept
        }
    }

    /// <summary>Sums copper per key for one UTC day (a new day forgets the old total; bounded past <see cref="MapCap"/> keys).</summary>
    private sealed class DailyTotals<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, (long Day, long Amount)> _map = [];

        public long Get(TKey key, long day) => _map.TryGetValue(key, out (long Day, long Amount) entry) && entry.Day == day ? entry.Amount : 0;

        public void Add(TKey key, long day, long amount)
        {
            if (!_map.ContainsKey(key) && _map.Count >= MapCap)
            {
                foreach (KeyValuePair<TKey, (long Day, long Amount)> entry in _map.ToArray())
                    if (entry.Value.Day != day) _map.Remove(entry.Key);
                if (_map.Count >= MapCap)
                {
                    TKey? oldest = default;
                    long oldestDay = long.MaxValue;
                    foreach (KeyValuePair<TKey, (long Day, long Amount)> entry in _map)
                        if (entry.Value.Day < oldestDay) (oldest, oldestDay) = (entry.Key, entry.Value.Day);
                    if (oldest is not null) _map.Remove(oldest);
                }
            }

            _map[key] = (day, Get(key, day) + amount);
        }
    }
}
