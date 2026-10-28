using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using Xunit;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>The economy bot's decisions: what it lists, how many listings a visit may add, what it may spend, which upgrade it buys.</summary>
public sealed class PlayerbotEconomyPlannerTests
{
    private static readonly AuctionHouseEntry Alliance = new(AuctionHouseRules.AllianceHouse, 5, 5);
    private const ulong Self = 0x1000;
    private const ulong Other = 0x2000;

    private static ItemTemplate Template(uint entry = 6000, ItemClass itemClass = ItemClass.Armor, uint quality = 2, uint sell = 100, uint bonding = 0,
        uint buy = 0)
        => new() { Entry = entry, Name = $"item-{entry}", Class = (uint)itemClass, Quality = quality, SellPrice = sell, BuyPrice = buy, Bonding = bonding };

    private static PlayerbotBagItem Bag(ItemTemplate template, uint guid = 1, uint count = 1, bool transferable = true, bool reserved = false)
        => new(ObjectGuid.Item(guid), template, count, transferable, reserved);

    private static PlayerbotAuctionRow Row(uint id, uint entry, uint buyout, ulong owner = Other, uint count = 1)
        => new(id, entry, 0, count, owner, 1, 1, buyout, 3_600_000, 0, 0);

    /// <summary>A random source whose roll is fixed: <c>Next(100)</c> returns the given value.</summary>
    private sealed class FixedRandom(int roll) : Random
    {
        public override int Next(int maxValue) => roll;
    }

    // --- IsListable ------------------------------------------------------------------------------------------------

