namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Trading</c>: bots answering trade windows, fixed-price deals, group gifts and the outflow limits (P4a). On by default.
/// The keys are live (a change applies without a restart). The trade desk (<c>PlayerbotTradeDesk</c>) reads <see cref="Enabled"/>, <see cref="LowTrustPermille"/>,
/// <see cref="TradeTimeoutSeconds"/> and every key of <see cref="Deals"/> (except the price book keys, read by the price book of design lane T1, not yet built) and <see cref="Limits"/>;
/// <see cref="ScamMemoryDays"/> and <see cref="NinjaMemoryDays"/> are read by the trade ledger. The gift keys (<see cref="AnswerTrades"/>, <see cref="AcceptGifts"/>, <see cref="GiftMinValueCopper"/>,
/// <see cref="MaxGiftsReceivedPerHour"/>, <see cref="Gifts"/>) are read by later P4 steps and nothing yet. The desk runs only while <c>World:Playerbots:Enabled</c> is on; the deal talk
/// also needs <c>World:Playerbots:Chat:Enabled</c> and the bot voice. The scam and ninja memory of a partner is kept in the bot's trade ledger (counts and times only).
/// </summary>
public sealed class PlayerbotSocialTradingOptions
{
    /// <summary>Master switch (on by default). Off stops the trade desk quoting and agreeing deals and cancels every window it drives at its next pass.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Busy or ignore a trade window nobody expected, and open an allowed one as a gift window (on by default).</summary>
    public bool AnswerTrades { get; set; } = true;

    /// <summary>Accept gift windows (an empty bot side) from partners the rules allow (on by default).</summary>
    public bool AcceptGifts { get; set; } = true;

    /// <summary>The seconds one trade may take from the initiate to the close, a running settlement not counted (10..300, default 45).</summary>
    public int TradeTimeoutSeconds { get; set; } = 45;

    /// <summary>The days a bot refuses every trade and gift from a partner who scammed it (0..365, default 30; 0 forgets at once, the scam still costs affinity and trust).</summary>
    public int ScamMemoryDays { get; set; } = 30;

    /// <summary>The days a bot gives a partner who looted ninja-style no group gifts and asks more (0..365, default 14).</summary>
    public int NinjaMemoryDays { get; set; } = 14;

    /// <summary>The trust (permille) below which a partner's deals are capped at a quarter of the deal maximum (0..1000, default 350).</summary>
    public int LowTrustPermille { get; set; } = 350;

    /// <summary>The vendor value in copper below which a gift earns no affinity and a stranger's gift is cancelled (0..1000000, default 100).</summary>
    public int GiftMinValueCopper { get; set; } = 100;

    /// <summary>The gifts one bot accepts from one partner in an hour (0..120, default 10).</summary>
    public int MaxGiftsReceivedPerHour { get; set; } = 10;

    /// <summary>Fixed-price deals (<c>World:Playerbots:Social:Trading:Deals</c>).</summary>
    public PlayerbotSocialTradingDealsOptions Deals { get; set; } = new();

    /// <summary>Group gifts (<c>World:Playerbots:Social:Trading:Gifts</c>).</summary>
    public PlayerbotSocialTradingGiftsOptions Gifts { get; set; } = new();

    /// <summary>Rate limits and the daily outflow caps (<c>World:Playerbots:Social:Trading:Limits</c>).</summary>
    public PlayerbotSocialTradingLimitsOptions Limits { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Trading";
        if (Deals is null) throw new InvalidOperationException($"{section}:Deals is missing.");
        if (Gifts is null) throw new InvalidOperationException($"{section}:Gifts is missing.");
        if (Limits is null) throw new InvalidOperationException($"{section}:Limits is missing.");
        PlayerbotTradingRange.Check(section, TradeTimeoutSeconds, 10, 300, nameof(TradeTimeoutSeconds));
        PlayerbotTradingRange.Check(section, ScamMemoryDays, 0, 365, nameof(ScamMemoryDays));
        PlayerbotTradingRange.Check(section, NinjaMemoryDays, 0, 365, nameof(NinjaMemoryDays));
        PlayerbotTradingRange.Check(section, LowTrustPermille, 0, 1000, nameof(LowTrustPermille));
        PlayerbotTradingRange.Check(section, GiftMinValueCopper, 0, 1_000_000, nameof(GiftMinValueCopper));
        PlayerbotTradingRange.Check(section, MaxGiftsReceivedPerHour, 0, 120, nameof(MaxGiftsReceivedPerHour));
        Deals.Validate();
        Gifts.Validate();
        Limits.Validate();
    }
}

/// <summary>
/// <c>World:Playerbots:Social:Trading:Deals</c>: a bot completes its own WTS or WTB post at a fixed price through the trade window. Read by
/// <c>PlayerbotTradeDesk</c> (every key but the price book ones).
/// </summary>
public sealed class PlayerbotSocialTradingDealsOptions
{
    /// <summary>Fixed-price WTS completion (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The minutes an agreed deal stays open (1..60, default 5).</summary>
    public int DealMinutes { get; set; } = 5;

    /// <summary>The minutes a pair waits after a deal expired, was declined or was cancelled before a new one (0..1440, default 10).</summary>
    public int PairCooldownMinutes { get; set; } = 10;

    /// <summary>The most a single deal may be worth in copper (0..100000000, default 500000 = 50 g; 0 disables deals).</summary>
    public int MaxDealCopper { get; set; } = 500_000;

    /// <summary>The ask discount in percent for a friend at full affinity, scaled by the bot's giving affinity (0..100, default 20).</summary>
    public int FriendDiscountPercent { get; set; } = 20;

