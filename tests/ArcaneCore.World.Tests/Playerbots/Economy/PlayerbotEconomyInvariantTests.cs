using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>
/// The structural invariant of the economy bot's prices: for every item and every in-range option value, every bot buy per unit is at most
/// F - 1 and every bot ask per unit (buyout and start bid) is at least F, so a player who buys from a bot and sells to a bot loses money
/// (the deposit is returned on a sale). F is <see cref="PlayerbotAuctionPricing.FloorPerUnit"/>, the buy cap is
/// <see cref="PlayerbotAuctionPricing.BuyCapPerUnit"/>.
/// </summary>
public sealed class PlayerbotEconomyInvariantTests
{
    private const ulong Self = 0x1000;
    private const ulong Other = 0x2000;
    private const uint Entry = 9000;

    /// <summary>Houses as (id, deposit %, cut %): the three faction houses of the repo, one without deposit or cut, one with an 80 % cut.</summary>
    private static readonly AuctionHouseEntry[] Houses =
    [
        new(AuctionHouseRules.AllianceHouse, 5, 5),
        new(AuctionHouseRules.HordeHouse, 5, 5),
        new(AuctionHouseRules.NeutralHouse, 25, 15),
        new(AuctionHouseRules.AllianceHouse, 0, 0),
        new(AuctionHouseRules.AllianceHouse, 5, 80),
    ];

    private static readonly float[] CutRates = [0f, 0.5f, 1f, 2f];

    private static readonly ItemClass[] Classes = [ItemClass.TradeGoods, ItemClass.Weapon, ItemClass.Armor, ItemClass.Consumable, ItemClass.Reagent];

    /// <summary>How much of the property each run really exercised: a run that checked nothing must not pass.</summary>
    private sealed class Coverage
    {
        public int Unsellable { get; set; }
        public int Listed { get; set; }
        public int NotListable { get; set; }
        public int BuyRowsChecked { get; set; }
        public int Loops { get; set; }
    }

    private static void Require(bool ok, Func<string> describe)
    {
        if (!ok) Assert.Fail(describe());
    }

    private static uint RandomSellPrice(Random random)
    {
        int roll = random.Next(100);
        if (roll < 5) return 0;
        if (roll == 5) return uint.MaxValue;
        if (roll < 30) return (uint)random.Next(1, 100);
        if (roll < 65) return (uint)random.Next(100, 10_000);
        return (uint)random.Next(10_000, 1_000_001);
    }

    private static uint RandomBuyPrice(Random random, uint sell)
        => random.Next(2) == 0
            ? (uint)Math.Min((ulong)sell * (ulong)random.Next(0, 7), uint.MaxValue)
            : (uint)random.NextInt64(0, uint.MaxValue);

    private static PlayerbotAuctionRow CompetitorRow(Random random, uint id, uint floor)
    {
        long highest = floor == 0 ? 1_000_000 : 4L * floor;
        uint perUnit = (uint)Math.Min(1 + random.NextInt64(highest), uint.MaxValue);
        uint count = (uint)random.Next(1, 21);
        uint buyout = (uint)Math.Min((ulong)perUnit * count, uint.MaxValue);
        ulong owner = random.Next(100) < 30 ? Self : Other;
        if (random.Next(100) < 20) buyout = 0;   // bid only
        return new PlayerbotAuctionRow(id, Entry, 0, count, owner, 1, 1, buyout, 3_600_000, 0, 0);
    }

