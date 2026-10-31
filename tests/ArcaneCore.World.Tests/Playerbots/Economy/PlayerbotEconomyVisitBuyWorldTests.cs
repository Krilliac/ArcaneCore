using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>
/// The auction visit: listing, upgrade buyouts, the buy cap, and the first reagent buys. Split from the former <c>PlayerbotEconomyWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotEconomyWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotEconomyVisitBuyWorldTests : PlayerbotEconomyWorldTestBase
{
    // --- the visit lists ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Visit_ListsTheMostValuableItemsUpToTheFreeSlots_AndTakesExactlyTheDeposit()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-listing", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            AuctionHouseEntry house = HouseOf(economy);
            ScenarioBot bot = await c.LoginAsync("Econlister");
            await c.GiveMoneyAsync(bot, 5000);
            ObjectGuid cloth = await c.GiveItemAsync(bot, LinenCloth);
            uint existing = await ListAsync(bot, cloth, 20, 60, 480);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            await c.GiveItemAsync(bot, BoltC, 1);
            uint moneyBefore = await bot.ReadAsync(p => p.Money);

            // Three listable stacks, one auction of the bot already in the house, a cap of three: two free slots, the two most valuable stacks.
            PlayerbotOptions options = Options(listChance: 100, maxListings: 3, budget: 0.25f);
            Driver driver = await AttachAsync(c, bot, options);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ItemTemplate a = Template(economy, BoltA);
            ItemTemplate b = Template(economy, BoltB);
            ulong self = bot.Guid.Value;
            uint depositA = AuctionHouseRules.Deposit(house, a.SellPrice, 3, PlayerbotAuctionPricing.DurationMinutes, economy.Options.AuctionDepositMin, economy.Options.AuctionRateDeposit);
            uint depositB = AuctionHouseRules.Deposit(house, b.SellPrice, 2, PlayerbotAuctionPricing.DurationMinutes, economy.Options.AuctionDepositMin, economy.Options.AuctionRateDeposit);
            float cutRate = economy.Options.AuctionRateCut;
            int markupA = options.Economy.ListMarkupPercentFor(a.Quality);
            int markupB = options.Economy.ListMarkupPercentFor(b.Quality);
            uint buyoutA = PlayerbotAuctionPricing.ListingPrice(a, 3, [], self, house, cutRate, markupA);
            uint buyoutB = PlayerbotAuctionPricing.ListingPrice(b, 2, [], self, house, cutRate, markupB);
            uint floorA = PlayerbotAuctionPricing.FloorPerUnit(a, house.CutPercent * cutRate, markupA);
            uint floorB = PlayerbotAuctionPricing.FloorPerUnit(b, house.CutPercent * cutRate, markupB);
            // Common trade goods: 500 % of the sell price (1000 and 750 per unit) beats the reference (500 and 375) and the vendor floor.
            ScenarioContext.ExpectEqual((1000u, 750u), (floorA, floorB), "the bot floor per unit of the two bolts");
            ScenarioContext.ExpectEqual((3000u, 1500u), (buyoutA, buyoutB), "the asks without a competitor: the floor times the count");
            ScenarioContext.Expect(buyoutA > 0 && buyoutB > 0 && depositA > 0 && depositB > 0, "the test items must be worth listing");

            (int listed, AuctionRecord? first, AuctionRecord? second, uint money, uint spent, int placed) = await c.ReadAsync(() =>
            {
                var own = economy.Auctions.Select(v => v.Auction).Where(x => x.SellerId == (int)bot.Guid.Low).ToList();
                return (own.Count, own.FirstOrDefault(x => x.ItemEntry == BoltA), own.FirstOrDefault(x => x.ItemEntry == BoltB),
                    bot.RequirePlayer().Money, driver.Economy.Spent, driver.Economy.ListingsPlaced);
            });
            ScenarioContext.ExpectEqual(3, listed, "auctions of the bot (the old one and two new ones)");
            ScenarioContext.ExpectEqual(2, placed, "listings the bot placed");
            ScenarioContext.Expect(first is not null && second is not null, "the two most valuable stacks are listed");
            ScenarioContext.ExpectEqual((buyoutA, PlayerbotAuctionPricing.StartBid(buyoutA, floorA, 3), depositA, 3u), (first!.Buyout, first.StartBid, first.Deposit, first.ItemCount), "stack A listing");
            ScenarioContext.ExpectEqual((buyoutB, PlayerbotAuctionPricing.StartBid(buyoutB, floorB, 2), depositB, 2u), (second!.Buyout, second.StartBid, second.Deposit, second.ItemCount), "stack B listing");
            ScenarioContext.ExpectEqual((buyoutA, buyoutB), (first.StartBid, second.StartBid), "no competitor: the start bid is lifted to the floor of the stack, which is the buyout");
            ScenarioContext.ExpectEqual(moneyBefore - depositA - depositB, money, "money after the visit: the deposits of the house, nothing else");
            ScenarioContext.ExpectEqual(depositA + depositB, spent, "what the visit counted as spent");
            ScenarioContext.ExpectEqual(0u, await bot.ReadAsync(p => p.Inventory.GetItemCount(BoltA) + p.Inventory.GetItemCount(BoltB)), "the listed stacks left the bags");
            await c.ExpectItemCountAsync(bot, BoltC, 1);
            ScenarioContext.Expect(existing != 0, "the pre-existing listing was made");

            // The house now counts three auctions of the bot: a second visit has no free slot and lists nothing.
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings after the second visit");
            ScenarioContext.ExpectEqual(money, await bot.ReadAsync(p => p.Money), "money after the second visit");
            await c.ExpectItemCountAsync(bot, BoltC, 1);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_ListsLootWhileTheBotSavesForSomethingBeyondItsMoney_ButBuysNothing()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-listing-saving", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            AuctionHouseEntry house = HouseOf(economy);
            ScenarioBot bot = await c.LoginAsync("Econsaver");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            uint moneyBefore = await bot.ReadAsync(p => p.Money);

            // The bot saves a million copper it does not have, and no floor lets it spend any of its money on purchases.
            PlayerbotOptions options = Options(listChance: 100, maxListings: 3, budget: 0.25f);
            options.Budget.DiscretionaryFloorFraction = 0f;
            Driver driver = await AttachAsync(c, bot, options);
            var budget = new PlayerbotBudget(bot.Session!, options);
            budget.Pin(new PlayerbotBudgetLine(PlayerbotSpendKind.Riding, 1_000_000));
            await c.ReadAsync(() => { driver.Economy.SpendBudget = budget; return true; });
            ScenarioContext.ExpectEqual(0u, await c.ReadAsync(() => budget.Spendable(PlayerbotSpendKind.Discretionary, bot.RequirePlayer())), "discretionary money while saving");

            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ItemTemplate a = Template(economy, BoltA);
            uint depositA = AuctionHouseRules.Deposit(house, a.SellPrice, 3, PlayerbotAuctionPricing.DurationMinutes, economy.Options.AuctionDepositMin, economy.Options.AuctionRateDeposit);
            ScenarioContext.Expect(depositA > 0, "the test item must be worth listing");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed by a saving bot");
            ScenarioContext.ExpectEqual(moneyBefore - depositA, await bot.ReadAsync(p => p.Money), "money after the visit: the deposit, nothing else");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    // --- the visit buys ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Visit_BuysOutTheUpgradeWithinTheBudget_PaysTheSellerAndCollectsTheItem()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-buying", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            AuctionHouseEntry house = HouseOf(economy);
            ScenarioBot seller = await c.LoginAsync("Econseller");
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(seller, 1000);
            ObjectGuid small = await c.GiveItemAsync(seller, SmallRing);
            ObjectGuid big = await c.GiveItemAsync(seller, BigRing);
            ScenarioContext.ExpectEqual(RingBuyout, BuyCap(economy, Template(economy, SmallRing)), "the ring's buy cap per unit at the defaults");
            uint smallId = await ListAsync(seller, small, RingStartBid, RingBuyout, 480);
            uint bigId = await ListAsync(seller, big, 1000, 5000, 480);
            uint smallDeposit = await c.ReadAsync(() => economy.FindAuction(smallId)!.Deposit);
            await c.GiveMoneyAsync(buyer, 4000);

            // Budget: a quarter of 4000 = 1000. The small ring costs 70 (exactly the buy cap) and is an upgrade; the great ring costs 5000, above the budget and the purse.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            (int purchases, uint spent, uint budget, int collected) = await c.ReadAsync(() =>
                (driver.Economy.PurchasesMade, driver.Economy.Spent, driver.Economy.Budget, driver.Economy.ItemsCollected));
            ScenarioContext.ExpectEqual(1, purchases, "purchases");
            ScenarioContext.ExpectEqual(1000u, budget, "visit budget");
            ScenarioContext.ExpectEqual(RingBuyout, spent, "spent");
            ScenarioContext.ExpectEqual(4000u - RingBuyout, await buyer.ReadAsync(p => p.Money), "money after the buyout");
            ScenarioContext.ExpectEqual(1, collected, "items collected from the mailbox");
            await c.ExpectItemCountAsync(buyer, SmallRing, 1);
            await c.ExpectItemCountAsync(buyer, BigRing, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(smallId) is null), "the bought auction left the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(bigId) is { BidderId: 0 }), "the great ring is still for sale, without a bidder");

            IReadOnlyList<MailRecord> sellerMail = await MailsAsync(c, seller);
            MailRecord proceeds = Assert.Single(sellerMail);
            ScenarioContext.ExpectEqual(MailMessageType.Auction, proceeds.MessageType, "the seller's letter");
            ScenarioContext.ExpectEqual(MailRules.AuctionSubject(SmallRing, AuctionMailAction.Successful), proceeds.Subject, "the seller's letter subject");
            ScenarioContext.ExpectEqual(AuctionHouseRules.Proceeds(RingBuyout, smallDeposit, AuctionHouseRules.Cut(house, RingBuyout)), proceeds.Money, "the seller's proceeds: bid + deposit - cut");
            ScenarioContext.ExpectEqual(0, (await MailsAsync(c, buyer)).Count, "the buyer's won letter was emptied and deleted");
            ScenarioContext.ExpectEqual(1, await ItemRowsAsync(c, small.Low), "the ring exists once");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNothing_WhenTheBudgetDoesNotCoverTheBuyout()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-budget", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot seller = await c.LoginAsync("Econseller");
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(seller, 1000);
            // 5% of 2000 = 100, the smallest budget that makes a trip. At 99 % of the floor 141 the cap is 139: the ring is within the cap,
            // above the budget, and the purse could pay it, so only the budget refuses it.
            PlayerbotOptions options = Options(listChance: 0, maxListings: 5, budget: 0.05f);
            options.Economy.BuyBelowAskPercent = 99;
            ScenarioContext.ExpectEqual(139u, BuyCap(economy, Template(economy, SmallRing), options.Economy), "the ring's buy cap per unit at 99 %");
            uint auction = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), 60, 139, 480);
            await c.GiveMoneyAsync(buyer, 2000);

            Driver driver = await AttachAsync(c, buyer, options);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases");
            ScenarioContext.ExpectEqual(2000u, await buyer.ReadAsync(p => p.Money), "money");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is not null), "the auction is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoUpgradeAboveTheBuyCap_EvenWithinTheBudgetAndThePurse_ButBuysOneAtTheCap()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-upgrade-buy-cap", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            AuctionHouseEntry house = HouseOf(economy);
            ScenarioBot seller = await c.LoginAsync("Econseller");
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(seller, 1000);
            await c.GiveMoneyAsync(buyer, 4000);
            ItemTemplate ring = Template(economy, SmallRing);
            var defaults = new PlayerbotEconomyOptions();
            uint floor = PlayerbotAuctionPricing.FloorPerUnit(ring, house.CutPercent * economy.Options.AuctionRateCut, defaults.ListMarkupPercentFor(ring.Quality));
            ScenarioContext.ExpectEqual((141u, RingBuyout), (floor, BuyCap(economy, ring)), "the ring's floor and buy cap per unit at the defaults");

            // One copper over the cap and the floor itself (what a bot asks): both inside the budget (a quarter of 4000) and the purse.
            uint overId = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout + 1, 480);
            uint askId = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, floor, 480);
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases above the cap");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money after the first visit");
            await c.ExpectItemCountAsync(buyer, SmallRing, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(overId) is { BidderId: 0 } && economy.FindAuction(askId) is { BidderId: 0 }), "both over-cap rings are still in the house");

            // A ring exactly at the cap joins them: the next visit buys that one and leaves the others alone.
            uint atCapId = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout, 480);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases after the second visit");
            ScenarioContext.ExpectEqual(4000u - RingBuyout, await buyer.ReadAsync(p => p.Money), "money after the second visit");
            await c.ExpectItemCountAsync(buyer, SmallRing, 1);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(atCapId) is null), "the ring at the cap left the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(overId) is { BidderId: 0 } && economy.FindAuction(askId) is { BidderId: 0 }), "the over-cap rings are still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    // --- the visit buys reagents -------------------------------------------------------------------------------------

    [Fact]
    public async Task Visit_BuysTheCheapestReagentAuctionThatCoversTheShortfall_AndCollectsIt()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-buying", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);

            // The bot lacks two linen per craft; its budget (a quarter of 4000 = 1000) covers the in-cap pair.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            (int reagents, int purchases, uint spent, int collected, bool held) = await c.ReadAsync(() =>
                (driver.Economy.ReagentPurchasesMade, driver.Economy.PurchasesMade, driver.Economy.Spent, driver.Economy.ItemsCollected,
                 driver.Economy.Bought.IsHeld(LinenCloth, buyer.Session!.World.NowMs)));
            ScenarioContext.ExpectEqual(1, reagents, "reagent purchases");
            ScenarioContext.ExpectEqual(0, purchases, "upgrade purchases");
            ScenarioContext.ExpectEqual(market.Within, spent, "spent");
            ScenarioContext.ExpectEqual(1, collected, "items collected from the mailbox");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money after the buyout");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is null), "the bought auction left the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.OverId) is { BidderId: 0 }), "the over-priced pair is still for sale");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.SingleId) is { BidderId: 0 }), "the single linen is still for sale");
            ScenarioContext.Expect(held, "the bought entry is held against listing");
            ScenarioContext.ExpectEqual(0, (await MailsAsync(c, buyer)).Count, "the buyer's won letter was emptied and deleted");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoReagent_WhenBuyReagentsIsOff()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-off", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            PlayerbotOptions options = Options(listChance: 0, maxListings: 5, budget: 0.25f);
            options.Economy.BuyReagents = false;
            Driver driver = await AttachAsync(c, buyer, options);
            await SetReagentNeedsAsync(c, driver);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases while off");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money while off");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is not null), "the in-cap pair is still in the house");

            // The same market, the same bot, the switch turned on: now the pair is bought (the off result was the switch, not the market).
            await c.ReadAsync(() => { options.Economy.BuyReagents = true; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases once on");
            ScenarioContext.ExpectEqual(4000u - market.Within, await buyer.ReadAsync(p => p.Money), "money once on");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoReagent_WhenTheEntryIsStockedByAVendor_EvenWithAnInCapCoveringRowListed()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-vendor-stocked", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The in-cap pair is a covering row the bot could afford, but a vendor sells linen: a player must not be able to sell it to bots at the house.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver, entry => entry == LinenCloth);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases of a vendor-stocked entry");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is { BidderId: 0 }), "the in-cap pair is still in the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.OverId) is { BidderId: 0 }), "the over-priced pair is still in the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.SingleId) is { BidderId: 0 }), "the single linen is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }
}