    /// <summary>The ask markup in percent for a partner the bot dislikes, scaled by the negative affinity (0..200, default 25).</summary>
    public int DislikeMarkupPercent { get; set; } = 25;

    /// <summary>The hours an auction house observation is kept in the price book (1..720, default 24). Read by the price book (design lane T1), not yet by the desk.</summary>
    public int PriceBookHours { get; set; } = 24;

    /// <summary>The distinct seller accounts an item needs before an auction house reading prices a deal (1..20, default 3). Read by the price book (design lane T1), not yet by the desk.</summary>
    public int PriceBookMinSellers { get; set; } = 3;

    /// <summary>A counter-offer the bot accepts: at most this share under its ask when selling (never under the vendor value); one counter per deal, then it holds its price (0..50, default 10).</summary>
    public int CounterTolerancePercent { get; set; } = 10;

    /// <summary>The most a buying bot pays over the item's reference price in percent; never more than the reference divided by 0.67 (0..100, default 50).</summary>
    public int BuyMaxPremiumPercent { get; set; } = 50;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Trading:Deals";
        PlayerbotTradingRange.Check(section, DealMinutes, 1, 60, nameof(DealMinutes));
        PlayerbotTradingRange.Check(section, PairCooldownMinutes, 0, 1440, nameof(PairCooldownMinutes));
        PlayerbotTradingRange.Check(section, MaxDealCopper, 0, 100_000_000, nameof(MaxDealCopper));
        PlayerbotTradingRange.Check(section, FriendDiscountPercent, 0, 100, nameof(FriendDiscountPercent));
        PlayerbotTradingRange.Check(section, DislikeMarkupPercent, 0, 200, nameof(DislikeMarkupPercent));
        PlayerbotTradingRange.Check(section, PriceBookHours, 1, 720, nameof(PriceBookHours));
        PlayerbotTradingRange.Check(section, PriceBookMinSellers, 1, 20, nameof(PriceBookMinSellers));
        PlayerbotTradingRange.Check(section, CounterTolerancePercent, 0, 50, nameof(CounterTolerancePercent));
        PlayerbotTradingRange.Check(section, BuyMaxPremiumPercent, 0, 100, nameof(BuyMaxPremiumPercent));
    }
}

/// <summary><c>World:Playerbots:Social:Trading:Gifts</c>: a bot hands an item it cannot use to a groupmate it is an upgrade for.</summary>
public sealed class PlayerbotSocialTradingGiftsOptions
{
    /// <summary>Group gifts after a run (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The seconds a bot listens for an answer to its "anyone need" line (5..120, default 15).</summary>
    public int GiftClaimSeconds { get; set; } = 15;

    /// <summary>The most a gift may be worth in copper at level 60, scaled down by the bot's level (0..1000000, default 5000; 0 means no gifts).</summary>
    public int MaxGiftValueCopper { get; set; } = 5000;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Trading:Gifts";
        PlayerbotTradingRange.Check(section, GiftClaimSeconds, 5, 120, nameof(GiftClaimSeconds));
        PlayerbotTradingRange.Check(section, MaxGiftValueCopper, 0, 1_000_000, nameof(MaxGiftValueCopper));
    }
}

/// <summary><c>World:Playerbots:Social:Trading:Limits</c>: rate limits and the daily outflow caps, read by <c>PlayerbotTradeDesk</c>. War Mode never lifts them.</summary>
public sealed class PlayerbotSocialTradingLimitsOptions
{
    /// <summary>The trades one bot may complete with real players in an hour (0..120, default 6).</summary>
    public int MaxTradesPerBotPerHour { get; set; } = 6;

    /// <summary>The trades all bots together may complete toward one real account in an hour (0..120, default 10).</summary>
    public int MaxTradesPerAccountPerHour { get; set; } = 10;

    /// <summary>The trade windows open at once in the world (0..256, default 16).</summary>
    public int MaxConcurrentTrades { get; set; } = 16;

    /// <summary>The copper one bot may give real players per UTC day, gifts and discounts counted (0..100000000, default 20000 = 2 g).</summary>
    public int DailyOutflowCopperPerBot { get; set; } = 20_000;

    /// <summary>The copper one real account, all its characters together, may receive from all bots per UTC day (0..100000000, default 20000 = 2 g).</summary>
    public int DailyOutflowCopperPerAccount { get; set; } = 20_000;

    /// <summary>The copper all bots together may give real players per UTC day (0..1000000000, default 2000000 = 200 g).</summary>
    public int DailyOutflowCopperServer { get; set; } = 2_000_000;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Trading:Limits";
        PlayerbotTradingRange.Check(section, MaxTradesPerBotPerHour, 0, 120, nameof(MaxTradesPerBotPerHour));
        PlayerbotTradingRange.Check(section, MaxTradesPerAccountPerHour, 0, 120, nameof(MaxTradesPerAccountPerHour));
        PlayerbotTradingRange.Check(section, MaxConcurrentTrades, 0, 256, nameof(MaxConcurrentTrades));
        PlayerbotTradingRange.Check(section, DailyOutflowCopperPerBot, 0, 100_000_000, nameof(DailyOutflowCopperPerBot));
        PlayerbotTradingRange.Check(section, DailyOutflowCopperPerAccount, 0, 100_000_000, nameof(DailyOutflowCopperPerAccount));
        PlayerbotTradingRange.Check(section, DailyOutflowCopperServer, 0, 1_000_000_000, nameof(DailyOutflowCopperServer));
    }
}

internal static class PlayerbotTradingRange
{
    public static void Check(string section, int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
    }
}