    [Fact]
    public void IsListable_ListsAnUnboundGreenBlueOrEpicWeaponOrArmor_ThatIsNoUpgrade()
    {
        foreach (ItemClass itemClass in new[] { ItemClass.Weapon, ItemClass.Armor })
        {
            foreach (uint quality in new uint[] { 2, 3, 4 })
            {
                PlayerbotBagItem item = Bag(Template(itemClass: itemClass, quality: quality));
                Assert.True(PlayerbotEconomyPlanner.IsListable(item, null), $"{itemClass} q{quality}: the bot cannot use it");
                Assert.True(PlayerbotEconomyPlanner.IsListable(item, 0f), $"{itemClass} q{quality}: no gain");
            }
        }

        // Bind on equip is still listable, bind on pickup / use / quest are not.
        Assert.True(PlayerbotEconomyPlanner.IsListable(Bag(Template(bonding: 2)), null));
        foreach (uint bonding in new uint[] { 1, 3, 4 })
            Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(bonding: bonding)), null), $"bonding {bonding}");
    }

    [Fact]
    public void IsListable_KeepsAnUpgrade_ButTheMinimumGainBoundaryIsInclusive()
    {
        PlayerbotBagItem item = Bag(Template());
        Assert.True(PlayerbotEconomyPlanner.IsListable(item, PlayerbotItemScore.MinimumGain - 0.01f));
        Assert.True(PlayerbotEconomyPlanner.IsListable(item, PlayerbotItemScore.MinimumGain));
        Assert.False(PlayerbotEconomyPlanner.IsListable(item, PlayerbotItemScore.MinimumGain + 0.01f));
        Assert.False(PlayerbotEconomyPlanner.IsListable(item, 12f));
        Assert.True(PlayerbotEconomyPlanner.IsListable(item, -3f), "a worse item than the worn one is listable");
    }

    [Fact]
    public void IsListable_LeavesVendorTrash_AndOtherQualitiesOfGear_ToTheVendors()
    {
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(quality: 0)), null), "poor armor");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(quality: 1)), null), "white armor");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.Weapon, quality: 1)), null), "white weapon");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(quality: 5)), null), "legendary armor is not an auction good");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods, quality: 0)), null), "poor trade good");
    }

    [Fact]
    public void IsListable_ListsTradeGoodsAboveQualityPoor_WhateverTheGain()
    {
        Assert.True(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods, quality: 1)), null));
        Assert.True(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods, quality: 3)), 50f));
        // A trade good that binds is still not for the house.
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods, quality: 1, bonding: 1)), null));
    }

    [Fact]
    public void IsListable_NeverListsWhatTheBotKeepsOrCannotGive()
    {
        ItemTemplate green = Template();
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green, transferable: false), null), "bound, conjured or timed");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green, reserved: true), null), "a consumable it uses or a quest requirement");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green, count: 0), null), "an empty stack");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(sell: 0)), null), "no vendor price: no price evidence");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods, quality: 2, sell: 0)), null));

        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green with { BagFamily = 1 }), null), "ammunition and bags by family");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green with { StartQuest = 77 }), null), "a quest starter");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green with { FoodType = 1 }), null), "food");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(green with { RequiredSkill = 164 }), null), "a profession tool");
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods) with { RequiredSkill = 164 }), null));
        Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: ItemClass.TradeGoods) with { BagFamily = 1 }), null));
        foreach (ItemClass itemClass in new[] { ItemClass.Quest, ItemClass.Reagent, ItemClass.Consumable, ItemClass.Container, ItemClass.Key, ItemClass.Junk })
            Assert.False(PlayerbotEconomyPlanner.IsListable(Bag(Template(itemClass: itemClass, quality: 3)), null), $"{itemClass}");
    }

    [Fact]
    public void IsListable_RefusesANullItem()
        => Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.IsListable(null!, null));

    // --- Listables -------------------------------------------------------------------------------------------------

    [Fact]
    public void Listables_OrdersByReferencePriceTimesCount_ThenByLowestGuid()
    {
        ItemTemplate cheap = Template(entry: 100, itemClass: ItemClass.TradeGoods, quality: 1, sell: 100);   // reference 100
        ItemTemplate dear = Template(entry: 101, itemClass: ItemClass.TradeGoods, quality: 1, sell: 300);    // reference 300
        ItemTemplate other = Template(entry: 102, itemClass: ItemClass.TradeGoods, quality: 1, sell: 100);
        PlayerbotBagItem five = Bag(cheap, guid: 9, count: 5);        // 500
        PlayerbotBagItem single = Bag(dear, guid: 4, count: 1);       // 300
        PlayerbotBagItem tie = Bag(other, guid: 3, count: 5);         // 500, the lower guid than the first
        IReadOnlyList<PlayerbotBagItem> ordered = PlayerbotEconomyPlanner.Listables([five, single, tie], _ => null);
        Assert.Equal(new[] { tie.Guid, five.Guid, single.Guid }, ordered.Select(item => item.Guid).ToArray());
    }

    [Fact]
    public void Listables_LeavesOutUpgrades_UnlistableItems_AndGivenUpEntries()
    {
        ItemTemplate goods = Template(entry: 200, itemClass: ItemClass.TradeGoods, quality: 1, sell: 100);
        ItemTemplate upgrade = Template(entry: 201);
        ItemTemplate sold = Template(entry: 202, itemClass: ItemClass.TradeGoods, quality: 1, sell: 100);
        ItemTemplate junk = Template(entry: 203, quality: 0);
        var memory = new PlayerbotRelistMemory();
        memory.NoteExpired(202);
        Assert.Equal(1, memory.Count);
        Assert.False(memory.IsGivenUp(202), "one expiry is not yet a reason to give up");
        memory.NoteExpired(202);

        PlayerbotBagItem[] bag = [Bag(goods, 1), Bag(upgrade, 2), Bag(sold, 3), Bag(junk, 4)];
        var asked = new List<uint>();
        IReadOnlyList<PlayerbotBagItem> listed = PlayerbotEconomyPlanner.Listables(bag, template =>
        {
            asked.Add(template.Entry);
            return template.Entry == 201 ? 3f : null;
        }, memory);
        Assert.Equal(new[] { bag[0].Guid }, listed.Select(item => item.Guid).ToArray());
        // The given-up entry is not even scored.
        Assert.DoesNotContain(202u, asked);

        // Without the memory the given-up entry is listed again.
        Assert.Equal(2, PlayerbotEconomyPlanner.Listables(bag, template => template.Entry == 201 ? 3f : null).Count);
    }

    [Fact]
    public void Listables_RefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.Listables(null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.Listables([], null!));
        Assert.Empty(PlayerbotEconomyPlanner.Listables([], _ => null));
    }

    // --- listing caps ----------------------------------------------------------------------------------------------

    [Fact]
    public void FreeListingSlots_IsTheCapMinusWhatTheHouseCountsAsTheBotsOwn()
    {
        var options = new PlayerbotEconomyOptions { MaxListingsPerBot = 5 };
        Assert.Equal(5, PlayerbotEconomyPlanner.FreeListingSlots(options, 0));
        Assert.Equal(2, PlayerbotEconomyPlanner.FreeListingSlots(options, 3));
        Assert.Equal(0, PlayerbotEconomyPlanner.FreeListingSlots(options, 5));
        Assert.Equal(0, PlayerbotEconomyPlanner.FreeListingSlots(options, 9));
        Assert.Equal(0, PlayerbotEconomyPlanner.FreeListingSlots(options, uint.MaxValue));
        options.MaxListingsPerBot = 0;
        Assert.Equal(0, PlayerbotEconomyPlanner.FreeListingSlots(options, 0));
        options.MaxListingsPerBot = PlayerbotEconomyOptions.MaxListingsCeiling;
        Assert.Equal(PlayerbotEconomyOptions.MaxListingsCeiling, PlayerbotEconomyPlanner.FreeListingSlots(options, 0));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.FreeListingSlots(null!, 0));
    }

    [Fact]
    public void RollsToList_NeverAtZero_AlwaysAtOneHundred_AndBelowTheChanceOtherwise()
    {
        var options = new PlayerbotEconomyOptions { ListChancePerVisit = 0, MaxListingsPerBot = 5 };
        Assert.False(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(0)), "0 never lists, even on a roll of 0");
        options.ListChancePerVisit = 100;
        Assert.True(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(99)), "100 always lists, even on the highest roll");
        options.ListChancePerVisit = 50;
        Assert.True(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(0)));
        Assert.True(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(49)));
        Assert.False(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(50)));
        Assert.False(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(99)));
        options.MaxListingsPerBot = 0;
        Assert.False(PlayerbotEconomyPlanner.RollsToList(options, new FixedRandom(0)), "no slots at all: never a reason to list");
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.RollsToList(null!, new Random()));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.RollsToList(options, null!));
    }

    [Fact]
    public void ListingSlots_AreTheFreeSlots_WhenTheRollSucceeds_ElseNone()
    {
        var options = new PlayerbotEconomyOptions { ListChancePerVisit = 50, MaxListingsPerBot = 5 };
        Assert.Equal(3, PlayerbotEconomyPlanner.ListingSlots(options, 2, new FixedRandom(10)));
        Assert.Equal(0, PlayerbotEconomyPlanner.ListingSlots(options, 2, new FixedRandom(90)));
        Assert.Equal(0, PlayerbotEconomyPlanner.ListingSlots(options, 5, new FixedRandom(10)));
    }

    // --- the visit budget ------------------------------------------------------------------------------------------

    [Fact]
    public void VisitBudget_IsTheFractionOfTheMoneyHeld_RoundedDown_AndNeverMoreThanTheMoney()
    {
        Assert.Equal(250u, PlayerbotEconomyPlanner.VisitBudget(1000, 0.25f));
        Assert.Equal(250u, PlayerbotEconomyPlanner.VisitBudget(1001, 0.25f));
        Assert.Equal(0u, PlayerbotEconomyPlanner.VisitBudget(1000, 0f));
        Assert.Equal(0u, PlayerbotEconomyPlanner.VisitBudget(0, 0.25f));
        Assert.Equal(1000u, PlayerbotEconomyPlanner.VisitBudget(1000, 1f));
        Assert.Equal(1000u, PlayerbotEconomyPlanner.VisitBudget(1000, 7f)); // a fraction above 1 is clamped
        Assert.Equal(0u, PlayerbotEconomyPlanner.VisitBudget(1000, -0.5f));
        Assert.Equal(0u, PlayerbotEconomyPlanner.VisitBudget(1000, float.NaN));
        Assert.Equal(0u, PlayerbotEconomyPlanner.VisitBudget(1000, float.PositiveInfinity));
        Assert.Equal(2_147_483_647u, PlayerbotEconomyPlanner.VisitBudget(uint.MaxValue, 0.5f));
    }

    [Fact]
    public void Remaining_IsTheBudgetLeft_NeverNegative()
    {
        Assert.Equal(70u, PlayerbotEconomyPlanner.Remaining(100, 30));
        Assert.Equal(0u, PlayerbotEconomyPlanner.Remaining(100, 100));
        Assert.Equal(0u, PlayerbotEconomyPlanner.Remaining(100, 200));
        Assert.Equal(0u, PlayerbotEconomyPlanner.Remaining(0, 0));
        Assert.Equal(uint.MaxValue, PlayerbotEconomyPlanner.Remaining(uint.MaxValue, 0));
    }

    // --- the price plan --------------------------------------------------------------------------------------------

    [Fact]
    public void PlanListing_AsksTheReferencePrice_WithoutACompetitor_AndPaysTheHousesDeposit()
    {
        PlayerbotBagItem item = Bag(Template(sell: 1000));   // reference 1414
        PlayerbotListingPlan plan = Assert.IsType<PlayerbotListingPlan>(
            PlayerbotEconomyPlanner.PlanListing(item, [], Self, Alliance, 0, 1f, 1f, 10_000));
        Assert.Equal(1414u, plan.Buyout);
        Assert.Equal(PlayerbotAuctionPricing.StartBid(1414), plan.StartBid);
        Assert.Equal(1131u, plan.StartBid);
        Assert.Equal(200u, plan.Deposit);
        Assert.Equal(PlayerbotAuctionPricing.Deposit(Alliance, item.Template, 1), plan.Deposit);
        Assert.True(plan.StartBid <= plan.Buyout);
    }

    [Fact]
    public void PlanListing_UndercutsTheLowestOtherSeller_AndIgnoresItsOwnListing()
    {
        PlayerbotBagItem item = Bag(Template(sell: 1000));
        PlayerbotAuctionRow[] market = [Row(1, 6000, 1500), Row(2, 6000, 1200, owner: Self), Row(3, 6000, 2000)];
        PlayerbotListingPlan plan = Assert.IsType<PlayerbotListingPlan>(
            PlayerbotEconomyPlanner.PlanListing(item, market, Self, Alliance, 0, 1f, 1f, 10_000));
        Assert.Equal(1485u, plan.Buyout);   // 1% under 1500
        Assert.Equal(1188u, plan.StartBid);
    }

    [Fact]
    public void PlanListing_ScalesTheDepositAndBuyoutWithTheStack()
    {
        PlayerbotBagItem stack = Bag(Template(sell: 1000), count: 3);
        PlayerbotListingPlan plan = Assert.IsType<PlayerbotListingPlan>(
            PlayerbotEconomyPlanner.PlanListing(stack, [], Self, Alliance, 0, 1f, 1f, 10_000));
        Assert.Equal(1414u * 3, plan.Buyout);
        Assert.Equal(PlayerbotAuctionPricing.Deposit(Alliance, stack.Template, 3), plan.Deposit);
    }

    [Fact]
    public void PlanListing_IsNull_WhenTheDepositDoesNotFitTheBudgetLeft_OrTheListingDoesNotPay()
    {
        PlayerbotBagItem item = Bag(Template(sell: 1000));
        Assert.Null(PlayerbotEconomyPlanner.PlanListing(item, [], Self, Alliance, 0, 1f, 1f, remainingBudget: 199));
        Assert.NotNull(PlayerbotEconomyPlanner.PlanListing(item, [], Self, Alliance, 0, 1f, 1f, remainingBudget: 200));
        // A deposit minimum above the margin: not worth it.
        Assert.Null(PlayerbotEconomyPlanner.PlanListing(item, [], Self, Alliance, 5000, 1f, 1f, remainingBudget: 100_000));
        // A cut rate that eats the margin.
        Assert.Null(PlayerbotEconomyPlanner.PlanListing(item, [], Self, Alliance, 0, 1f, 10f, remainingBudget: 100_000));
        // No vendor price: no plan.
        Assert.Null(PlayerbotEconomyPlanner.PlanListing(Bag(Template(sell: 0)), [], Self, Alliance, 0, 1f, 1f, 100_000));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.PlanListing(null!, [], Self, Alliance, 0, 1f, 1f, 1));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.PlanListing(item, [], Self, null!, 0, 1f, 1f, 1));
    }

    // --- the upgrade to buy ----------------------------------------------------------------------------------------

    private static readonly Dictionary<uint, ItemTemplate> Templates = Enumerable.Range(7000, 20).ToDictionary(e => (uint)e, e => Template((uint)e));

    private static PlayerbotUpgradeChoice? Choose(IEnumerable<PlayerbotAuctionRow> rows, Dictionary<uint, float?> gains, uint startMoney = 10_000,
        uint money = 10_000, uint spent = 0, float fraction = 0.25f, Func<uint, bool>? refused = null)
        => PlayerbotEconomyPlanner.ChooseUpgrade(rows, Self, startMoney, money, spent, fraction, entry => Templates.GetValueOrDefault(entry),
            template => gains.GetValueOrDefault(template.Entry), refused);

    [Fact]
    public void ChooseUpgrade_TakesTheBiggestGainWithinTheBudget()
    {
        var gains = new Dictionary<uint, float?> { [7001] = 1f, [7002] = 4f, [7003] = 2f };
        PlayerbotAuctionRow[] rows = [Row(1, 7001, 500), Row(2, 7002, 900), Row(3, 7003, 100)];
        PlayerbotUpgradeChoice choice = Assert.IsType<PlayerbotUpgradeChoice>(Choose(rows, gains));
        Assert.Equal(2u, choice.Row.Id);
        Assert.Equal(4f, choice.Gain);
        // The order of the rows does not matter.
        Assert.Equal(2u, Assert.IsType<PlayerbotUpgradeChoice>(Choose(rows.Reverse(), gains)).Row.Id);
    }

    [Fact]
    public void ChooseUpgrade_NeverSpendsMoreThanTheBudgetLeft_OrTheMoneyInHand()
    {
        var gains = new Dictionary<uint, float?> { [7001] = 9f, [7002] = 1f };
        // Budget 25% of 10000 = 2500.
        Assert.Equal(2u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([Row(1, 7001, 2501), Row(2, 7002, 2500)], gains)).Row.Id);
        // Already spent 1000: 1500 left.
        Assert.Null(Choose([Row(1, 7001, 2000)], gains, spent: 1000));
        Assert.Equal(1u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([Row(1, 7001, 1500)], gains, spent: 1000)).Row.Id);
        // Spent the whole budget (or more, the deposits overran it): nothing.
        Assert.Null(Choose([Row(1, 7001, 1)], gains, spent: 2500));
        Assert.Null(Choose([Row(1, 7001, 1)], gains, spent: 9000));
        // Poorer than the budget now: the money in hand limits.
        Assert.Null(Choose([Row(1, 7001, 901)], gains, money: 900));
        Assert.Equal(1u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([Row(1, 7001, 900)], gains, money: 900)).Row.Id);
        // A budget fraction of 0 spends nothing.
        Assert.Null(Choose([Row(1, 7001, 1)], gains, fraction: 0f));
    }

    [Fact]
    public void ChooseUpgrade_SkipsOwnListings_BidOnlyAuctions_AndRefusedAuctions_ButNotTheAhBotsSellerZero()
    {
        var gains = new Dictionary<uint, float?> { [7001] = 5f, [7002] = 1f };
        PlayerbotAuctionRow own = Row(1, 7001, 100, owner: Self);
        PlayerbotAuctionRow bidOnly = Row(2, 7001, 0);
        PlayerbotAuctionRow refused = Row(3, 7001, 100);
        PlayerbotAuctionRow fallback = Row(4, 7002, 100);
        PlayerbotUpgradeChoice choice = Assert.IsType<PlayerbotUpgradeChoice>(Choose([own, bidOnly, refused, fallback], gains, refused: id => id == 3));
        Assert.Equal(4u, choice.Row.Id);
        Assert.Null(Choose([own, bidOnly, refused], gains, refused: id => id == 3));
        // A seller-less listing of the house's own bot (owner 0) is an ordinary row.
        Assert.Equal(5u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([Row(5, 7001, 100, owner: 0)], gains)).Row.Id);
    }

    [Fact]
    public void ChooseUpgrade_SkipsItemsThatGainNothing_CannotBeUsed_OrHaveNoTemplate()
    {
        var gains = new Dictionary<uint, float?>
        {
            [7001] = PlayerbotItemScore.MinimumGain,          // not above the minimum
            [7002] = 0f,
            [7003] = -2f,
            [7004] = null,                                    // the bot cannot use it
            [7005] = PlayerbotItemScore.MinimumGain + 0.01f,
        };
        PlayerbotAuctionRow[] rows = [Row(1, 7001, 10), Row(2, 7002, 10), Row(3, 7003, 10), Row(4, 7004, 10), Row(5, 7099, 10)];
        Assert.Null(Choose(rows, gains));
        Assert.Equal(6u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([.. rows, Row(6, 7005, 10)], gains)).Row.Id);
        Assert.Null(Choose([], gains));
    }

    [Fact]
    public void ChooseUpgrade_BreaksTiesByTheLowerBuyout_ThenTheLowerAuctionId()
    {
        var gains = new Dictionary<uint, float?> { [7001] = 3f, [7002] = 3f, [7003] = 3f };
        Assert.Equal(2u, Assert.IsType<PlayerbotUpgradeChoice>(Choose([Row(1, 7001, 300), Row(2, 7002, 200), Row(3, 7003, 250)], gains)).Row.Id);
        PlayerbotAuctionRow[] sameBuyout = [Row(9, 7001, 200), Row(4, 7002, 200), Row(7, 7003, 200)];
        Assert.Equal(4u, Assert.IsType<PlayerbotUpgradeChoice>(Choose(sameBuyout, gains)).Row.Id);
        Assert.Equal(4u, Assert.IsType<PlayerbotUpgradeChoice>(Choose(sameBuyout.Reverse(), gains)).Row.Id);
        // A bigger gain beats a lower price.
        gains[7003] = 3.5f;
        Assert.Equal(7u, Assert.IsType<PlayerbotUpgradeChoice>(Choose(sameBuyout, gains)).Row.Id);
    }

    [Fact]
    public void ChooseUpgrade_RefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseUpgrade(null!, Self, 1, 1, 0, 1f, _ => null, _ => null));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseUpgrade([], Self, 1, 1, 0, 1f, null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseUpgrade([], Self, 1, 1, 0, 1f, _ => null, null!));
    }

    // --- the reagents to buy ---------------------------------------------------------------------------------------

    /// <summary>A trade good with a vendor price of 100 copper: the reference price is 100 and the reagent cap 200 copper per unit.</summary>
    private static ItemTemplate Reagent(uint entry, uint sell = 100) => Template(entry, ItemClass.TradeGoods, quality: 1, sell: sell);

    private static readonly Dictionary<uint, ItemTemplate> ReagentTemplates = Enumerable.Range(8000, 10).ToDictionary(e => (uint)e, e => Reagent((uint)e));

    private const uint ReagentCap = 200;

    private static PlayerbotReagentChoice? ChooseReagent(IEnumerable<PlayerbotAuctionRow> rows, Dictionary<uint, uint> missing, uint limit = 10_000,
        Func<uint, bool>? refused = null, Func<uint, ItemTemplate?>? templates = null, Func<uint, bool>? vendorStocked = null)
        => PlayerbotEconomyPlanner.ChooseReagent(rows, Self, missing, limit, templates ?? (entry => ReagentTemplates.GetValueOrDefault(entry)), refused, vendorStocked);

    [Fact]
    public void ReagentUnitCap_IsTwiceTheReference_AndZeroWithoutAVendorPrice()
    {
        ItemTemplate plain = Reagent(8000);
        Assert.Equal(PlayerbotAuctionPricing.CapMultiple * PlayerbotAuctionPricing.ReferencePerUnit(plain), PlayerbotEconomyPlanner.ReagentUnitCap(plain));
        Assert.Equal(ReagentCap, PlayerbotEconomyPlanner.ReagentUnitCap(plain));

        foreach (ItemTemplate template in new[] { Reagent(8001, sell: 7), Reagent(8002, sell: 5000), Template(8003, ItemClass.TradeGoods, quality: 3, sell: 1000, buy: 9000) })
        {
            uint reference = PlayerbotAuctionPricing.ReferencePerUnit(template);
            Assert.True(reference > 0);
            Assert.Equal(2 * reference, PlayerbotEconomyPlanner.ReagentUnitCap(template));
        }

        Assert.Equal(0u, PlayerbotEconomyPlanner.ReagentUnitCap(Reagent(8004, sell: 0)));
    }

    [Fact]
    public void Shortfalls_AreThePerCraftCountLessWhatIsCarried_OnlyWhenShort()
    {
        var perCraft = new Dictionary<uint, uint> { [1] = 3, [2] = 2, [3] = 4, [4] = 0, [5] = 5, [6] = 1 };
        var carried = new Dictionary<uint, uint> { [1] = 0, [2] = 2, [3] = 3, [4] = 0, [5] = 9 };   // 6 is not carried at all
        IReadOnlyDictionary<uint, uint> shortfalls = PlayerbotEconomyPlanner.Shortfalls(perCraft, entry => carried.GetValueOrDefault(entry));

        Assert.Equal(new[] { (1u, 3u), (3u, 1u), (6u, 1u) }, shortfalls.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToArray());
        Assert.DoesNotContain(2u, shortfalls.Keys);   // carried exactly the per-craft count
        Assert.DoesNotContain(4u, shortfalls.Keys);   // a zero per-craft entry
        Assert.DoesNotContain(5u, shortfalls.Keys);   // carried more than needed
        Assert.Empty(PlayerbotEconomyPlanner.Shortfalls(new Dictionary<uint, uint>(), _ => 0));
        Assert.Empty(PlayerbotEconomyPlanner.Shortfalls(new Dictionary<uint, uint> { [4] = 0 }, _ => 0));
    }

    [Fact]
    public void ChooseReagent_PrefersARowThatCoversTheShortfall_ThenTheLowestPricePerUnit()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 3 };
        PlayerbotAuctionRow cheapShort = Row(1, 8000, 100, count: 2);      // 50 per unit, but two of the three missing
        PlayerbotAuctionRow covering = Row(2, 8000, 450, count: 3);        // 150 per unit
        PlayerbotAuctionRow dearerCovering = Row(3, 8000, 900, count: 5);  // 180 per unit
        PlayerbotAuctionRow single = Row(4, 8000, 60);                     // 60 per unit

        PlayerbotReagentChoice choice = Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([cheapShort, dearerCovering, covering, single], missing));
        Assert.Equal(2u, choice.Row.Id);
        Assert.Equal(3u, choice.Missing);
        Assert.Equal(2u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([single, covering, dearerCovering, cheapShort], missing)).Row.Id);

        // Without the cheapest covering row the other covering row still beats every row that falls short.
        Assert.Equal(3u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([cheapShort, single, dearerCovering], missing)).Row.Id);

        // When nothing covers, the lowest price per unit wins.
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([single, cheapShort], missing)).Row.Id);

        // The price per unit is compared exactly, not rounded down: 100/3 < 67/2 although both are 33 in integer division (neither covers five).
        var five = new Dictionary<uint, uint> { [8000] = 5 };
        PlayerbotAuctionRow threeFor100 = Row(5, 8000, 100, count: 3);
        PlayerbotAuctionRow twoFor67 = Row(6, 8000, 67, count: 2);
        Assert.Equal(5u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([twoFor67, threeFor100], five)).Row.Id);

        // The missing count reported is the one of the chosen row's entry.
        var both = new Dictionary<uint, uint> { [8000] = 3, [8001] = 1 };
        PlayerbotReagentChoice other = Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([Row(7, 8000, 450, count: 3), Row(8, 8001, 90)], both));
        Assert.Equal((8u, 1u), (other.Row.Id, other.Missing));
    }

    [Fact]
    public void ChooseReagent_BreaksTiesByLowerBuyout_ThenLowerId()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 1 };
        // Same price per unit (100): the smaller buyout first.
        PlayerbotAuctionRow pair = Row(5, 8000, 200, count: 2);
        PlayerbotAuctionRow single = Row(6, 8000, 100);
        Assert.Equal(6u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([pair, single], missing)).Row.Id);
        Assert.Equal(6u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([single, pair], missing)).Row.Id);

        // Same price per unit and the same buyout: the lower auction id.
        PlayerbotAuctionRow[] same = [Row(9, 8000, 100), Row(4, 8000, 100), Row(7, 8000, 100)];
        Assert.Equal(4u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent(same, missing)).Row.Id);
        Assert.Equal(4u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent(same.Reverse(), missing)).Row.Id);
    }

    [Fact]
    public void ChooseReagent_SkipsOwnBidOnlyRefusedUnneededAndUntemplatedRows()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 1, [8001] = 0, [8002] = 1, [8003] = 1 };
        Dictionary<uint, ItemTemplate> templates = new(ReagentTemplates) { [8003] = Reagent(8003, sell: 0) };
        PlayerbotAuctionRow own = Row(1, 8000, 10, owner: Self);
        PlayerbotAuctionRow bidOnly = Row(2, 8000, 0);
        PlayerbotAuctionRow refused = Row(3, 8000, 10);
        PlayerbotAuctionRow notWanted = Row(4, 8004, 10);          // an entry that is not missing at all
        PlayerbotAuctionRow zeroMissing = Row(5, 8001, 10);        // an entry whose missing count is 0
        PlayerbotAuctionRow untemplated = Row(6, 8002, 10);        // no template for the entry
        PlayerbotAuctionRow noVendorPrice = Row(7, 8003, 10);      // reagent cap 0: never bought
        PlayerbotAuctionRow emptyStack = Row(8, 8000, 10, count: 0);
        PlayerbotAuctionRow valid = Row(9, 8000, 150);
        Func<uint, ItemTemplate?> lookup = entry => entry == 8002 ? null : templates.GetValueOrDefault(entry);
        PlayerbotAuctionRow[] all = [own, bidOnly, refused, notWanted, zeroMissing, untemplated, noVendorPrice, emptyStack];

        Assert.Null(ChooseReagent(all, missing, refused: id => id == 3, templates: lookup));
        Assert.Equal(9u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([.. all, valid], missing, refused: id => id == 3, templates: lookup)).Row.Id);
        // Without the refusal predicate the refused row is an ordinary cheap row.
        Assert.Equal(3u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent(all, missing, templates: lookup)).Row.Id);
        Assert.Null(ChooseReagent([], missing));
        Assert.Null(ChooseReagent([valid], new Dictionary<uint, uint>()));
    }

    [Fact]
    public void ChooseReagent_NeverPaysMoreThanTwiceTheReferencePerUnit()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 1 };
        // The cap is 200 per unit: exactly at the cap is allowed, one copper per unit over it is not (ceil(buyout / count)).
        Assert.NotNull(ChooseReagent([Row(1, 8000, ReagentCap)], missing));
        Assert.Null(ChooseReagent([Row(1, 8000, ReagentCap + 1)], missing));
        Assert.NotNull(ChooseReagent([Row(1, 8000, 2 * ReagentCap, count: 2)], missing));
        Assert.Null(ChooseReagent([Row(1, 8000, (2 * ReagentCap) + 1, count: 2)], missing));
        var three = new Dictionary<uint, uint> { [8000] = 3 };   // a stack of three is within the overbuy bound only for a shortfall of two or more
        Assert.NotNull(ChooseReagent([Row(1, 8000, 3 * ReagentCap, count: 3)], three));
        Assert.Null(ChooseReagent([Row(1, 8000, (3 * ReagentCap) + 1, count: 3)], three));   // 200.33 per unit rounds up to 201
        // An over-cap row is passed over for a dearer-in-total but in-cap row.
        Assert.Equal(2u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([Row(1, 8000, 401, count: 2), Row(2, 8000, 400, count: 2)], missing)).Row.Id);
    }

    [Fact]
    public void ChooseReagent_NeverChoosesAVendorStockedEntry_ButStillChoosesANeighbouringOne()
    {
        Assert.Equal(2u, PlayerbotEconomyPlanner.ReagentOverbuyFactor);
        var missing = new Dictionary<uint, uint> { [8000] = 1, [8001] = 1 };
        PlayerbotAuctionRow stocked = Row(1, 8000, 100);          // cheap and covering, but a vendor sells entry 8000
        PlayerbotAuctionRow notStocked = Row(2, 8001, 190);       // dearer, but no vendor sells entry 8001
        Func<uint, bool> isStocked = entry => entry == 8000;

        Assert.Null(ChooseReagent([stocked], missing, vendorStocked: isStocked));
        Assert.Equal(2u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([stocked, notStocked], missing, vendorStocked: isStocked)).Row.Id);
        Assert.Equal(2u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([notStocked, stocked], missing, vendorStocked: isStocked)).Row.Id);
        // Without the predicate (or when it says no) the cheap row is an ordinary candidate.
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([stocked, notStocked], missing)).Row.Id);
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([stocked, notStocked], missing, vendorStocked: _ => false)).Row.Id);
        Assert.Null(ChooseReagent([stocked, notStocked], missing, vendorStocked: _ => true));
    }

    [Fact]
    public void ChooseReagent_SkipsARowWhoseCountExceedsTwiceTheShortfall()
    {
        var lacking1 = new Dictionary<uint, uint> { [8000] = 1 };
        PlayerbotAuctionRow two = Row(1, 8000, 100, count: 2);        // exactly lacking * 2: eligible
        PlayerbotAuctionRow three = Row(2, 8000, 100, count: 3);      // one over the bound
        PlayerbotAuctionRow twenty = Row(3, 8000, 100, count: 20);    // a whole stack for a shortfall of one

        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([two], lacking1)).Row.Id);
        Assert.Null(ChooseReagent([three], lacking1));
        Assert.Null(ChooseReagent([twenty], lacking1));
        Assert.Null(ChooseReagent([three, twenty], lacking1));
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([twenty, three, two], lacking1)).Row.Id);

        // The bound scales with the shortfall: lacking 3 admits a stack of 6, not of 7.
        var lacking3 = new Dictionary<uint, uint> { [8000] = 3 };
        Assert.NotNull(ChooseReagent([Row(4, 8000, 100, count: 6)], lacking3));
        Assert.Null(ChooseReagent([Row(5, 8000, 100, count: 7)], lacking3));
    }

    [Fact]
    public void ChooseReagent_AmongCoveringRows_PrefersTheLowerTotalBuyout_OverTheLowerPricePerUnit()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 2 };
        PlayerbotAuctionRow pair = Row(1, 8000, 180, count: 2);       // 90 per unit, 180 in total
        PlayerbotAuctionRow bigStack = Row(2, 8000, 200, count: 4);   // 50 per unit, 200 in total (covers, within twice the shortfall)
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([pair, bigStack], missing)).Row.Id);
        Assert.Equal(1u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([bigStack, pair], missing)).Row.Id);

        // A tie in the total goes to the lower price per unit, then to the lower auction id.
        PlayerbotAuctionRow tieBig = Row(3, 8000, 160, count: 4);     // 40 per unit
        PlayerbotAuctionRow tiePair = Row(4, 8000, 160, count: 2);    // 80 per unit
        Assert.Equal(3u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([tiePair, tieBig], missing)).Row.Id);
        Assert.Equal(5u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([Row(9, 8000, 160, count: 2), Row(5, 8000, 160, count: 2)], missing)).Row.Id);

        // Rows that do not cover keep the per-unit ranking: two single units of a shortfall of two, then rows of different sizes.
        PlayerbotAuctionRow dearUnit = Row(6, 8000, 90);
        PlayerbotAuctionRow cheapUnit = Row(7, 8000, 40);
        Assert.Equal(7u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([dearUnit, cheapUnit], missing)).Row.Id);
        var five = new Dictionary<uint, uint> { [8000] = 5 };
        PlayerbotAuctionRow fewDear = Row(8, 8000, 100, count: 1);    // 100 per unit, 100 in total
        PlayerbotAuctionRow manyCheap = Row(10, 8000, 160, count: 4); // 40 per unit, 160 in total: the lower price per unit wins when neither covers
        Assert.Equal(10u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([fewDear, manyCheap], five)).Row.Id);
    }

    [Fact]
    public void ChooseReagent_NeverSpendsMoreThanTheLimit()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 1 };
        Assert.NotNull(ChooseReagent([Row(1, 8000, 150)], missing, limit: 150));
        Assert.Null(ChooseReagent([Row(1, 8000, 151)], missing, limit: 150));
        Assert.Null(ChooseReagent([Row(1, 8000, 1)], missing, limit: 0));
        Assert.Equal(2u, Assert.IsType<PlayerbotReagentChoice>(ChooseReagent([Row(1, 8000, 190), Row(2, 8000, 180)], missing, limit: 185)).Row.Id);
    }

    [Fact]
    public void ChooseReagent_RefusesNullArguments()
    {
        var missing = new Dictionary<uint, uint> { [8000] = 1 };
        Func<uint, ItemTemplate?> templates = _ => null;
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseReagent(null!, Self, missing, 100, templates));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseReagent([], Self, null!, 100, templates));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyPlanner.ChooseReagent([], Self, missing, 100, null!));
    }

    // --- the bought memory -----------------------------------------------------------------------------------------

    [Fact]
    public void BoughtMemory_HoldsAnEntryFor24Hours_ThenForgetsIt()
    {
        Assert.Equal(86_400_000u, PlayerbotBoughtMemory.HoldMs);
        var memory = new PlayerbotBoughtMemory();
        Assert.False(memory.IsHeld(10, 5_000));
        memory.NoteBought(10, 5_000);
        Assert.Equal(1, memory.Count);
        Assert.True(memory.IsHeld(10, 5_000));
        Assert.True(memory.IsHeld(10, 5_000 + PlayerbotBoughtMemory.HoldMs - 1));
        Assert.False(memory.IsHeld(11, 5_000), "another entry is unaffected");
        Assert.False(memory.IsHeld(10, 5_000 + PlayerbotBoughtMemory.HoldMs), "the hold ends after exactly 24 hours");
        Assert.Equal(0, memory.Count);
        Assert.False(memory.IsHeld(10, 5_000), "an entry past the hold is forgotten");

        // Buying again restarts the hold.
        memory.NoteBought(10, 0);
        memory.NoteBought(10, 80_000_000);
        Assert.True(memory.IsHeld(10, 80_000_000 + PlayerbotBoughtMemory.HoldMs - 1));
        Assert.Equal(1, memory.Count);

        // World time wraps past uint.MaxValue: bought 100 ms before the wrap, 601 ms later is still within the hold.
        var wrapped = new PlayerbotBoughtMemory();
        uint boughtAt = uint.MaxValue - 100;
        wrapped.NoteBought(20, boughtAt);
        Assert.True(wrapped.IsHeld(20, 500));
        Assert.True(wrapped.IsHeld(20, unchecked(boughtAt + PlayerbotBoughtMemory.HoldMs - 1)));
        Assert.False(wrapped.IsHeld(20, unchecked(boughtAt + PlayerbotBoughtMemory.HoldMs)));
        Assert.Equal(0, wrapped.Count);
    }

    [Fact]
    public void BoughtMemory_CountsPurchasesAwaitingCollection_AndNeverGoesNegative()
    {
        var memory = new PlayerbotBoughtMemory();
        memory.NoteBought(10, 1_000);
        memory.NoteBought(10, 2_000);
        memory.NoteCollected(10);
        Assert.True(memory.IsAwaitingCollection(10, 3_000), "two bought, one collected: one letter still waits");
        memory.NoteCollected(10);
        Assert.False(memory.IsAwaitingCollection(10, 3_000), "the second collection clears it");
        Assert.True(memory.IsHeld(10, 3_000), "collected, but still held against listing");

        // Extra collections do not drive the count below zero: one later purchase and one collection leave nothing awaited.
        memory.NoteCollected(10);
        memory.NoteCollected(10);
        memory.NoteBought(10, 4_000);
        Assert.True(memory.IsAwaitingCollection(10, 4_000));
        memory.NoteCollected(10);
        Assert.False(memory.IsAwaitingCollection(10, 4_000), "a later purchase needs exactly one collection");

        // The hold is unchanged: a purchase restarts it, and an uncollected count stops being awaited when the hold ends.
        var held = new PlayerbotBoughtMemory();
        held.NoteBought(20, 0);
        held.NoteBought(20, 5_000);
        Assert.True(held.IsAwaitingCollection(20, 5_000 + PlayerbotBoughtMemory.HoldMs - 1));
        Assert.False(held.IsAwaitingCollection(20, 5_000 + PlayerbotBoughtMemory.HoldMs));
        Assert.False(held.IsHeld(20, 5_000 + PlayerbotBoughtMemory.HoldMs));
    }

    [Fact]
    public void BoughtMemory_AwaitsCollectionUntilCollected_OrTheHoldEnds()
    {
        var memory = new PlayerbotBoughtMemory();
        Assert.False(memory.IsAwaitingCollection(10, 1_000), "nothing bought, nothing awaited");

        memory.NoteBought(10, 1_000);
        Assert.True(memory.IsAwaitingCollection(10, 1_000), "a fresh purchase waits in the mailbox");
        Assert.True(memory.IsAwaitingCollection(10, 1_000 + PlayerbotBoughtMemory.HoldMs - 1));
        Assert.False(memory.IsAwaitingCollection(11, 1_000), "another entry is unaffected");

        memory.NoteCollected(11);
        Assert.True(memory.IsAwaitingCollection(10, 2_000), "collecting another entry changes nothing");

        memory.NoteCollected(10);
        Assert.False(memory.IsAwaitingCollection(10, 2_000), "collected: no longer awaited");
        Assert.True(memory.IsHeld(10, 2_000), "collected, but still held against listing");
        memory.NoteCollected(10);
        Assert.True(memory.IsHeld(10, 2_000), "collecting twice is harmless");

        // Buying the entry again re-arms the awaiting flag.
        memory.NoteBought(10, 3_000);
        Assert.True(memory.IsAwaitingCollection(10, 3_000));

        // An uncollected purchase stops being awaited when the hold ends.
        Assert.False(memory.IsAwaitingCollection(10, 3_000 + PlayerbotBoughtMemory.HoldMs), "past the hold: neither held nor awaited");
        Assert.False(memory.IsHeld(10, 3_000 + PlayerbotBoughtMemory.HoldMs));

        // Collecting an entry that was never bought (or already forgotten) does not create it.
        memory.NoteCollected(99);
        Assert.False(memory.IsHeld(99, 3_000));
        Assert.False(memory.IsAwaitingCollection(99, 3_000));
    }

    [Fact]
    public void BoughtMemory_IsNotHeld_AfterAGapLongerThan2Pow31Ms()
    {
        // 25 days = 2_160_000_000 ms, past 2^31 (2_147_483_648): a signed comparison of the gap would read it as negative, i.e. still held.
        const uint TwentyFiveDays = 25u * 24 * 3600 * 1000;
        Assert.True(TwentyFiveDays > int.MaxValue);
        var memory = new PlayerbotBoughtMemory();
        memory.NoteBought(10, 1_000);
        Assert.False(memory.IsHeld(10, unchecked(1_000 + TwentyFiveDays)));

        var awaiting = new PlayerbotBoughtMemory();
        awaiting.NoteBought(10, 1_000);
        Assert.False(awaiting.IsAwaitingCollection(10, unchecked(1_000 + TwentyFiveDays)), "an uncollected purchase is not awaited forever");
    }

    [Fact]
    public void BoughtMemory_IsBounded_AndTheOldestPurchaseGoesFirst()
    {
        var memory = new PlayerbotBoughtMemory();
        for (uint entry = 1; entry <= PlayerbotBoughtMemory.MaxEntries; entry++) memory.NoteBought(entry, entry * 10);

        Assert.Equal(PlayerbotBoughtMemory.MaxEntries, memory.Count);
        uint now = (PlayerbotBoughtMemory.MaxEntries * 10) + 10;
        memory.NoteBought(1000, now);
        Assert.Equal(PlayerbotBoughtMemory.MaxEntries, memory.Count);
        Assert.False(memory.IsHeld(1, now), "the oldest purchase was dropped");
        Assert.True(memory.IsHeld(2, now));
        Assert.True(memory.IsHeld(1000, now));
        // A repeat of a known entry never grows the memory.
        memory.NoteBought(2, now);
        Assert.Equal(PlayerbotBoughtMemory.MaxEntries, memory.Count);
    }

    // --- auction letter subjects -----------------------------------------------------------------------------------

    [Fact]
    public void TryParseAuctionSubject_ReadsTheSubjectsTheHouseWrites_ForEveryAction()
    {
        foreach (AuctionMailAction action in Enum.GetValues<AuctionMailAction>())
        {
            string subject = MailRules.AuctionSubject(4321, action);
            Assert.True(PlayerbotEconomyPlanner.TryParseAuctionSubject(subject, out uint entry, out AuctionMailAction parsed), subject);
            Assert.Equal(4321u, entry);
            Assert.Equal(action, parsed);
        }

        Assert.True(PlayerbotEconomyPlanner.TryParseAuctionSubject("2589:0:3", out uint expired, out AuctionMailAction expiry));
        Assert.Equal((2589u, AuctionMailAction.Expired), (expired, expiry));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("2589:0")]
    [InlineData("2589:0:3:1")]
    [InlineData("2589:1:3")]
    [InlineData("abc:0:3")]
    [InlineData("-5:0:3")]
    [InlineData("2589:0:99")]
    [InlineData("2589:0:-1")]
    [InlineData("2589:0:x")]
    [InlineData(":0:3")]
    [InlineData("4294967296:0:3")]
    public void TryParseAuctionSubject_RefusesAnythingElse(string subject)
    {
        Assert.False(PlayerbotEconomyPlanner.TryParseAuctionSubject(subject, out uint entry, out AuctionMailAction action), subject);
        Assert.Equal(default(AuctionMailAction), action);
    }

    [Fact]
    public void TryParseAuctionSubject_TreatsANullSubjectAsEmpty()
        => Assert.False(PlayerbotEconomyPlanner.TryParseAuctionSubject(null!, out _, out _));

    // --- the relist memory -----------------------------------------------------------------------------------------

    [Fact]
    public void RelistMemory_GivesUpAnEntryAfterTwoExpiries_AndASaleForgetsIt()
    {
        var memory = new PlayerbotRelistMemory();
        Assert.False(memory.IsGivenUp(10));
        memory.NoteExpired(10);
        Assert.False(memory.IsGivenUp(10));
        memory.NoteExpired(10);
        Assert.True(memory.IsGivenUp(10));
        memory.NoteExpired(10);
        Assert.True(memory.IsGivenUp(10));
        Assert.Equal(1, memory.Count);
        Assert.False(memory.IsGivenUp(11), "another entry is unaffected");
        memory.NoteSold(10);
        Assert.False(memory.IsGivenUp(10));
        Assert.Equal(0, memory.Count);
        memory.NoteSold(10);   // forgetting what is not known is harmless
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void RelistMemory_IsBounded_AndTheOldestEntryGoesFirst()
    {
        var memory = new PlayerbotRelistMemory();
        for (uint entry = 1; entry <= PlayerbotRelistMemory.MaxEntries; entry++)
        {
            memory.NoteExpired(entry);
            memory.NoteExpired(entry);
        }

        Assert.Equal(PlayerbotRelistMemory.MaxEntries, memory.Count);
        memory.NoteExpired(1000);
        Assert.Equal(PlayerbotRelistMemory.MaxEntries, memory.Count);
        Assert.False(memory.IsGivenUp(1), "the oldest entry was dropped");
        Assert.True(memory.IsGivenUp(2));
        Assert.False(memory.IsGivenUp(1000), "one expiry of the new entry");
        memory.NoteExpired(1000);
        Assert.True(memory.IsGivenUp(1000));
        Assert.Equal(PlayerbotRelistMemory.MaxEntries, memory.Count);
        // A repeat of a known entry never grows the memory.
        memory.NoteExpired(2);
        Assert.Equal(PlayerbotRelistMemory.MaxEntries, memory.Count);
    }

    // --- the options -----------------------------------------------------------------------------------------------

    [Fact]
    public void Options_BuyReagentsIsOnByDefault_AndEitherValueIsValid()
    {
        Assert.True(new PlayerbotEconomyOptions().BuyReagents);
        new PlayerbotEconomyOptions { BuyReagents = false }.Validate();
        new PlayerbotEconomyOptions { BuyReagents = true }.Validate();
    }

    [Fact]
    public void Options_DefaultsAreValid_AndEveryBoundIsEnforced()
    {
        var defaults = new PlayerbotEconomyOptions();
        defaults.Validate();
        Assert.True(defaults.Enabled);
        Assert.Equal((50, 5, 0.25f, 30), (defaults.ListChancePerVisit, defaults.MaxListingsPerBot, defaults.BuyUpgradeBudgetFraction, defaults.VisitIntervalMinutes));

        new PlayerbotEconomyOptions { ListChancePerVisit = 0, MaxListingsPerBot = 0, BuyUpgradeBudgetFraction = 0f, VisitIntervalMinutes = 1 }.Validate();
        new PlayerbotEconomyOptions
        {
            ListChancePerVisit = 100, MaxListingsPerBot = PlayerbotEconomyOptions.MaxListingsCeiling, BuyUpgradeBudgetFraction = 1f,
            VisitIntervalMinutes = PlayerbotEconomyOptions.MaxVisitIntervalMinutes,
        }.Validate();

        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { ListChancePerVisit = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { ListChancePerVisit = 101 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { MaxListingsPerBot = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { MaxListingsPerBot = PlayerbotEconomyOptions.MaxListingsCeiling + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { BuyUpgradeBudgetFraction = -0.1f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { BuyUpgradeBudgetFraction = 1.1f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { BuyUpgradeBudgetFraction = float.NaN }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { VisitIntervalMinutes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotEconomyOptions { VisitIntervalMinutes = PlayerbotEconomyOptions.MaxVisitIntervalMinutes + 1 }.Validate());
    }
}
