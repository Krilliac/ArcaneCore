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
/// Reagent buyouts against the operation deadline, the hourly caps, and vendor stock. Split from the former <c>PlayerbotEconomyWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotEconomyWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotEconomyReagentVendorWorldTests : PlayerbotEconomyWorldTestBase
{
    [Fact]
    public async Task Visit_DoesNotBuyAReagentAgain_WhenItsPendingBidHitTheOperationDeadline()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-bid-deadline", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);
            uint nowOf() => buyer.Session!.World.NowMs;

            // The bot is told the settlement stays pending and gets no result packet, so only the operation deadline ends the wait. The
            // real settlement completes at once in the database (the money leaves, the letter arrives); the bot never sees it.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await c.ReadAsync(() => { driver.Economy.BidSettlementPendingOverride = _ => true; driver.Economy.DropBidResultForTest = true; return true; });
            driver.PauseWhen = e => e.Bought.IsAwaitingCollection(LinenCloth, nowOf());
            await StartVisitAsync(c, driver);
            await PaceAsync(c, "the deadline drops the visit and books the pending bid", () => !driver.Active, TimeSpan.FromSeconds(120));
            await c.ReadAsync(() => { driver.Economy.BidSettlementPendingOverride = null; driver.Economy.DropBidResultForTest = false; driver.PauseWhen = null; return true; });

            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "phase after the deadline");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "the dropped visit counted no purchase (its outcome was unknown)");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsAwaitingCollection(LinenCloth, nowOf())), "the pending bid was booked at the deadline");
            await PaceAsync(c, "the settlement of the bid lands", () => buyer.RequirePlayer().Money == moneyBefore - market.Within && !buyer.RequirePlayer().IsQuestSettlementPending);
            ScenarioContext.ExpectEqual(1, (await MailsAsync(c, buyer)).Count, "the won letter waits in the mailbox");

            // The seams are cleared: the next visit sees the settlement as it is, buys nothing and collects the letter.
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after the next visit (no second buy)");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is { BidderId: 0 })),
                "the other in-cap pair is still for sale");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money after the next visit: the first buyout, once");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_CountsAReagentBuyoutAgainstTheHourlyCaps_WhenItsSettlementEndsWithoutAResultPacket()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-reagent-no-result", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The result packet is discarded, so the buyout is found out from the money alone (not a house confirmation): it still counts.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await c.ReadAsync(() => { driver.Economy.DropBidResultForTest = true; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => { driver.Economy.DropBidResultForTest = false; driver.Active = false; return true; });

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.BuyoutsThisHour(buyer.Session!.World.NowMs)), "buyouts in the bot's hourly window");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => PlayerbotAuctionPace.For(economy).Buyouts(AuctionHouseRules.AllianceHouse, buyer.Session!.World.NowMs)),
                "buyouts the house counts");
        });
    }

    [Fact]
    public async Task Visit_CountsAReagentBuyoutAgainstTheHourlyCaps_WhenTheVisitIsDroppedWhileItsBidSettles()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-reagent-aborted", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The visit is reset in the tick that placed the bid, while the settlement is still pending: its outcome is unknown, yet it counts.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            driver.PauseWhen = e =>
            {
                if (e.Phase != PlayerbotEconomyPhase.AwaitBid) return false;
                e.Reset();
                return true;
            };
            await StartVisitAsync(c, driver);
            await PaceAsync(c, "the visit is reset while its reagent bid is pending", () => !driver.Active);
            await c.ReadAsync(() => { driver.PauseWhen = null; return true; });

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "the dropped visit counted no purchase");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.BuyoutsThisHour(buyer.Session!.World.NowMs)), "buyouts in the bot's hourly window");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => PlayerbotAuctionPace.For(economy).Buyouts(AuctionHouseRules.AllianceHouse, buyer.Session!.World.NowMs)),
                "buyouts the house counts");
        });
    }

    // --- the vendor-stocked check fails closed -----------------------------------------------------------------------

    [Fact]
    public async Task Visit_BuysNoReagent_WhenTheServiceIndexListsNoVendorStock_AndTheSeamIsNull()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-empty-index", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The scenario creature content has no vendor, so the index (if it is built at all) lists no stocked item: fail closed, every
            // entry counts as vendor stocked and the in-cap pair the bot could afford is left alone.
            (bool built, int stockedCount, bool linenStocked) = await IndexAsync(c, buyer);
            ScenarioContext.ExpectEqual(0, stockedCount, $"vendor-stocked items of the scenario world's service index (index built: {built})");
            ScenarioContext.Expect(!linenStocked, "the index does not list linen as vendor stocked");

            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver, pinVendorStocked: false);
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.VendorStocked is null), "the vendor-stocked seam is not pinned");
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases with an empty index");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is { BidderId: 0 }), "the in-cap pair is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysTheReagent_WhenARealVendorStocksADifferentItem_AndTheSeamIsNull()
    {
        await using ScenarioTestWorld world = await StartAsync(services => UseVendor(services, Filler));
        await RunAsync(world, "economy-reagent-real-index-other-item", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // A spawned vendor stocks the pebble, so the real index is non-empty and does not list linen: the pair is bought.
            (bool built, int stockedCount, bool linenStocked) = await IndexAsync(c, buyer);
            ScenarioContext.Expect(built, "the service index is built");
            ScenarioContext.ExpectEqual(1, stockedCount, "vendor-stocked items of the index");
            ScenarioContext.Expect(!linenStocked, "the index does not list linen as vendor stocked");

            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver, pinVendorStocked: false);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases");
            ScenarioContext.ExpectEqual(4000u - market.Within, await buyer.ReadAsync(p => p.Money), "money after the buyout");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is null), "the bought auction left the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoReagent_WhenARealVendorStocksIt_AndTheSeamIsNull()
    {
        await using ScenarioTestWorld world = await StartAsync(services => UseVendor(services, LinenCloth));
        await RunAsync(world, "economy-reagent-real-index-stocked", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            (bool built, int stockedCount, bool linenStocked) = await IndexAsync(c, buyer);
            ScenarioContext.Expect(built, "the service index is built");
            ScenarioContext.ExpectEqual(1, stockedCount, "vendor-stocked items of the index");
            ScenarioContext.Expect(linenStocked, "the index lists linen as vendor stocked");

            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver, pinVendorStocked: false);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases of an entry a vendor stocks");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is { BidderId: 0 }), "the in-cap pair is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_NeverListsAReagentTheBotNeeds_WhenTheDefaultReaderFindsItInTheSpellbook()
    {
        const uint Potion = 990330, Recipe = 996101;
        await using ScenarioTestWorld world = await StartAsync(services =>
        {
            var items = (InMemoryItemTemplateSource)services.Last(d => d.ServiceType == typeof(IItemTemplateSource)).ImplementationInstance!;
            items.Templates.Add(new ItemTemplate { Entry = Potion, Name = "Scenario Potion", Class = (uint)ItemClass.Consumable, InventoryType = (uint)InventoryType.NonEquip,
                Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        });
        await RunAsync(world, "economy-reagent-keep-spellbook", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econcrafter");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, LinenCloth, 3);
            await c.GiveItemAsync(bot, BoltA, 3);

            // The bot really knows a recipe (two linen cloth makes a potion it carries none of); ReagentNeeds stays null, so the economy reads the spellbook.
            bool wanted = await c.ReadAsync(() =>
            {
                SpellFeature feature = c.Services.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([new SpellInfo
                {
                    Id = Recipe,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Potion }, new(), new()],
                    Reagents = [new SpellReagent(LinenCloth, 2)],
                    Totems = [0, 0],
                }], [], []);
                feature.Spellbook.LearnSpell(bot.RequirePlayer(), Recipe);
                var needs = new PlayerbotVendorSupplyNeeds();
                needs.Refresh(bot.RequirePlayer(), feature.System.Store, feature.Spellbook);
                return needs.Reagents.TryGetValue(LinenCloth, out uint perCraft) && perCraft == 2;
            });
            ScenarioContext.Expect(wanted, "precondition: the spellbook read wants two linen per craft");

            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.ReagentNeeds is null), "the default reader is in use");
            await c.ReadAsync(() => { driver.Economy.VendorStocked = _ => false; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == BoltA)), "BoltA auctions of the bot");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == LinenCloth)), "linen auctions of the bot");
            await c.ExpectItemCountAsync(bot, LinenCloth, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }
}
