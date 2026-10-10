using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>The economy bot's prices (mangoszero ahbot PricingStrategy::GetDefaultBuyPrice, vmangos deposit and cut rules).</summary>
public sealed class PlayerbotAuctionPricingTests
{
    private static readonly AuctionHouseEntry Alliance = new(AuctionHouseRules.AllianceHouse, 5, 5);
    private static readonly AuctionHouseEntry Neutral = new(AuctionHouseRules.NeutralHouse, 25, 15);
    private const ulong Self = 0x1000;
    private const ulong Other = 0x2000;

    private static ItemTemplate Item(uint sell, uint quality = 2, uint buy = 0, uint itemLevel = 0, uint requiredLevel = 0, uint entry = 5000)
        => new() { Entry = entry, Name = "Test item", Class = 4, Quality = quality, SellPrice = sell, BuyPrice = buy, ItemLevel = itemLevel, RequiredLevel = requiredLevel };

    private static PlayerbotAuctionRow Row(uint entry, uint buyout, uint count = 1, ulong owner = Other, uint id = 1)
        => new(id, entry, 0, count, owner, 1, 1, buyout, 3_600_000, 0, 0);

    [Fact]
    public void ReferencePrice_FollowsMangoszeroDefaultBuyPrice_SellBuyLevelAndQuality()
    {
        // price = max(sell, buy / 4); raised to 20 * level^2 / 10 (level = max(item level, required level)) and to 100; times sqrt(quality) above poor.
        Assert.Equal(100u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 10, quality: 1)));        // floor 100, sqrt(1)
        Assert.Equal(100u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 10, quality: 0)));        // poor: no multiplier
        Assert.Equal(141u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 100, quality: 2)));       // 100 * sqrt(2)
        Assert.Equal(300u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 150, quality: 4)));       // 150 * sqrt(4)
        Assert.Equal(565u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 100, quality: 2, buy: 1600)));          // buy / 4 = 400 * sqrt(2)
        Assert.Equal(1131u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 100, quality: 2, itemLevel: 20)));     // 20 * 400 / 10 = 800 * sqrt(2)
        Assert.Equal(1131u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 100, quality: 2, requiredLevel: 20))); // the larger of the two levels
        Assert.Equal(1131u, PlayerbotAuctionPricing.ReferencePerUnit(Item(sell: 100, quality: 2, itemLevel: 5, requiredLevel: 20)));
    }

    [Fact]
    public void ReferencePrice_IsZero_ForAnItemWithNoSellPrice_SoItIsNeverListed()
    {
        ItemTemplate worthless = Item(sell: 0, quality: 3, buy: 4000, itemLevel: 30);
        Assert.Equal(0u, PlayerbotAuctionPricing.ReferencePerUnit(worthless));
        Assert.Equal(0u, PlayerbotAuctionPricing.ListingPrice(worthless, 1, [], Self, Alliance));
    }

    [Fact]
    public void ListingPrice_UndercutsTheLowestCompetingBuyoutPerUnit_ByOnePercentAtLeastOneCopper()
    {
        ItemTemplate item = Item(sell: 100);   // reference 141, vendor floor 107, cap 282
        Assert.Equal(198u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 200)], Self, Alliance));
        Assert.Equal(149u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 150)], Self, Alliance));   // 1% of 150 is 1
        // Per unit: a competitor's stack of 2 for 400 asks 200 each; three units go for 3 * 198.
        Assert.Equal(594u, PlayerbotAuctionPricing.ListingPrice(item, 3, [Row(5000, 400, count: 2)], Self, Alliance));
        // The cheapest of several counts.
        Assert.Equal(198u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 260, id: 1), Row(5000, 200, id: 2), Row(5000, 230, id: 3)], Self, Alliance));
        // At least one copper under even when 1% rounds to nothing (a low floor lets it show).
        ItemTemplate cheap = Item(sell: 10);   // floor 12
        Assert.Equal(49u, PlayerbotAuctionPricing.ListingPrice(cheap, 1, [Row(5000, 50)], Self, Alliance));
        // No competitor: the reference price.
        Assert.Equal(141u, PlayerbotAuctionPricing.ListingPrice(item, 1, [], Self, Alliance));
    }

    [Fact]
    public void ListingPrice_IgnoresTheBotsOwnAndBidOnlyListings()
    {
        ItemTemplate item = Item(sell: 100);
        PlayerbotAuctionRow[] rows =
        [
            Row(5000, 120, owner: Self, id: 1),          // its own
            Row(5000, 0, id: 2),                          // bid only
            Row(5001, 110, id: 3),                        // another item whose name contains this one's
            Row(5000, 250, id: 4),                        // the only real competitor
        ];
        Assert.Equal(248u, PlayerbotAuctionPricing.ListingPrice(item, 1, rows, Self, Alliance));
        Assert.Equal(250u, PlayerbotAuctionPricing.LowestCompetitorPerUnit(rows, 5000, Self));
        Assert.Equal(0u, PlayerbotAuctionPricing.LowestCompetitorPerUnit([rows[0], rows[1], rows[2]], 5000, Self));
    }

    [Fact]
    public void ListingPrice_NeverFallsBelowTheVendorFloorAfterTheHouseCut()
    {
        ItemTemplate item = Item(sell: 100);
        // ceil(100 * 100 / 95) + 1 = 107: one copper more than a vendor pays once the 5% cut is taken.
        Assert.Equal(107u, PlayerbotAuctionPricing.VendorFloorPerUnit(100, 5));
        Assert.Equal(109u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 110)], Self, Alliance));
        Assert.Equal(107u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 105)], Self, Alliance));   // 104 is clamped up
        Assert.Equal(107u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 1)], Self, Alliance));
        uint buyout = PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 1)], Self, Alliance);
        Assert.True(buyout - AuctionHouseRules.Cut(Alliance, buyout) > 100, "the sale must leave more than the vendor pays");
        // A cut that takes everything leaves no price at all, and a floor above twice the reference is never listed.
        Assert.Equal(uint.MaxValue, PlayerbotAuctionPricing.VendorFloorPerUnit(100, 100));
        Assert.Equal(0u, PlayerbotAuctionPricing.ListingPrice(Item(sell: 1000), 1, [], Self, new AuctionHouseEntry(2, 5, 80)));
    }

    [Fact]
    public void ListingPrice_CapsAtTwiceReference_AndTheClientPriceLimit()
    {
        ItemTemplate item = Item(sell: 100);
        Assert.Equal(282u, PlayerbotAuctionPricing.ListingPrice(item, 1, [Row(5000, 5000)], Self, Alliance));   // twice 141, not 4950
        ItemTemplate gold = Item(sell: 10_000_000);
        uint reference = PlayerbotAuctionPricing.ReferencePerUnit(gold);
        Assert.Equal(14_142_135u, reference);
        Assert.Equal(reference, PlayerbotAuctionPricing.ListingPrice(gold, 1, [], Self, Alliance));
        Assert.Equal(AuctionHouseRules.MaxPrice, PlayerbotAuctionPricing.ListingPrice(gold, 255, [], Self, Alliance));
    }

    [Fact]
    public void StartBid_IsEightyPercentOfBuyout_AtLeastOne_AndNeverAboveBuyout()
    {
        Assert.Equal(80u, PlayerbotAuctionPricing.StartBid(100));
        Assert.Equal(2u, PlayerbotAuctionPricing.StartBid(3));
        Assert.Equal(1u, PlayerbotAuctionPricing.StartBid(1));
        Assert.Equal(1u, PlayerbotAuctionPricing.StartBid(2));
        Assert.Equal(0u, PlayerbotAuctionPricing.StartBid(0));
        for (uint buyout = 1; buyout < 500; buyout++)
        {
            uint bid = PlayerbotAuctionPricing.StartBid(buyout);
            Assert.InRange(bid, 1u, buyout);
        }

        Assert.Equal(1_600_000_000u, PlayerbotAuctionPricing.StartBid(AuctionHouseRules.MaxPrice));
    }

    [Fact]
    public void Deposit_EqualsAuctionHouseRulesDeposit_ForTheEightHourDuration_FactionAndNeutralHouses()
    {
        ItemTemplate item = Item(sell: 100);
        Assert.Equal(480u, PlayerbotAuctionPricing.DurationMinutes);
        // sell * count * (480 min / 120 min = 4 units) * percent / 100.
        Assert.Equal(AuctionHouseRules.Deposit(Alliance, 100, 2, 480), PlayerbotAuctionPricing.Deposit(Alliance, item, 2));
        Assert.Equal(40u, PlayerbotAuctionPricing.Deposit(Alliance, item, 2));
        Assert.Equal(200u, PlayerbotAuctionPricing.Deposit(Neutral, item, 2));
        var horde = new AuctionHouseEntry(AuctionHouseRules.HordeHouse, 5, 5);
        Assert.Equal(AuctionHouseRules.Deposit(horde, 100, 2, 480), PlayerbotAuctionPricing.Deposit(horde, item, 2));
        Assert.Equal(75u, PlayerbotAuctionPricing.Deposit(Alliance, item, 2, depositMinimum: 75));
        Assert.Equal(80u, PlayerbotAuctionPricing.Deposit(Alliance, item, 2, depositRate: 2f));
    }

    [Fact]
    public void Listing_IsSkipped_WhenOneLostDepositWouldExceedTheMarginOverVendoring()
    {
        ItemTemplate item = Item(sell: 1000);   // reference 1414
        uint buyout = PlayerbotAuctionPricing.ListingPrice(item, 1, [], Self, Alliance);
        Assert.Equal(1414u, buyout);
        uint deposit = PlayerbotAuctionPricing.Deposit(Alliance, item, 1);
        Assert.Equal(200u, deposit);
        // Alliance: cut 70, margin 1414 - 70 - 1000 = 344 > the 200 deposit: it pays.
        Assert.True(PlayerbotAuctionPricing.IsWorthListing(item, 1, buyout, deposit, Alliance, 1f, remainingBudget: 1000));
        // The neutral house takes 25% to deposit and 15% as cut: margin 1414 - 212 - 1000 = 202 < the 1000 deposit.
        uint neutralDeposit = PlayerbotAuctionPricing.Deposit(Neutral, item, 1);
        Assert.Equal(1000u, neutralDeposit);
        Assert.False(PlayerbotAuctionPricing.IsWorthListing(item, 1, buyout, neutralDeposit, Neutral, 1f, remainingBudget: 100_000));
        // Within the margin but not within the budget left.
        Assert.False(PlayerbotAuctionPricing.IsWorthListing(item, 1, buyout, deposit, Alliance, 1f, remainingBudget: 199));
        Assert.True(PlayerbotAuctionPricing.IsWorthListing(item, 1, buyout, deposit, Alliance, 1f, remainingBudget: 200));
        // A buyout that does not clear the vendor price is never worth it.
        Assert.False(PlayerbotAuctionPricing.IsWorthListing(item, 1, 1000, 0, Alliance, 1f, remainingBudget: 1000));
        Assert.False(PlayerbotAuctionPricing.IsWorthListing(item, 1, 0, 0, Alliance, 1f, remainingBudget: 1000));
    }
}
