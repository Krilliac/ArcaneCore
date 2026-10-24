namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Trading</c>: bots answering trade windows, fixed-price deals, group gifts and the outflow limits (P4a).
/// TEST-AUTHOR STUB: the defaults are the design's (p4-trading-design.md section 5); <see cref="Validate"/> and the live keys are the implementer's (S1).
/// </summary>
public sealed class PlayerbotSocialTradingOptions
{
    public bool Enabled { get; set; } = true;
    public bool AnswerTrades { get; set; } = true;
    public bool AcceptGifts { get; set; } = true;
    public int TradeTimeoutSeconds { get; set; } = 45;
    public int ScamMemoryDays { get; set; } = 30;
    public int NinjaMemoryDays { get; set; } = 14;
    public int LowTrustPermille { get; set; } = 350;
    public int GiftMinValueCopper { get; set; } = 100;
    public int MaxGiftsReceivedPerHour { get; set; } = 10;
    public PlayerbotSocialTradingDealsOptions Deals { get; set; } = new();
    public PlayerbotSocialTradingGiftsOptions Gifts { get; set; } = new();
    public PlayerbotSocialTradingLimitsOptions Limits { get; set; } = new();

    public void Validate()
    {
    }
}

public sealed class PlayerbotSocialTradingDealsOptions
{
    public bool Enabled { get; set; } = true;
    public int DealMinutes { get; set; } = 5;
    public int PairCooldownMinutes { get; set; } = 10;
    public int MaxDealCopper { get; set; } = 500_000;
    public int FriendDiscountPercent { get; set; } = 20;
    public int DislikeMarkupPercent { get; set; } = 25;
    public int PriceBookHours { get; set; } = 24;
    public int PriceBookMinSellers { get; set; } = 3;
}

public sealed class PlayerbotSocialTradingGiftsOptions
{
    public bool Enabled { get; set; } = true;
    public int GiftClaimSeconds { get; set; } = 15;
    public int MaxGiftValueCopper { get; set; } = 5000;
}

public sealed class PlayerbotSocialTradingLimitsOptions
{
    public int MaxTradesPerBotPerHour { get; set; } = 6;
    public int MaxTradesPerAccountPerHour { get; set; } = 10;
    public int MaxConcurrentTrades { get; set; } = 16;
    public int DailyOutflowCopperPerBot { get; set; } = 20_000;
    public int DailyOutflowCopperPerAccount { get; set; } = 20_000;
    public int DailyOutflowCopperServer { get; set; } = 2_000_000;
}
