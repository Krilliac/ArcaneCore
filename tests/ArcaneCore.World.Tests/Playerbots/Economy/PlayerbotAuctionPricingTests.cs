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

    /// <summary>Markup 100 % makes the markup term equal the vendor sell price, so the floor is the reference price or the vendor floor after the cut.</summary>
    private const int NoMarkup = 100;

    private static uint Price(ItemTemplate item, uint count, IReadOnlyList<PlayerbotAuctionRow> rows, AuctionHouseEntry? house = null, float cutRate = 1f,
        int markup = NoMarkup)
        => PlayerbotAuctionPricing.ListingPrice(item, count, rows, Self, house ?? Alliance, cutRate, markup);

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
        Assert.Equal(0u, Price(worthless, 1, [], markup: 100_000));
        Assert.Equal(0u, PlayerbotAuctionPricing.FloorPerUnit(worthless, 5f, 100_000));
        Assert.Equal(0u, PlayerbotAuctionPricing.BuyCapPerUnit(worthless, 5f, 100_000, 99));
    }

    [Fact]
    public void ListingPrice_UndercutsTheLowestCompetingBuyoutPerUnit_ByOnePercentAtLeastOneCopper()
    {
        ItemTemplate item = Item(sell: 100);   // reference 141 = the floor (markup 100 % gives 100, vendor floor 107), cap 282
        Assert.Equal(198u, Price(item, 1, [Row(5000, 200)]));
        Assert.Equal(149u, Price(item, 1, [Row(5000, 150)]));   // 1% of 150 is 1
        // Per unit: a competitor's stack of 2 for 400 asks 200 each; three units go for 3 * 198.
        Assert.Equal(594u, Price(item, 3, [Row(5000, 400, count: 2)]));
        // The cheapest of several counts.
        Assert.Equal(198u, Price(item, 1, [Row(5000, 260, id: 1), Row(5000, 200, id: 2), Row(5000, 230, id: 3)]));
        // At least one copper under even when 1% rounds to nothing.
        ItemTemplate cheap = Item(sell: 10);   // floor 141: the reference of a q2 item (100 * sqrt 2), above its vendor floor 12
        Assert.Equal(149u, Price(cheap, 1, [Row(5000, 150)]));
        // A competitor below the floor is not followed under it: the ask is raised to the floor.
        Assert.Equal(141u, Price(cheap, 1, [Row(5000, 50)]));
        // No competitor: the reference price.
        Assert.Equal(141u, Price(item, 1, []));
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
        Assert.Equal(248u, Price(item, 1, rows));
        Assert.Equal(250u, PlayerbotAuctionPricing.LowestCompetitorPerUnit(rows, 5000, Self));
        Assert.Equal(0u, PlayerbotAuctionPricing.LowestCompetitorPerUnit([rows[0], rows[1], rows[2]], 5000, Self));
    }

    [Fact]
    public void ListingPrice_NeverFallsBelowTheVendorFloorAfterTheHouseCut()
    {
        ItemTemplate item = Item(sell: 100);   // reference 141
        // ceil(100 * 100 / 95) + 1 = 107: one copper more than a vendor pays once the 5% cut is taken.
        Assert.Equal(107u, PlayerbotAuctionPricing.VendorFloorPerUnit(100, 5));
        // Under the 5% cut the reference (141) is the larger floor, so a competitor at 110 is not followed under it.
        Assert.Equal(141u, Price(item, 1, [Row(5000, 110)]));
        Assert.Equal(141u, Price(item, 1, [Row(5000, 1)]));
        // Under a 50% cut the vendor floor is ceil(100 * 100 / 50) + 1 = 201, above the reference: that is the floor and the ask.
        var halfCut = new AuctionHouseEntry(AuctionHouseRules.AllianceHouse, 5, 50);
        Assert.Equal(201u, PlayerbotAuctionPricing.VendorFloorPerUnit(100, 50));
        Assert.Equal(201u, PlayerbotAuctionPricing.FloorPerUnit(item, 50f, NoMarkup));
        Assert.Equal(201u, Price(item, 1, [Row(5000, 1)], halfCut));
        Assert.Equal(201u, Price(item, 1, [], halfCut));
        Assert.Equal(402u, Price(item, 1, [Row(5000, 5000)], halfCut));   // twice the floor, not twice the reference
        uint buyout = Price(item, 1, [Row(5000, 1)], halfCut);
        Assert.True(buyout - AuctionHouseRules.Cut(halfCut, buyout) > 100, "the sale must leave more than the vendor pays");
        // The cut rate scales the cut: 5 % * 10 = 50 % gives the same floor.
        Assert.Equal(201u, Price(item, 1, [], Alliance, cutRate: 10f));
        // A cut of 80 % no longer hides the item: the floor is the vendor floor ceil(1000 * 100 / 20) + 1 = 5001.
        var eighty = new AuctionHouseEntry(2, 5, 80);
        Assert.Equal(5001u, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: 1000), 80f, NoMarkup));
        Assert.Equal(5001u, Price(Item(sell: 1000), 1, [], eighty));
        // A cut that takes everything leaves the vendor floor at uint.MaxValue; the floor is capped at the client price limit, and a
        // single unit asking exactly that limit is the only listing that fits.
        Assert.Equal(uint.MaxValue, PlayerbotAuctionPricing.VendorFloorPerUnit(100, 100));
        var whole = new AuctionHouseEntry(2, 5, 100);
        Assert.Equal(AuctionHouseRules.MaxPrice, PlayerbotAuctionPricing.FloorPerUnit(item, 100f, NoMarkup));
        Assert.Equal(AuctionHouseRules.MaxPrice, Price(item, 1, [], whole));
        Assert.Equal(0u, Price(item, 2, [], whole));
    }

    [Fact]
    public void FloorPerUnit_IsTheLargestOfVendorFloor_MarkupAndReference_CappedAtThePriceLimit()
    {
        ItemTemplate item = Item(sell: 100);   // reference 141, vendor floor 107 at 5 %
        Assert.Equal(141u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 100));    // markup term 100
        Assert.Equal(141u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 141));    // markup term 141: tie with the reference
        Assert.Equal(142u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 142));    // one percent past it
        Assert.Equal(250u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 250));
        Assert.Equal(500u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 500));
        Assert.Equal(1_000_000u, PlayerbotAuctionPricing.FloorPerUnit(item, 5f, 1_000_000));
        // The markup term is an integer percent of the sell price: 7 * 150 / 100 = 10, below the reference 141 of a q2 item.
        Assert.Equal(141u, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: 7), 5f, 150));
        // Markup in unsigned 64-bit math: the product overflows 32 bits and ends at the price limit.
        Assert.Equal(AuctionHouseRules.MaxPrice, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: uint.MaxValue), 5f, 100_000));
        Assert.Equal(AuctionHouseRules.MaxPrice, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: 100_000_000), 5f, 100_000));
        // The cut raises the vendor floor above a modest markup.
        Assert.Equal(201u, PlayerbotAuctionPricing.FloorPerUnit(item, 50f, 100));
        // A sell price of 0: no floor, whatever the markup or the cut.
        Assert.Equal(0u, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: 0), 5f, 100_000));
        Assert.Equal(0u, PlayerbotAuctionPricing.FloorPerUnit(Item(sell: 0), 99f, 100));
        Assert.Throws<ArgumentNullException>(() => PlayerbotAuctionPricing.FloorPerUnit(null!, 5f, 100));
    }

    [Fact]
    public void BuyCapPerUnit_IsTheFloorTimesBuyBelowAsk_RoundedDown_AndAlwaysBelowTheFloor()
    {
        ItemTemplate item = Item(sell: 100);   // floor 141 at markup 100
        Assert.Equal(70u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 50));    // 141 * 50 / 100 = 70.5
        Assert.Equal(1u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 1));      // 1.41
        Assert.Equal(139u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 99));   // 139.59
        // The percent is clamped to 1..99: nothing past the options' own bounds reaches the floor.
        Assert.Equal(1u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 0));
        Assert.Equal(1u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, -40));
        Assert.Equal(1u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, int.MinValue));
        Assert.Equal(139u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 100));
        Assert.Equal(139u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, 5000));
        Assert.Equal(139u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 100, int.MaxValue));
        // The cap follows the markup: the same item with a 1000 % markup floors at 1000.
        Assert.Equal(500u, PlayerbotAuctionPricing.BuyCapPerUnit(item, 5f, 1000, 50));
        // No sell price: never bought.
        Assert.Equal(0u, PlayerbotAuctionPricing.BuyCapPerUnit(Item(sell: 0), 5f, 100_000, 99));
        // The largest floor (the 2,000,000,000 price limit): the cap stays below it.
        Assert.Equal(1_000_000_000u, PlayerbotAuctionPricing.BuyCapPerUnit(Item(sell: uint.MaxValue), 5f, 100_000, 50));
        Assert.Equal(1_980_000_000u, PlayerbotAuctionPricing.BuyCapPerUnit(Item(sell: uint.MaxValue), 5f, 100_000, 99));
        Assert.Throws<ArgumentNullException>(() => PlayerbotAuctionPricing.BuyCapPerUnit(null!, 5f, 100, 50));
    }

    [Fact]
    public void ListingPrice_AsksTheMarkupFloor_WithoutACompetitor_AndNeverUndercutsItsFloor()
    {
        ItemTemplate boots = Item(sell: 359);   // reference 507, markup 300 % = 1077
        Assert.Equal(1077u, Price(boots, 1, [], markup: 300));
        Assert.Equal(3231u, Price(boots, 3, [], markup: 300));
        Assert.Equal(1077u, Price(boots, 1, [Row(5000, 600)], markup: 300));   // a competitor below the floor is not followed under it
        Assert.Equal(1178u, Price(boots, 1, [Row(5000, 1189)], markup: 300)); // 1 % of 1189 is 11
        Assert.Equal(2154u, Price(boots, 1, [Row(5000, 9000)], markup: 300)); // twice the floor
    }

    [Fact]
    public void ListingPrice_CapsAtTwiceTheFloor_AndTheClientPriceLimit()
    {
        ItemTemplate item = Item(sell: 100);
        Assert.Equal(282u, Price(item, 1, [Row(5000, 5000)]));   // twice 141, not 4950
        Assert.Equal(1000u, Price(item, 1, [], markup: 1000));
        Assert.Equal(2000u, Price(item, 1, [Row(5000, 5000)], markup: 1000));   // twice the floor of 1000
        ItemTemplate gold = Item(sell: 10_000_000);
        uint reference = PlayerbotAuctionPricing.ReferencePerUnit(gold);
        Assert.Equal(14_142_135u, reference);
        Assert.Equal(reference, Price(gold, 1, []));
        // 255 units would pass the client limit at the floor itself: no listing, and never a total clamped under the floor.
        Assert.Equal(0u, Price(gold, 255, []));
        Assert.Equal(reference * 141u, Price(gold, 141, []));   // within the limit
        Assert.Equal(0u, Price(gold, 142, []));                 // 142 * 14_142_135 passes it

        // The floor fits but the undercut-clamped ask (up to twice the floor) does not: no listing either.
        ItemTemplate big = Item(sell: 100_000_000, quality: 0);   // reference 100_000_000, vendor floor 100_000_001 at a 0 % cut
        var noCut = new AuctionHouseEntry(AuctionHouseRules.AllianceHouse, 5, 0);
        Assert.Equal(100_000_001u, PlayerbotAuctionPricing.FloorPerUnit(big, 0f, NoMarkup));
        Assert.Equal(1_900_000_019u, Price(big, 19, [], noCut));
        Assert.Equal(0u, Price(big, 20, [], noCut));                                   // the floor times 20 passes the limit
        Assert.Equal(1_100_000_011u, Price(big, 11, [], noCut));
        Assert.Equal(0u, Price(big, 11, [Row(5000, 400_000_000)], noCut));             // the ask 2 * floor * 11 passes the limit
        Assert.Equal(1_000_000_010u, Price(big, 10, [], noCut));
        Assert.Equal(0u, Price(big, 0, []));
    }

    [Fact]
    public void ListingPrice_RefusesNullArguments_AndAnItemWithNoSellPrice()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerbotAuctionPricing.ListingPrice(null!, 1, [], Self, Alliance, 1f, 100));
        Assert.Throws<ArgumentNullException>(() => PlayerbotAuctionPricing.ListingPrice(Item(100), 1, [], Self, null!, 1f, 100));
        Assert.Equal(0u, Price(Item(sell: 0, buy: 40_000, itemLevel: 60), 1, [Row(5000, 300)], markup: 100_000));
    }

    [Fact]
    public void StartBid_IsEightyPercentOfBuyout_AtLeastOne_NeverAboveBuyout_AndNeverBelowTheFloor()
    {
        Assert.Equal(80u, PlayerbotAuctionPricing.StartBid(100, 0, 1));
        Assert.Equal(2u, PlayerbotAuctionPricing.StartBid(3, 0, 1));
        Assert.Equal(1u, PlayerbotAuctionPricing.StartBid(1, 0, 1));
        Assert.Equal(1u, PlayerbotAuctionPricing.StartBid(2, 0, 1));
        Assert.Equal(0u, PlayerbotAuctionPricing.StartBid(0, 0, 1));
        Assert.Equal(0u, PlayerbotAuctionPricing.StartBid(0, 500, 3));   // no buyout, no start bid, whatever the floor
        for (uint buyout = 1; buyout < 500; buyout++)
        {
            uint bid = PlayerbotAuctionPricing.StartBid(buyout, 0, 1);
            Assert.InRange(bid, 1u, buyout);
        }

        Assert.Equal(1_600_000_000u, PlayerbotAuctionPricing.StartBid(AuctionHouseRules.MaxPrice, 0, 1));

        // The floor lifts the bid over 80 %, up to the buyout.
        Assert.Equal(160u, PlayerbotAuctionPricing.StartBid(200, 100, 1));   // 80 % wins over a floor of 100
        Assert.Equal(190u, PlayerbotAuctionPricing.StartBid(200, 190, 1));
        Assert.Equal(200u, PlayerbotAuctionPricing.StartBid(200, 200, 1));   // the floor equals the buyout
        Assert.Equal(200u, PlayerbotAuctionPricing.StartBid(200, 250, 1));   // never above the buyout
        Assert.Equal(300u, PlayerbotAuctionPricing.StartBid(300, 100, 3));   // the floor of the stack: 100 * 3
        Assert.Equal(240u, PlayerbotAuctionPricing.StartBid(300, 70, 3));    // 210 < 240
        // A floor times count past 32 bits: an unsigned 64-bit product, never wrapped.
        Assert.Equal(AuctionHouseRules.MaxPrice, PlayerbotAuctionPricing.StartBid(AuctionHouseRules.MaxPrice, uint.MaxValue, 20));
        Assert.Equal(uint.MaxValue, PlayerbotAuctionPricing.StartBid(uint.MaxValue, uint.MaxValue, 255));
        Assert.Equal(3_435_973_836u, PlayerbotAuctionPricing.StartBid(uint.MaxValue, 0, 1));   // 80 % of 4_294_967_295, rounded down

        // For a real listing the bid is never below the floor of the stack: a bot's ask and bid both stay above every bot buy.
        ItemTemplate item = Item(sell: 100);
        uint floor = PlayerbotAuctionPricing.FloorPerUnit(item, 5f, NoMarkup);
        foreach (uint count in new uint[] { 1, 2, 5, 20 })
        {
            uint buyoutOfStack = Price(item, count, []);
            uint bid = PlayerbotAuctionPricing.StartBid(buyoutOfStack, floor, count);
            Assert.True(bid >= floor * count && bid <= buyoutOfStack, $"count {count}");
        }
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
        uint buyout = Price(item, 1, []);
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
