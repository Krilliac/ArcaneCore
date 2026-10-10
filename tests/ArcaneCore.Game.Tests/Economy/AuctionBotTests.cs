using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>The auction house bot's pricing, item pool and planner (cMaNGOS AuctionHouseBot.cpp) and its custody ledger (MaNGOS Zero Custody*).</summary>
public sealed class AuctionBotTests
{
    private static ItemTemplate Item(uint entry, uint quality = 2, uint itemClass = 4, uint buy = 1000, uint sell = 250, uint stack = 1,
        uint bonding = 0, uint flags = 0, uint requiredLevel = 10, uint itemLevel = 15) => new()
        {
            Entry = entry, Quality = quality, Class = itemClass, BuyPrice = buy, SellPrice = sell, Stackable = stack, Bonding = bonding,
            Flags = flags, RequiredLevel = requiredLevel, ItemLevel = itemLevel, Name = $"item {entry}",
        };

    private static AuctionBotOptions Options(Action<AuctionBotOptions>? change = null)
    {
        var options = new AuctionBotOptions { Enabled = true };
        change?.Invoke(options);
        Assert.Empty(options.Normalize());
        return options;
    }

    [Fact]
    public void Defaults_are_off_and_match_the_cmangos_conf()
    {
        var options = new AuctionBotOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.Normalize());
        Assert.Equal((10u, 10u), (options.SellChance, options.BuyChance));
        Assert.Equal((75u, 90u, 2u, 24u, 80u, 10u), (options.BidMinPercent, options.BidMaxPercent, options.TimeMinHours, options.TimeMaxHours,
            options.BuyValuePercent, options.ValueVariance));
        uint[][] values = options.ParseValues();
        Assert.All(values[0], v => Assert.Equal(0u, v));
        Assert.Equal(100u, values[1][0]);
        Assert.Equal(0u, values[1][2]); // white weapons are not sold (ahbot.conf.dist.in Value.Normal)
        Assert.Equal(800u, values[4][4]);
        Assert.Equal(AuctionBotOptions.ItemClassCount, values[3].Length);
        Assert.Equal([2u, 6u, 7u], options.HouseIds());
        Assert.Empty(options.BlacklistIds());
        Assert.Equal([7u, 3u], new AuctionBotOptions { Houses = " 7, x,0,3,7" }.HouseIds());
    }

    [Fact]
    public void Normalize_falls_back_like_GetMinMaxConfig_and_orders_the_pairs()
    {
        var options = new AuctionBotOptions { SellChance = 150, TimeMinHours = 0, BidMinPercent = 95, BidMaxPercent = 80, UpdateIntervalSeconds = 0 };
        IReadOnlyList<string> fixes = options.Normalize();
        Assert.Equal(10u, options.SellChance);
        Assert.Equal(2u, options.TimeMinHours);
        Assert.Equal(80u, options.BidMinPercent);
        Assert.Equal(20u, options.UpdateIntervalSeconds);
        Assert.Equal(4, fixes.Count);
        Assert.All(fixes, f => Assert.StartsWith("AuctionHouseBot:", f, StringComparison.Ordinal));
    }

    [Fact]
    public void Value_lists_parse_like_atoi_with_missing_values_zero()
    {
        uint[] values = AuctionBotOptions.ParseValueList(" 5, x ,7");
        Assert.Equal([5u, 0u, 7u], values[..3]);
        Assert.All(values[3..], v => Assert.Equal(0u, v));
    }

    [Theory]
    [InlineData(1000u, 250u, 2u, 2000u)]  // buy price × 200 %
    [InlineData(0u, 250u, 2u, 2500u)]     // no buy price: sell × 5 for green
    [InlineData(0u, 250u, 1u, 1000u)]     // no buy price: sell × 4 for white, × 100 %
    [InlineData(600u, 100u, 2u, 1000u)]   // buy price more than 5 × sell (arrows): sell × 5 × 200 %
    public void BuyoutPerItem_follows_CalculateBuyoutPrice(uint buy, uint sell, uint quality, uint expected)
    {
        var pricing = new AuctionBotPricing(Options());
        Assert.Equal(expected, pricing.BuyoutPerItem(Item(1, quality, itemClass: 0, buy: buy, sell: sell)));
    }

    [Fact]
    public void Vendor_items_are_priced_at_the_vendor_price()
    {
        var item = Item(7, quality: 2, itemClass: 0, buy: 1000);
        Assert.Equal(1000u, new AuctionBotPricing(Options(), new HashSet<uint> { 7 }).BuyoutPerItem(item));
        Assert.Equal(2000u, new AuctionBotPricing(Options(o => o.VendorValue = false), new HashSet<uint> { 7 }).BuyoutPerItem(item));
    }

    [Fact]
    public void Variance_spans_minus_v_to_v_plus_one_percent_steps()
    {
        var pricing = new AuctionBotPricing(Options());
        var random = new Random(5);
        var seen = new HashSet<uint>();
        for (int i = 0; i < 5000; i++)
        {
            seen.Add(pricing.WithVariance(1000, random));
        }

        Assert.Equal(900u, seen.Min());
        Assert.Equal(1110u, seen.Max());
        Assert.Equal(22, seen.Count);
        // Variance 0 still has cMaNGOS's inclusive urand(0, 1) step: the value or 1 % above it.
        var exact = new AuctionBotPricing(Options(o => o.ValueVariance = 0));
        Assert.All(Enumerable.Range(0, 50).Select(_ => exact.WithVariance(1000, random)), v => Assert.True(v is 1000 or 1010, v.ToString()));
    }

    [Fact]
    public void Pool_applies_the_cmangos_filters_and_groups_by_quality_and_class()
    {
        var options = Options(o => { o.MaxRequiredLevel = 40; o.Blacklist = "9"; });
        var pricing = new AuctionBotPricing(options);
        ItemTemplate[] templates =
        [
            Item(1), // ok
            Item(2, bonding: AuctionBotItemPool.BindOnPickup),
            Item(3, bonding: AuctionBotItemPool.BindQuestItem),
            Item(4, flags: (uint)ItemTemplateFlags.Lootable),
            Item(5, flags: (uint)ItemTemplateFlags.Conjured),
            Item(6, requiredLevel: 41),
            Item(7, itemLevel: 46), // item level cap = 40 + 5 below level 60
            Item(8, quality: 1, itemClass: 2), // white weapon: value 0
            Item(9), // blacklisted
            Item(10, buy: 0, sell: 0), // no price
            Item(11, quality: 3, itemClass: 0, itemLevel: 45),
            Item(12, stack: 0),
        ];
        var pool = new AuctionBotItemPool(templates, options, pricing);
        Assert.Equal([1u, 11u], pool.All.Select(t => t.Entry).ToArray());
        Assert.Equal(11u, Assert.Single(pool.Group(3, 0)).Entry);
        Assert.True(pool.Contains(1));
        Assert.False(pool.Contains(2));

        // At the max level the item level cap is removed (cMaNGOS CalculateItemLevelCap).
        var open = new AuctionBotItemPool([Item(20, requiredLevel: 60, itemLevel: 92)], Options(), pricing);
        Assert.Equal(1, open.Count);
    }

    [Fact]
    public void PlanSell_splits_into_stacks_prices_with_variance_and_respects_room()
    {
        var options = Options(o =>
        {
            o.TemplatesPerSellMin = 3;
            o.TemplatesPerSellMax = 3;
            o.StackPercentMin = 100;
            o.StackPercentMax = 100;
            o.ValueVariance = 0;
            o.BidMinPercent = 50;
            o.BidMaxPercent = 50;
            o.TimeMinHours = 12;
            o.TimeMaxHours = 12;
        });
        var pricing = new AuctionBotPricing(options);
        var pool = new AuctionBotItemPool([Item(1, quality: 1, itemClass: 7, buy: 10, sell: 2, stack: 20)], options, pricing);
        var planner = new AuctionBotPlanner(options, pricing, pool, new Random(1));
        IReadOnlyList<AuctionBotSellIntent> intents = planner.PlanSell(room: 100);

        // Three white draws (always kept) of a full stack: 60 items as three stacks of 20, one value for the entry.
        Assert.Equal(3, intents.Count);
        Assert.All(intents, i =>
        {
            Assert.Equal((1u, 20u, 12u), (i.Entry, i.Count, i.Hours));
            Assert.Equal(i.Buyout / 2, i.StartBid);
            Assert.Equal(200u, i.Buyout); // 20 × 10 copper at 100 %; the +1 % step of a 10 copper value rounds to 0
        });
        Assert.Single(intents.Select(i => i.Buyout).Distinct());
        Assert.Equal(2, planner.PlanSell(room: 2).Count);
        Assert.Empty(planner.PlanSell(room: 0));
    }

    [Fact]
    public void PlanSell_keeps_higher_qualities_less_often_and_never_draws_poor()
    {
        var options = Options(o =>
        {
            o.TemplatesPerSellMin = 4000;
            o.TemplatesPerSellMax = 4000;
            o.ValuePoor = string.Join(',', Enumerable.Repeat("100", 17));
        });
        var pricing = new AuctionBotPricing(options);
        var pool = new AuctionBotItemPool([Item(1, quality: 0), Item(2, quality: 1, itemClass: 0), Item(3, quality: 2), Item(4, quality: 4)], options, pricing);
        Assert.Equal(4, pool.Count);
        var planner = new AuctionBotPlanner(options, pricing, pool, new Random(3));
        Dictionary<uint, int> counts = planner.PlanSell(int.MaxValue).GroupBy(i => i.Entry).ToDictionary(g => g.Key, g => g.Count());
        Assert.False(counts.ContainsKey(1));
        Assert.True(counts[2] > counts[3] && counts[3] > counts[4], string.Join(", ", counts));
        Assert.InRange(counts[2], 900, 1100);  // white: every draw (1000 expected)
        Assert.InRange(counts[4], 60, 200);    // epic: 1 in 8 (125 expected)
    }

    [Fact]
    public void PlanBuy_buys_out_only_player_auctions_below_the_buy_value()
    {
        var options = Options(o => { o.ValueVariance = 0; o.BuyValuePercent = 100; o.Blacklist = "3"; });
        var pricing = new AuctionBotPricing(options);
        ItemTemplate cheap = Item(1, itemClass: 0, buy: 1000); // value 2000 per item
        ItemTemplate filtered = Item(2, quality: 1, itemClass: 2, buy: 1000); // white weapon: value 0
        ItemTemplate black = Item(3, itemClass: 0, buy: 1000);
        var templates = new Dictionary<uint, ItemTemplate> { [1] = cheap, [2] = filtered, [3] = black };
        var planner = new AuctionBotPlanner(options, pricing, new AuctionBotItemPool(templates.Values, options, pricing), new Random(2));
        AuctionRecord A(uint id, uint entry, uint buyout, int seller = 5, uint count = 1) => new()
        {
            Id = id, ItemEntry = entry, ItemCount = count, SellerId = seller, StartBid = 1, Buyout = buyout,
        };
        IReadOnlyList<AuctionBotBuyIntent> buys = planner.PlanBuy(
        [
            A(1, 1, 1500),            // below 2000: bought
            A(2, 1, 2500),            // above: bid at the start bid instead
            A(3, 1, 3000, count: 2),  // 2 × 2000 = 4000 > 3000: bought
            A(4, 1, 1, seller: 0),    // the bot's own
            A(5, 1, 0),               // no buyout: bid
            A(6, 2, 1),               // not valued
            A(7, 3, 1),               // blacklisted
            A(8, 99, 1),              // unknown item
        ], e => templates.GetValueOrDefault(e));
        Assert.Equal([(1u, 1500u), (3u, 3000u)], buys.Where(b => !b.Bid).Select(b => (b.AuctionId, b.Price)).ToArray());
        Assert.Equal([(2u, 1u), (5u, 1u)], buys.Where(b => b.Bid).Select(b => (b.AuctionId, b.Price)).ToArray());

        var noBids = new AuctionBotPlanner(Options(o => { o.ValueVariance = 0; o.BuyValuePercent = 100; o.Bidding = false; }), pricing,
            new AuctionBotItemPool(templates.Values, options, pricing), new Random(2));
        Assert.DoesNotContain(noBids.PlanBuy([A(2, 1, 2500), A(5, 1, 0)], e => templates.GetValueOrDefault(e)), b => b.Bid);
    }

    [Fact]
    public void PlanBuy_bids_the_next_outbid_step_and_never_over_its_own_bid()
    {
        var options = Options(o => { o.ValueVariance = 0; o.BuyValuePercent = 100; });
        var pricing = new AuctionBotPricing(options);
        ItemTemplate item = Item(1, itemClass: 0, buy: 1000); // value 2000
        var planner = new AuctionBotPlanner(options, pricing, new AuctionBotItemPool([item], options, pricing), new Random(3));
        AuctionRecord A(uint id, uint bid, int bidder, uint buyout = 5000, uint start = 100) => new()
        {
            Id = id, ItemEntry = 1, ItemCount = 1, SellerId = 5, StartBid = start, Buyout = buyout, Bid = bid, BidderId = bidder,
        };
        IReadOnlyList<AuctionBotBuyIntent> intents = planner.PlanBuy(
        [
            A(1, 1000, 9),            // a player's 1000: next is 1000 + 50
            A(2, 1000, 0),            // the bot's own standing bid: skipped
            A(3, 1950, 9),            // next 2045 is above the value: left
            A(4, 0, 0, buyout: 1500), // buyout below the value: bought out, not bid
            A(5, 0, 0, buyout: 2100, start: 2050), // the next bid is the 2050 start bid, above the value: left
        ], _ => item);
        Assert.Equal([(1u, 1050u, true), (4u, 1500u, false)], intents.Select(i => (i.AuctionId, i.Price, i.Bid)).ToArray());
    }

    [Fact]
    public void Item_overrides_block_reprice_and_add_items_whatever_the_filters()
    {
        var options = Options(o => { o.ValueVariance = 0; o.TemplatesPerSellMin = 0; o.TemplatesPerSellMax = 0; });
        var pricing = new AuctionBotPricing(options);
        ItemTemplate normal = Item(1, itemClass: 0, buy: 1000, stack: 20);
        ItemTemplate bop = Item(2, itemClass: 0, buy: 1000, bonding: 1); // filtered out of the pool
        ItemTemplate blocked = Item(3, itemClass: 0, buy: 1000);
        var templates = new Dictionary<uint, ItemTemplate> { [1] = normal, [2] = bop, [3] = blocked };
        var planner = new AuctionBotPlanner(options, pricing, new AuctionBotItemPool(templates.Values, options, pricing), new Random(4))
        {
            Overrides = new Dictionary<uint, AuctionBotItemOverride>
            {
                [1] = new(1, 7, 0, 1, 1),        // value 7 per item, normal sources
                [2] = new(2, 500, 100, 3, 3),    // always added, 3 of them, despite BoP
                [3] = new(3, 0, 0, 1, 1),        // never
            },
        };
        Assert.False(planner.Pool.Contains(2));
        IReadOnlyList<AuctionBotSellIntent> sell = planner.PlanSell(100, e => templates.GetValueOrDefault(e), [(1u, 25u), (3u, 1u)]);
        Assert.Equal([(1u, 20u, 140u), (1u, 5u, 35u), (2u, 1u, 500u), (2u, 1u, 500u), (2u, 1u, 500u)],
            sell.Select(s => (s.Entry, s.Count, s.Buyout)).ToArray());
        Assert.Equal(7u, planner.ValuePerItem(normal));
        Assert.Equal(0u, planner.ValuePerItem(blocked));

        AuctionRecord auction = new() { Id = 1, ItemEntry = 3, ItemCount = 1, SellerId = 5, StartBid = 1, Buyout = 1 };
        Assert.Empty(planner.PlanBuy([auction], e => templates.GetValueOrDefault(e)));
    }

    [Fact]
    public void Loot_config_parses_like_ParseLootConfig()
    {
        Assert.Equal([30, 35, 8, 12], AuctionBotOptions.ParseLootConfig(" 30, 35,  8, 12"));
        Assert.Equal([-10, 2, 1, 1], AuctionBotOptions.ParseLootConfig("-10,2,1,1"));
        Assert.Equal([5, 5, 0, 0], AuctionBotOptions.ParseLootConfig("9,5"));
        Assert.Equal([0, 0, 2, 2], AuctionBotOptions.ParseLootConfig("0,-3,4,2,99"));
        Assert.Equal([0, 0, 0, 0], AuctionBotOptions.ParseLootConfig(null));
    }

    [Fact]
    public void Loot_sources_split_creatures_by_rank_and_skip_quest_and_conditional_drops()
    {
        var rows = new List<(ArcaneCore.Kernel.WorldData.Loot.LootTableKind, ArcaneCore.Kernel.WorldData.Loot.LootStoreRow)>
        {
            (ArcaneCore.Kernel.WorldData.Loot.LootTableKind.Creature, new(100, 1, 100, 0, 2, 2)),
            (ArcaneCore.Kernel.WorldData.Loot.LootTableKind.Creature, new(100, 2, -100, 0, 1, 1)),     // quest drop
            (ArcaneCore.Kernel.WorldData.Loot.LootTableKind.Creature, new(100, 3, 100, 0, 1, 1, 7)),   // behind a condition
            (ArcaneCore.Kernel.WorldData.Loot.LootTableKind.Creature, new(200, 4, 100, 0, 1, 1)),
            (ArcaneCore.Kernel.WorldData.Loot.LootTableKind.Skinning, new(300, 5, 100, 0, 1, 1)),
        };
        var content = new ArcaneCore.Kernel.WorldData.Loot.LootContent(rows,
            [new(10, 100, 0, 0, 0), new(11, 200, 0, 0, 0)]);
        var options = Options(o =>
        {
            o.LootCreatureNormal = "1,1,1,1"; o.LootCreatureElite = "1,1,2,2"; o.LootSkinning = "0,0,0,0"; o.LootCreatureRare = "0,0,0,0";
            o.LootCreatureRareElite = "0,0,0,0"; o.LootCreatureWorldBoss = "0,0,0,0"; o.LootDisenchant = "0,0,0,0"; o.LootFishing = "0,0,0,0"; o.LootGameobject = "0,0,0,0";
        });
        var sources = new AuctionBotLootSources(content, e => e == 10 ? 0u : 1u, options, new Random(5));
        Assert.Equal([100u], sources.Sources.Single(s => s.Name == "creature normal").Tables);
        Assert.Equal([200u], sources.Sources.Single(s => s.Name == "creature elite").Tables);
        Assert.Equal([300u], sources.Sources.Single(s => s.Name == "skinning").Tables);
        Assert.Equal([(1u, 2u), (4u, 1u), (4u, 1u)], sources.Roll().ToArray());
    }

    [Fact]
    public void Ledger_round_trips_through_records_and_resumes_attempts_above_the_loaded_keys()
    {
        var options = Options(o => { o.DailyItemBudget = 10; o.DailyBuyBudgetCopper = 100_000; });
        var ledger = new AuctionBotCustodyLedger(options);
        const long now = 1_000_000;
        ledger.TryReserveListing(2, 40, 900, 1, 1, now);
        AuctionBotCustodyRow buy = ledger.TryReserveBuyout(2, 41, 901, 1, 1, 500, now)!;
        ledger.Resolve(buy.IdemKey, committed: false, now);
        (IReadOnlyList<AuctionBotCustodyRecord> rows, IReadOnlyList<string> deleted) = ledger.TakeChanges();
        Assert.Equal(2, rows.Count);
        Assert.Empty(deleted);
        Assert.False(ledger.HasChanges);

        var restarted = new AuctionBotCustodyLedger(options);
        Assert.Equal(1, restarted.Load([.. rows, new AuctionBotCustodyRecord("bad", 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]));
        Assert.Equal(41u, restarted.MaxAuctionId);
        Assert.Equal(AuctionBotCustodyState.Reserved, restarted.Find(AuctionBotCustodyLedger.ListingKey(40))!.State);
        Assert.Single(restarted.Pending);
        Assert.False(restarted.HasChanges);
        Assert.Null(restarted.TryReserveListing(2, 40, 902, 1, 1, now)); // the key survives the restart
        Assert.Equal(AuctionBotCustodyLedger.BuyoutKey(41, 2), restarted.TryReserveBuyout(2, 41, 901, 1, 1, 500, now)!.IdemKey);
        Assert.Equal(restarted.Find(AuctionBotCustodyLedger.BuyoutKey(41, 1))!.OperationId, buy.OperationId);
    }

    [Fact]
    public void Ledger_bid_rows_hold_budget_while_standing_and_free_it_when_outbid()
    {
        var ledger = new AuctionBotCustodyLedger(Options(o => o.DailyBuyBudgetCopper = 1000));
        const long now = 2_000_000;
        AuctionBotCustodyRow bid = ledger.TryReserveBid(2, 50, 900, 1, 1, 600, now)!;
        Assert.Null(ledger.TryReserveBid(2, 50, 900, 1, 1, 700, now));      // one live bid per auction
        Assert.Null(ledger.TryReserveBuyout(2, 50, 900, 1, 1, 300, now));   // nor a buyout over it
        Assert.Null(ledger.TryReserveBid(2, 51, 901, 1, 1, 500, now));      // 600 + 500 > 1000
        Assert.False(ledger.ReleaseBid(bid.IdemKey, now));                   // not committed yet
        Assert.True(ledger.Resolve(bid.IdemKey, committed: true, now));
        Assert.Equal(bid.IdemKey, ledger.StandingBid(50)!.IdemKey);
        Assert.Equal(600ul, ledger.Totals(now).CopperToday);
        Assert.True(ledger.ReleaseBid(bid.IdemKey, now));
        Assert.False(ledger.ReleaseBid(bid.IdemKey, now));
        Assert.Equal(0ul, ledger.Totals(now).CopperToday);
        AuctionBotCustodyRow again = ledger.TryReserveBid(2, 50, 900, 1, 1, 700, now)!;
        Assert.Equal(AuctionBotCustodyLedger.BidKey(50, 2), again.IdemKey);
        ledger.Resolve(again.IdemKey, committed: true, now);
        Assert.Empty(ledger.Audit());

        // A standing bid is never pruned; a settled (won) one is, once old.
        long later = now + (5 * AuctionBotCustodyLedger.SecondsPerDay);
        ledger.PruneTerminal(later);
        Assert.NotNull(ledger.Find(again.IdemKey));
        Assert.True(ledger.SettleBid(again.IdemKey));
        ledger.PruneTerminal(later);
        Assert.Null(ledger.Find(again.IdemKey));
        Assert.Contains(again.IdemKey, ledger.TakeChanges().Deleted);
    }

    [Fact]
    public void Ledger_reserves_each_key_and_item_once_and_resolves_once()
    {
        var ledger = new AuctionBotCustodyLedger(Options());
        const long now = 1_900_000_000;
        AuctionBotCustodyRow? row = ledger.TryReserveListing(2, 10, 500, 4000, 1, now);
        Assert.NotNull(row);
        Assert.Null(ledger.TryReserveListing(2, 10, 501, 4000, 1, now)); // same key
        Assert.Null(ledger.TryReserveListing(2, 11, 500, 4000, 1, now)); // same item GUID
        Assert.Equal(row.OperationId, AuctionBotCustodyLedger.OperationIdFor(AuctionBotCustodyLedger.ListingKey(10)));
        Assert.NotEqual(row.OperationId, AuctionBotCustodyLedger.OperationIdFor(AuctionBotCustodyLedger.ListingKey(11)));

        Assert.True(ledger.Resolve(row.IdemKey, committed: true, now));
        Assert.False(ledger.Resolve(row.IdemKey, committed: false, now)); // a duplicate completion changes nothing
        Assert.Equal(AuctionBotCustodyState.TerminalOk, ledger.Find(row.IdemKey)!.State);
        Assert.Empty(ledger.Audit());
    }

    [Fact]
    public void Ledger_rolled_back_listing_frees_its_item_but_not_its_key()
    {
        var ledger = new AuctionBotCustodyLedger(Options());
        AuctionBotCustodyRow row = ledger.TryReserveListing(2, 10, 500, 4000, 1, 100)!;
        Assert.True(ledger.Resolve(row.IdemKey, committed: false, 101));
        Assert.Null(ledger.TryReserveListing(2, 10, 600, 4000, 1, 102));
        Assert.NotNull(ledger.TryReserveListing(2, 12, 500, 4000, 1, 102));
        Assert.Equal(1u, ledger.Totals(102).ItemsToday);
    }

    [Fact]
    public void Ledger_daily_budgets_count_reserved_and_committed_rows_and_reset_each_day()
    {
        var ledger = new AuctionBotCustodyLedger(Options(o => { o.DailyItemBudget = 2; o.DailyBuyBudgetCopper = 1000; }));
        const long day = AuctionBotCustodyLedger.SecondsPerDay;
        long now = 20_000 * day;
        Assert.NotNull(ledger.TryReserveListing(2, 1, 1, 1, 1, now));
        AuctionBotCustodyRow second = ledger.TryReserveListing(2, 2, 2, 1, 1, now)!;
        Assert.Null(ledger.TryReserveListing(2, 3, 3, 1, 1, now)); // budget spent while both are only reserved
        ledger.Resolve(second.IdemKey, committed: false, now);
        Assert.NotNull(ledger.TryReserveListing(2, 3, 3, 1, 1, now)); // the rollback gave its slot back
        Assert.NotNull(ledger.TryReserveListing(2, 4, 4, 1, 1, now + day)); // a new day

        Assert.NotNull(ledger.TryReserveBuyout(2, 50, 9, 1, 1, 600, now));
        Assert.Null(ledger.TryReserveBuyout(2, 51, 9, 1, 1, 500, now)); // 1100 > 1000
        Assert.NotNull(ledger.TryReserveBuyout(2, 51, 9, 1, 1, 400, now));
        Assert.Equal(1000ul, ledger.Totals(now).CopperToday);
        Assert.Equal(1000ul, ledger.Totals(now).ReservedCopper);
        Assert.Empty(ledger.Audit());
    }

    [Fact]
    public void Ledger_allows_one_live_buyout_per_auction_and_a_new_attempt_key_after_rollback()
    {
        var ledger = new AuctionBotCustodyLedger(Options());
        AuctionBotCustodyRow first = ledger.TryReserveBuyout(2, 7, 70, 1, 1, 100, 1000)!;
        Assert.Null(ledger.TryReserveBuyout(2, 7, 70, 1, 1, 100, 1000));
        ledger.Resolve(first.IdemKey, committed: false, 1001);
        AuctionBotCustodyRow second = ledger.TryReserveBuyout(2, 7, 70, 1, 1, 100, 1002)!;
        Assert.NotEqual(first.IdemKey, second.IdemKey);
        Assert.NotEqual(first.OperationId, second.OperationId);
        ledger.Resolve(second.IdemKey, committed: true, 1003);
        Assert.Null(ledger.TryReserveBuyout(2, 7, 70, 1, 1, 100, 1004)); // bought: never again
    }

    [Fact]
    public void Ledger_unknown_rows_stay_reserved_until_reconciled()
    {
        var ledger = new AuctionBotCustodyLedger(Options());
        AuctionBotCustodyRow a = ledger.TryReserveListing(2, 1, 1, 1, 1, 10)!;
        AuctionBotCustodyRow b = ledger.TryReserveListing(2, 2, 2, 1, 1, 10)!;
        AuctionBotCustodyRow c = ledger.TryReserveListing(2, 3, 3, 1, 1, 10)!;
        int moved = ledger.Reconcile(row => row.IdemKey == a.IdemKey ? true : row.IdemKey == b.IdemKey ? false : null, 20);
        Assert.Equal(2, moved);
        Assert.Equal(AuctionBotCustodyState.TerminalOk, ledger.Find(a.IdemKey)!.State);
        Assert.Equal(AuctionBotCustodyState.TerminalBack, ledger.Find(b.IdemKey)!.State);
        Assert.Equal(AuctionBotCustodyState.Reserved, ledger.Find(c.IdemKey)!.State);
        Assert.Equal(1, ledger.Totals(20).ReservedItems);

        // Pruning forgets old terminal rows, never a reserved one.
        Assert.Equal(2, ledger.PruneTerminal(10 + (3 * AuctionBotCustodyLedger.SecondsPerDay)));
        Assert.NotNull(ledger.Find(c.IdemKey));
    }
}