    private static Coverage RunProperty(int seed, int iterations)
    {
        var random = new Random(seed);
        var coverage = new Coverage();
        for (int i = 0; i < iterations; i++)
        {
            uint sell = RandomSellPrice(random);
            ItemClass itemClass = Classes[random.Next(Classes.Length)];
            uint count = itemClass is ItemClass.Weapon or ItemClass.Armor ? 1u : (uint)random.Next(1, 21);
            var template = new ItemTemplate
            {
                Entry = Entry, Name = "Invariant item", Class = (uint)itemClass, Quality = (uint)random.Next(0, 7), SellPrice = sell,
                BuyPrice = RandomBuyPrice(random, sell), ItemLevel = (uint)random.Next(1, 101), RequiredLevel = (uint)random.Next(0, 61),
            };
            int markup = random.Next(100, 100_001);
            int buyBelow = random.Next(1, 100);
            AuctionHouseEntry house = Houses[random.Next(Houses.Length)];
            float cutRate = CutRates[random.Next(CutRates.Length)];
            float cutPercent = house.CutPercent * cutRate;

            uint floor = PlayerbotAuctionPricing.FloorPerUnit(template, cutPercent, markup);
            var rows = new List<PlayerbotAuctionRow>();
            int competitors = random.Next(0, 4);
            for (int row = 0; row < competitors; row++) rows.Add(CompetitorRow(random, (uint)(row + 1), floor));

            string Context() => $"seed {seed} iteration {i}: sell {sell} buy {template.BuyPrice} quality {template.Quality} level {template.ItemLevel}/{template.RequiredLevel} "
                + $"class {itemClass} count {count} markup {markup} buyBelow {buyBelow} house {house} cutRate {cutRate} rows [{string.Join("; ", rows)}]";

            uint cap = PlayerbotAuctionPricing.BuyCapPerUnit(template, cutPercent, markup, buyBelow);
            uint ask = PlayerbotAuctionPricing.ListingPrice(template, count, rows, Self, house, cutRate, markup);
            uint bid = ask == 0 ? 0 : PlayerbotAuctionPricing.StartBid(ask, floor, count);

            if (sell == 0)
            {
                coverage.Unsellable++;
                Require(floor == 0 && cap == 0 && ask == 0 && bid == 0, () => $"{Context()}: floor {floor} cap {cap} ask {ask} bid {bid} must all be 0 without a sell price");
                CheckBuyRows(template, 0, count, cutPercent, markup, buyBelow, coverage, Context);
                continue;
            }

            Require(floor >= PlayerbotAuctionPricing.MinimumReference, () => $"{Context()}: floor {floor} is below the minimum reference");
            Require(cap <= floor - 1, () => $"{Context()}: cap {cap} is not below floor {floor}");
            if (ask == 0)
            {
                coverage.NotListable++;
                Require((ulong)floor * count > AuctionHouseRules.MaxPrice || (ulong)floor * 2 * count > AuctionHouseRules.MaxPrice,
                    () => $"{Context()}: refused to list although the stack of floor {floor} fits the price limit");
            }
            else
            {
                coverage.Listed++;
                ulong floorOfStack = (ulong)floor * count;
                Require(ask >= floorOfStack, () => $"{Context()}: ask {ask} is below the floor of the stack {floorOfStack}");
                Require(bid >= floorOfStack, () => $"{Context()}: start bid {bid} is below the floor of the stack {floorOfStack}");
                Require(bid <= ask, () => $"{Context()}: start bid {bid} is above the buyout {ask}");
                Require(ask <= 2 * floorOfStack, () => $"{Context()}: ask {ask} is above twice the floor of the stack {floorOfStack}");
                Require((ulong)cap * count < bid && (ulong)cap * count < ask, () => $"{Context()}: a bot buys the stack at {(ulong)cap * count}, no less than it asks (ask {ask}, bid {bid})");

                // The loop: a player lists the stack at the buy cap, a bot buys it, the player buys from a bot at the lowest ask (the start
                // bid can never be lower than the floor): the net of the sale (the deposit comes back with the proceeds) must stay under it.
                ulong total = (ulong)cap * count;
                uint deposit = PlayerbotAuctionPricing.Deposit(house, template, count);
                uint cut = AuctionHouseRules.Cut(house, (uint)total, cutRate);
                long net = (long)AuctionHouseRules.Proceeds((uint)total, deposit, cut) - deposit;
                Require(net < Math.Min(ask, bid), () => $"{Context()}: the loop nets {net}, ask {ask}, bid {bid}, cap {cap}");
                coverage.Loops++;
            }

            CheckBuyRows(template, cap, count, cutPercent, markup, buyBelow, coverage, Context);
        }

        return coverage;
    }

