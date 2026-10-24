using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social.Trading;

/// <summary>P4 S1: <c>World:Playerbots:Social:Trading</c> and the new <c>Memory</c> keys: the design's defaults and ranges (design 5), wired into the social options.</summary>
public sealed class PlayerbotTradeOptionsTests
{
    // (group, key, minimum, maximum): the Range column of the design's option table. The group is "" for the section itself.
    public static IEnumerable<object[]> Ranges() =>
    [
        ["", "TradeTimeoutSeconds", 10, 300],
        ["", "ScamMemoryDays", 0, 365],
        ["", "NinjaMemoryDays", 0, 365],
        ["", "LowTrustPermille", 0, 1000],
        ["", "GiftMinValueCopper", 0, 1_000_000],
        ["", "MaxGiftsReceivedPerHour", 0, 120],
        ["Deals", "DealMinutes", 1, 60],
        ["Deals", "PairCooldownMinutes", 0, 1440],
        ["Deals", "MaxDealCopper", 0, 100_000_000],
        ["Deals", "FriendDiscountPercent", 0, 100],
        ["Deals", "DislikeMarkupPercent", 0, 200],
        ["Deals", "PriceBookHours", 1, 720],
        ["Deals", "PriceBookMinSellers", 1, 20],
        ["Gifts", "GiftClaimSeconds", 5, 120],
        ["Gifts", "MaxGiftValueCopper", 0, 1_000_000],
        ["Limits", "MaxTradesPerBotPerHour", 0, 120],
        ["Limits", "MaxTradesPerAccountPerHour", 0, 120],
        ["Limits", "MaxConcurrentTrades", 0, 256],
        ["Limits", "DailyOutflowCopperPerBot", 0, 100_000_000],
        ["Limits", "DailyOutflowCopperPerAccount", 0, 100_000_000],
        ["Limits", "DailyOutflowCopperServer", 0, 1_000_000_000],
    ];

    private static void Set(PlayerbotSocialTradingOptions options, string group, string key, int value)
    {
        object target = group.Length == 0 ? options : options.GetType().GetProperty(group)!.GetValue(options)!;
        target.GetType().GetProperty(key)!.SetValue(target, value);
    }

    [Fact]
    public void Defaults_AreTheDesignsAndOnByDefault_AndValidate()
    {
        var social = new PlayerbotSocialOptions();
        PlayerbotSocialTradingOptions t = social.Trading;
        Assert.NotNull(t);
        social.Validate();
        Assert.True(t.Enabled && t.AnswerTrades && t.AcceptGifts && t.Deals.Enabled && t.Gifts.Enabled);
        Assert.Equal((45, 30, 14, 350, 100, 10), (t.TradeTimeoutSeconds, t.ScamMemoryDays, t.NinjaMemoryDays, t.LowTrustPermille, t.GiftMinValueCopper, t.MaxGiftsReceivedPerHour));
        Assert.Equal((5, 10, 500_000, 20, 25, 24, 3),
            (t.Deals.DealMinutes, t.Deals.PairCooldownMinutes, t.Deals.MaxDealCopper, t.Deals.FriendDiscountPercent, t.Deals.DislikeMarkupPercent,
                t.Deals.PriceBookHours, t.Deals.PriceBookMinSellers));
        Assert.Equal((15, 5000), (t.Gifts.GiftClaimSeconds, t.Gifts.MaxGiftValueCopper));
        // Decision T5 (proposed): 200 g a day for the server, 2 g per bot and per account.
        Assert.Equal((6, 10, 16, 20_000, 20_000, 2_000_000),
            (t.Limits.MaxTradesPerBotPerHour, t.Limits.MaxTradesPerAccountPerHour, t.Limits.MaxConcurrentTrades,
                t.Limits.DailyOutflowCopperPerBot, t.Limits.DailyOutflowCopperPerAccount, t.Limits.DailyOutflowCopperServer));
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void EachKey_AcceptsItsBoundaries_AndRefusesOneOutside(string group, string key, int min, int max)
    {
        foreach (int ok in new[] { min, max })
        {
            var social = new PlayerbotSocialOptions();
            Set(social.Trading, group, key, ok);
            social.Validate();
        }

        foreach (int bad in new[] { min - 1, max + 1 })
        {
            var social = new PlayerbotSocialOptions();
            Set(social.Trading, group, key, bad);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(social.Validate);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASectionLeftMissing_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialOptions { Trading = null! }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialOptions { Trading = { Deals = null! } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialOptions { Trading = { Gifts = null! } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialOptions { Trading = { Limits = null! } }.Validate());
    }

    // The memory deltas have no range in the design table; they follow their siblings (AffinityPerTrade 0..30, AffinityPerStrike -100..0, TrustPerGroupSuccess -500..500).
    public static IEnumerable<object[]> MemoryRanges() =>
    [
        ["AffinityPerFairDeal", 0, 30],
        ["AffinityPerGiftReceived", 0, 30],
        ["AffinityPerTradeScam", -100, 0],
        ["AffinityPerDealFlaked", -100, 0],
        ["TrustPerFairDeal", -500, 500],
        ["TrustPerTradeScam", -500, 500],
    ];

    [Theory]
    [MemberData(nameof(MemoryRanges))]
    public void TheMemoryKeys_AcceptTheirSiblingsBoundaries_AndRefuseOneOutside(string key, int min, int max)
    {
        var property = typeof(PlayerbotSocialMemoryOptions).GetProperty(key)!;
        foreach (int ok in new[] { min, max })
        {
            var options = new PlayerbotSocialMemoryOptions();
            property.SetValue(options, ok);
            options.Validate();
        }

        foreach (int bad in new[] { min - 1, max + 1 })
        {
            var options = new PlayerbotSocialMemoryOptions();
            property.SetValue(options, bad);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        }
    }
}