    /// <summary>
    /// A row at the cap per unit is bought by the reagent and the upgrade rule when the cap is above 0; a row one copper over it is bought by
    /// neither; with a cap of 0 even a one-copper row is not.
    /// </summary>
    private static void CheckBuyRows(ItemTemplate template, uint cap, uint count, float cutPercent, int markup, int buyBelow, Coverage coverage, Func<string> context)
    {
        Func<ItemTemplate, uint> unitCap = t => PlayerbotAuctionPricing.BuyCapPerUnit(t, cutPercent, markup, buyBelow);
        ulong atCap = (ulong)cap * count;
        PlayerbotAuctionRow Row(ulong buyout) => new(1, Entry, 0, count, Other, 1, 1, (uint)buyout, 3_600_000, 0, 0);

        PlayerbotReagentChoice? Reagent(ulong buyout) => PlayerbotEconomyPlanner.ChooseReagent([Row(buyout)], Self,
            new Dictionary<uint, uint> { [Entry] = count }, uint.MaxValue, entry => entry == Entry ? template : null, unitCap);
        PlayerbotUpgradeChoice? Upgrade(ulong buyout) => PlayerbotEconomyPlanner.ChooseUpgrade([Row(buyout)], Self, uint.MaxValue, uint.MaxValue, 0, 1f,
            entry => entry == Entry ? template : null, _ => 1f, unitCap);

        if (cap == 0)
        {
            Require(Reagent(1) is null && Upgrade(1) is null, () => $"{context()}: a one-copper row was bought although the cap is 0");
            coverage.BuyRowsChecked++;
            return;
        }

        // The rows are uint buyouts: a stack whose cap total does not fit 32 bits cannot be represented.
        if (atCap + 1 > uint.MaxValue) return;
        Require(Reagent(atCap) is not null, () => $"{context()}: the reagent rule refused a row at the cap {cap} per unit ({atCap} for {count})");
        Require(Upgrade(atCap) is not null, () => $"{context()}: the upgrade rule refused a row at the cap {cap} per unit ({atCap} for {count})");
        Require(Reagent(atCap + 1) is null, () => $"{context()}: the reagent rule bought a row one copper over the cap {cap} per unit");
        Require(Upgrade(atCap + 1) is null, () => $"{context()}: the upgrade rule bought a row one copper over the cap {cap} per unit");
        coverage.BuyRowsChecked++;
    }

    private static void AssertExercised(Coverage coverage, int iterations)
    {
        Assert.True(coverage.Unsellable > iterations / 50, $"only {coverage.Unsellable} items without a sell price");
        Assert.True(coverage.Listed > iterations / 5, $"only {coverage.Listed} listings were priced");
        Assert.True(coverage.NotListable > 0, "no stack was ever too dear to list");
        Assert.True(coverage.Loops == coverage.Listed, $"{coverage.Loops} loops for {coverage.Listed} listings");
        Assert.True(coverage.BuyRowsChecked > iterations / 5, $"only {coverage.BuyRowsChecked} buy rows were checked");
    }

    [Fact]
    public void NoPlayerLoopThroughBots_EverProfits()
    {
        const int iterations = 5000;
        AssertExercised(RunProperty(20261028, iterations), iterations);
    }

    [Fact]
    public void NoPlayerLoopThroughBots_EverProfits_SecondSeed()
    {
        const int iterations = 5000;
        AssertExercised(RunProperty(7, iterations), iterations);
    }

    [Fact]
    public void BuyCap_IsBelowTheFloor_ForEveryBuyBelowAskPercent_AndTheMarkupAndSellEdges()
    {
        foreach (uint sell in new uint[] { 1, 2, 13, 99, 100, 359, 9_999, 1_000_000, 100_000_000, uint.MaxValue })
        {
            foreach (int markup in new[] { 100, 101, 500, 99_999, 100_000 })
            {
                foreach (float cutPercent in new[] { 0f, 5f, 15f, 80f, 99.9f, 100f })
                {
                    var template = new ItemTemplate { Entry = Entry, Name = "Edge item", Class = (uint)ItemClass.TradeGoods, Quality = 1, SellPrice = sell };
                    uint floor = PlayerbotAuctionPricing.FloorPerUnit(template, cutPercent, markup);
                    Assert.InRange(floor, 100u, AuctionHouseRules.MaxPrice);
                    for (int buyBelow = 1; buyBelow <= 99; buyBelow++)
                    {
                        uint cap = PlayerbotAuctionPricing.BuyCapPerUnit(template, cutPercent, markup, buyBelow);
                        Assert.True(cap < floor, $"sell {sell} markup {markup} cut {cutPercent} buyBelow {buyBelow}: cap {cap} floor {floor}");
                        Assert.True(cap >= 1, $"sell {sell} markup {markup} cut {cutPercent} buyBelow {buyBelow}: cap {cap} must buy something");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The eight reference items at the default options (cut 5 %, no competitor), with the vendor sell price and quality of the classic-db rows and
    /// the item level where the floor depends on it (the three cloths: the reference price 20 * level^2 / 10 is their floor). The other levels
    /// are left at 0: their floor is the markup term, which only needs the sell price and the quality. F and the buy cap are the formula
    /// max(vendor floor, sell * markup / 100, reference) and floor(F * 50 / 100), computed from the real helpers below as well.
    /// </summary>
    [Theory]
    [InlineData(2589u, 1u, 13u, 5u, 100u, 50u)]                       // Linen Cloth: reference 100 (the minimum), 500 % of 13 = 65
    [InlineData(4306u, 1u, 150u, 30u, 1800u, 900u)]                   // Silk Cloth: reference 20 * 30^2 / 10 = 1800, 500 % of 150 = 750
    [InlineData(4338u, 1u, 250u, 40u, 3200u, 1600u)]                  // Mageweave Cloth: reference 3200, 500 % of 250 = 1250
    [InlineData(14047u, 1u, 400u, 50u, 5000u, 2500u)]                 // Runecloth: reference 5000, 500 % of 400 = 2000
    [InlineData(2583u, 2u, 359u, 0u, 1077u, 538u)]                    // Woolen Boots: 300 % of 359 = 1077, cap 538.5 rounded down
    [InlineData(6567u, 2u, 1036u, 0u, 3108u, 1554u)]                  // Shimmering Armor: 300 % of 1036 = 3108
    [InlineData(6622u, 3u, 55400u, 0u, 443_200u, 221_600u)]           // Sword of Zeal: 800 % of 55400 = 443200 (44 g 32 s)
    [InlineData(2825u, 4u, 14721u, 0u, 1_472_100u, 736_050u)]         // Bow of Searing Arrows: 10000 % of 14721 = 1472100 (147 g 21 s)
    public void ReferenceItems_HaveTheDocumentedBotFloorAndBuyCap(uint entry, uint quality, uint sell, uint itemLevel, uint expectedFloor, uint expectedCap)
    {
        var options = new PlayerbotEconomyOptions();
        var template = new ItemTemplate { Entry = entry, Name = "Reference item", Class = (uint)(quality >= 2 ? ItemClass.Armor : ItemClass.TradeGoods), Quality = quality, SellPrice = sell, ItemLevel = itemLevel };
        int markup = options.ListMarkupPercentFor(quality);
        var alliance = new AuctionHouseEntry(AuctionHouseRules.AllianceHouse, 5, 5);

        // The formula with the real building blocks.
        ulong formula = Math.Max(Math.Max(PlayerbotAuctionPricing.VendorFloorPerUnit(sell, 5f), (ulong)sell * (ulong)markup / 100), PlayerbotAuctionPricing.ReferencePerUnit(template));
        Assert.Equal(expectedFloor, (uint)formula);
        Assert.Equal(expectedCap, (uint)(formula * (ulong)options.BuyBelowAskPercent / 100));

        Assert.Equal(expectedFloor, PlayerbotAuctionPricing.FloorPerUnit(template, 5f, markup));
        Assert.Equal(expectedCap, PlayerbotAuctionPricing.BuyCapPerUnit(template, 5f, markup, options.BuyBelowAskPercent));
        Assert.Equal(expectedFloor, PlayerbotAuctionPricing.ListingPrice(template, 1, [], Self, alliance, 1f, markup));
        Assert.Equal(expectedFloor, PlayerbotAuctionPricing.StartBid(expectedFloor, expectedFloor, 1));
        Assert.True(expectedCap < expectedFloor);
    }
}
