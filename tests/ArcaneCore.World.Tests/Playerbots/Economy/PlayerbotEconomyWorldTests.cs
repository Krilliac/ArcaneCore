using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
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
/// The economy bot (<see cref="PlayerbotEconomy"/>) in a real scenario world (docs/areas/playbots-economy.md): the ordinary world thread, the real
/// auction and mail handlers, a SQLite character database and a Stormwind auctioneer two yards from the start (<see cref="ScenarioTestContent.UseAuctioneer"/>).
/// A bot's economy is driven by a controller that calls <see cref="PlayerbotEconomy.Update"/> once per world tick, exactly as the brain does; the
/// seller, the buyer of a sale and the mail senders are scripted scenario bots using the ordinary client packets.
/// </summary>
public sealed class PlayerbotEconomyWorldTests
{
    private const uint BoltA = 990301;
    private const uint BoltB = 990302;
    private const uint BoltC = 990303;
    private const uint SmallRing = 990310;
    private const uint BigRing = 990311;
    private const uint Filler = 990320;

    private static ObjectGuid Auctioneer => ObjectGuid.WithEntry(HighGuid.Unit, AuctioneerEntry, AuctioneerSpawn);

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
            uint buyoutA = PlayerbotAuctionPricing.ListingPrice(a, 3, [], self, house);
            uint buyoutB = PlayerbotAuctionPricing.ListingPrice(b, 2, [], self, house);
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
            ScenarioContext.ExpectEqual((buyoutA, PlayerbotAuctionPricing.StartBid(buyoutA), depositA, 3u), (first!.Buyout, first.StartBid, first.Deposit, first.ItemCount), "stack A listing");
            ScenarioContext.ExpectEqual((buyoutB, PlayerbotAuctionPricing.StartBid(buyoutB), depositB, 2u), (second!.Buyout, second.StartBid, second.Deposit, second.ItemCount), "stack B listing");
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
            uint smallId = await ListAsync(seller, small, 100, 400, 480);
            uint bigId = await ListAsync(seller, big, 1000, 5000, 480);
            uint smallDeposit = await c.ReadAsync(() => economy.FindAuction(smallId)!.Deposit);
            await c.GiveMoneyAsync(buyer, 4000);

            // Budget: a quarter of 4000 = 1000. The small ring costs 400 and is an upgrade; the great ring costs 5000, above the budget and the purse.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            (int purchases, uint spent, uint budget, int collected) = await c.ReadAsync(() =>
                (driver.Economy.PurchasesMade, driver.Economy.Spent, driver.Economy.Budget, driver.Economy.ItemsCollected));
            ScenarioContext.ExpectEqual(1, purchases, "purchases");
            ScenarioContext.ExpectEqual(1000u, budget, "visit budget");
            ScenarioContext.ExpectEqual(400u, spent, "spent");
            ScenarioContext.ExpectEqual(3600u, await buyer.ReadAsync(p => p.Money), "money after the buyout");
            ScenarioContext.ExpectEqual(1, collected, "items collected from the mailbox");
            await c.ExpectItemCountAsync(buyer, SmallRing, 1);
            await c.ExpectItemCountAsync(buyer, BigRing, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(smallId) is null), "the bought auction left the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(bigId) is { BidderId: 0 }), "the great ring is still for sale, without a bidder");

            IReadOnlyList<MailRecord> sellerMail = await MailsAsync(c, seller);
            MailRecord proceeds = Assert.Single(sellerMail);
            ScenarioContext.ExpectEqual(MailMessageType.Auction, proceeds.MessageType, "the seller's letter");
            ScenarioContext.ExpectEqual(MailRules.AuctionSubject(SmallRing, AuctionMailAction.Successful), proceeds.Subject, "the seller's letter subject");
            ScenarioContext.ExpectEqual(AuctionHouseRules.Proceeds(400, smallDeposit, AuctionHouseRules.Cut(house, 400)), proceeds.Money, "the seller's proceeds: bid + deposit - cut");
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
            uint auction = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), 100, 400, 480);
            await c.GiveMoneyAsync(buyer, 4000);

            // 5% of 4000 = 200: enough for a trip (at least 100) but not for the 400 buyout, though the purse could pay it.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.05f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is not null), "the auction is still in the house");
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
    public async Task Visit_NeverListsAReagentTheBotNeeds()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-keep", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econkeeper");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, LinenCloth, 3);
            await c.GiveItemAsync(bot, BoltA, 3);

            // Linen is a trade good with a vendor price, listable like BoltA, but a wanted recipe uses it (two per craft; the bot carries three).
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == BoltA)), "BoltA auctions of the bot");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == LinenCloth)), "linen auctions of the bot");
            await c.ExpectItemCountAsync(bot, LinenCloth, 3);
            await c.ExpectItemCountAsync(bot, BoltA, 0);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_DoesNotBuyAReagentAgain_WhileTheBoughtOneWaitsInTheMailbox()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-awaiting", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);
            uint nowOf() => buyer.Session!.World.NowMs;

            // Visit 1 buys one in-cap pair and is cut off (from inside the tick, on the world thread) when it reaches the mail step: the won
            // letter, which holds the linen, stays in the mailbox.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            driver.PauseWhen = e =>
            {
                if (e.Phase != PlayerbotEconomyPhase.MailStart || e.ReagentPurchasesMade != 1) return false;
                e.Reset();
                return true;
            };
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("visit 1 is cut off at the mail step", () => !driver.Active);
            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "phase after the cut");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after visit 1");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ItemsCollected), "nothing collected yet");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money after the first buyout");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.ExpectEqual(1, (await MailsAsync(c, buyer)).Count, "the won letter waits in the mailbox");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsAwaitingCollection(LinenCloth, nowOf())), "the linen is awaiting collection");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is not null)),
                "exactly one of the two in-cap pairs was bought");

            // Visit 2: the bot still carries no linen, but the bought pair is on its way, so it is not a shortfall: the second pair stays in the house.
            await c.ReadAsync(() => { driver.PauseWhen = null; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after visit 2 (no second buy)");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is { BidderId: 0 })),
                "the other in-cap pair is still for sale");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money after visit 2: only the first buyout");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ItemsCollected), "visit 2 collected the won letter");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            ScenarioContext.Expect(!await c.ReadAsync(() => driver.Economy.Bought.IsAwaitingCollection(LinenCloth, nowOf())), "collected: no longer awaited");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsHeld(LinenCloth, nowOf())), "still held against listing");
            ScenarioContext.ExpectEqual(0, (await MailsAsync(c, buyer)).Count, "the won letter was emptied and deleted");
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
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == BoltA)), "BoltA auctions of the bot");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == LinenCloth)), "linen auctions of the bot");
            await c.ExpectItemCountAsync(bot, LinenCloth, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    private sealed record ReagentMarket(uint Cap, uint Within, uint WithinId, uint OverId, uint SingleId, uint ExtraId = 0);

    /// <summary>Per craft the bot wants two linen cloth (set on the world thread, the seam the economy reads instead of the spellbook).</summary>
    private static Task SetReagentNeedsAsync(ScenarioContext c, Driver driver) => c.ReadAsync(() =>
    {
        driver.Economy.ReagentNeeds = _ => new Dictionary<uint, uint> { [LinenCloth] = 2 };
        return true;
    });

    /// <summary>
    /// A seller lists linen cloth three ways: a pair exactly at the reagent cap per unit (the one to buy), a pair one copper per unit over the cap,
    /// and a single cheap cloth that does not cover a shortfall of two.
    /// </summary>
    private static async Task<ReagentMarket> SeedReagentMarketAsync(ScenarioContext c, EconomyFeature economy, bool secondInCapPair = false)
    {
        ScenarioBot seller = await c.LoginAsync("Econseller");
        await c.GiveMoneyAsync(seller, 3000);
        uint cap = PlayerbotEconomyPlanner.ReagentUnitCap(Template(economy, LinenCloth));
        uint within = 2 * cap;
        uint over = (2 * cap) + 2;
        ScenarioContext.Expect(cap > 0 && over < 1000, $"the linen cap {cap} must leave the over-priced pair inside the 1000 copper visit budget");

        // One stack at a time: a second stack of the same entry would merge into the first one in the bags.
        uint withinId = await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), within / 2, within, 480);
        uint overId = await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), over / 2, over, 480);
        uint singleId = await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 1), 10, cap / 4 + 1, 480);
        uint extraId = secondInCapPair ? await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), within / 2, within, 480) : 0;
        return new ReagentMarket(cap, within, withinId, overId, singleId, extraId);
    }

    // --- the visit collects ------------------------------------------------------------------------------------------

    /// <summary>
    /// The same visit under both client fidelities, with the scenario tap taken off the bot so its ring is the only transport: the mail
    /// replies the economy reads (MSG_QUERY_NEXT_MAIL_TIME, SMSG_MAIL_LIST_RESULT, SMSG_SEND_MAIL_RESULT) must reach it in Lean as in Full.
    /// </summary>
    [Theory]
    [InlineData(PlayerbotClientFidelity.Lean)]
    [InlineData(PlayerbotClientFidelity.Full)]
    public async Task Visit_CollectsSoldAndExpiredLetters_AndLeavesCodAndPlayerLettersExactlyAsTheyAre(PlayerbotClientFidelity fidelity)
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-mail-" + fidelity, async c =>
        {
            c.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlayerbotOptions>>().Value.ClientFidelity = fidelity;
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econmail");
            ScenarioBot other = await c.LoginAsync("Econother");
            Letters seeded = await SeedLettersAsync(c, economy, bot, other, foreign: true);

            IReadOnlyList<MailRecord> before = await MailsAsync(c, bot);
            ScenarioContext.ExpectEqual(4, before.Count, "letters in the box before the visit (sale, expiry, cash on delivery, player)");
            MailRecord sale = Assert.Single(before, m => m.Subject == MailRules.AuctionSubject(BoltA, AuctionMailAction.Successful));
            MailRecord expiry = Assert.Single(before, m => m.Subject == MailRules.AuctionSubject(BoltB, AuctionMailAction.Expired));
            MailRecord cod = Assert.Single(before, m => m.Cod == 50);
            MailRecord text = Assert.Single(before, m => m.Subject == "hello");
            ScenarioContext.ExpectEqual(seeded.SaleProceeds, sale.Money, "the sale letter pays bid + deposit - cut");
            ScenarioContext.Expect(expiry.HasItem && cod.HasItem && sale.Cod == 0 && expiry.Cod == 0, "the expiry letter returns the item; the cash on delivery letter holds one");
            uint moneyBefore = await bot.ReadAsync(p => p.Money);

            Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5, budget: 0f));
            await c.ReadAsync(() => bot.Session!.ManagedPacketObserver = null);
            ScenarioContext.ExpectEqual(fidelity == PlayerbotClientFidelity.Full, bot.Session!.WantsUpdateObjects, "update blocks are built for the bot only in Full");
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            (long collectedMoney, int collectedItems, int remembered) = await c.ReadAsync(() =>
                (driver.Economy.MoneyCollected, driver.Economy.ItemsCollected, driver.Economy.Relist.Count));
            ScenarioContext.ExpectEqual((long)sale.Money, collectedMoney, "money collected");
            ScenarioContext.ExpectEqual(1, collectedItems, "items collected");
            ScenarioContext.ExpectEqual(1, remembered, "the expiry is remembered (the sale forgot its entry)");
            ScenarioContext.ExpectEqual(moneyBefore + sale.Money, await bot.ReadAsync(p => p.Money), "money after the visit");
            await c.ExpectItemCountAsync(bot, BoltB, 1);

            IReadOnlyList<MailRecord> after = await MailsAsync(c, bot);
            ScenarioContext.ExpectEqual(2, after.Count, "letters left in the box: " + string.Join(" | ", after.Select(m => $"{m.Subject} money {m.Money} item {m.ItemEntry} cod {m.Cod}")));
            ScenarioContext.Expect(after.Contains(cod) && after.Contains(text), "the cash on delivery letter and the player's letter are exactly as they were (record equality: money, item, cod, read flags)");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_WithFullBags_TakesTheMoneyButLeavesTheItemLetter()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-mail-full-bags", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econfull");
            ScenarioBot other = await c.LoginAsync("Econother");
            Letters seeded = await SeedLettersAsync(c, economy, bot, other, foreign: false);
            int filled = await c.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                int n = 0;
                while (n < 200 && player.Inventory.AddItem(Filler, 1, out _) == InventoryResult.Ok) n++;
                return n;
            });
            ScenarioContext.Expect(filled > 0 && await c.ReadAsync(() => PlayerbotTownGoals.FreeBagSlots(bot.RequirePlayer())) == 0, "the bags are full");
            uint moneyBefore = await bot.ReadAsync(p => p.Money);

            Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5, budget: 0f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(moneyBefore + seeded.SaleProceeds, await bot.ReadAsync(p => p.Money), "the sale's money is taken without a bag slot");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ItemsCollected), "items collected");
            await c.ExpectItemCountAsync(bot, BoltB, 0);
            IReadOnlyList<MailRecord> left = await MailsAsync(c, bot);
            MailRecord letter = Assert.Single(left);
            ScenarioContext.ExpectEqual(MailRules.AuctionSubject(BoltB, AuctionMailAction.Expired), letter.Subject, "the letter left in the box");
            ScenarioContext.Expect(letter.HasItem && letter.ItemEntry == BoltB, "the returned item is still attached");
            ScenarioContext.ExpectEqual(1, await ItemRowsAsync(c, letter.ItemGuid), "the returned item exists once");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    // --- two bots, one auction ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoBotsBuyingTheSameAuction_BuyItOnce_AndTheLoserKeepsItsMoney()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-race", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot seller = await c.LoginAsync("Econseller");
            await c.GiveMoneyAsync(seller, 1000);
            ObjectGuid ring = await c.GiveItemAsync(seller, SmallRing);
            uint auction = await ListAsync(seller, ring, 100, 400, 480);
            uint itemGuid = ring.Low;

            Race race = await RaceAsync(c, economy, auction);
            ScenarioContext.ExpectEqual(2, race.BidsSent, "both bots sent their bid (the contention was real)");
            ScenarioContext.ExpectEqual(1, race.Winners.Count, "bots that bought the auction");
            ScenarioContext.ExpectEqual(4000u - 400u, race.Winners[0].Money, "the winner paid the buyout once");
            ScenarioContext.ExpectEqual(4000u, race.Loser.Money, "the loser kept all its money");
            ScenarioContext.ExpectEqual(0, race.Loser.Purchases, "the loser's purchases");
            await ExpectItemOnceAsync(c, economy, auction, itemGuid, race);

            IReadOnlyList<MailRecord> sellerMail = await MailsAsync(c, seller);
            MailRecord proceeds = Assert.Single(sellerMail);
            ScenarioContext.ExpectEqual(MailRules.AuctionSubject(SmallRing, AuctionMailAction.Successful), proceeds.Subject, "exactly one sale letter for the seller");
        });
    }

    [Fact]
    public async Task TwoBotsBuyingTheSameSellerlessListing_BuyItOnce_TheItemExistsOnce_AndTheAhBotLedgerAuditIsClean()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-race-seller-zero", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            var ledger = new AuctionBotCustodyLedger(new AuctionBotOptions());
            await c.LoginAsync("Econseed"); // the item templates load with the first character
            ScenarioContext.Expect(economy.Templates.Find(SmallRing) is not null, "the item templates are not loaded");
            (uint auction, uint itemGuid) = await c.ReadAsync(() =>
            {
                ItemTemplate template = Template(economy, SmallRing);
                uint id = economy.NextAuctionId();
                var item = new ItemInstanceData { Guid = economy.ItemGuids!.Next(), Entry = template.Entry, Count = 1, Durability = template.MaxDurability, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
                AuctionBotCustodyRow row = ledger.TryReserveListing(AuctionHouseRules.AllianceHouse, id, item.Guid, item.Entry, item.Count, economy.Now)
                    ?? throw new ScenarioAssertionException("the ledger refused the listing");
                economy.StartBotListing(AuctionHouseRules.AllianceHouse, id, item, 100, 400, economy.Now + (8 * 3600), row.OperationId,
                    outcome => ledger.Resolve(row.IdemKey, outcome == EconomyOutcome.After, economy.Now));
                return (id, item.Guid);
            });
            await c.WaitUntilAsync("the seller-less listing is committed", () => ledger.Find(AuctionBotCustodyLedger.ListingKey(auction))?.State == AuctionBotCustodyState.TerminalOk);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is { SellerId: 0 }), "the listing is a seller-less (AH bot) auction");

            Race race = await RaceAsync(c, economy, auction);
            ScenarioContext.ExpectEqual(2, race.BidsSent, "both bots sent their bid (the contention was real)");
            ScenarioContext.ExpectEqual(1, race.Winners.Count, "bots that bought the auction");
            ScenarioContext.ExpectEqual(4000u - 400u, race.Winners[0].Money, "the winner paid the buyout once");
            ScenarioContext.ExpectEqual(4000u, race.Loser.Money, "the loser kept all its money");
            await ExpectItemOnceAsync(c, economy, auction, itemGuid, race);

            // A seller-less listing has no seller to pay: no letter was minted for owner 0 and the two bots' boxes hold nothing.
            ScenarioContext.Expect(await c.ReadAsync(() => ledger.Audit().Count == 0), "AuctionBotCustodyLedger.Audit(): " + string.Join("; ", ledger.Audit()));
            AuctionBotCustodyRow only = Assert.Single(ledger.Rows);
            ScenarioContext.ExpectEqual((AuctionBotCustodyRole.Listing, AuctionBotCustodyState.TerminalOk, itemGuid), (only.Role, only.State, only.ItemGuid), "the one ledger row is the AH bot's own listing");
            ScenarioContext.ExpectEqual(0, ledger.Rows.Count(r => r.Role != AuctionBotCustodyRole.Listing), "a player buyout adds no row to the AH bot's ledger");
        });
    }

    // --- aborts and timeouts -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ListingWhoseCommitOutlastsTheOperationTimeout_DropsTheVisit_AndTheLateCommitIsNotRepeated()
    {
        var gate = new Gate();
        await using ScenarioTestWorld world = await StartAsync(services => services.AddScoped<IEconomyStore>(sp => new GateStore(gate, new EfEconomyStore(sp.GetRequiredService<CharacterDbContext>()))));
        await RunAsync(world, "economy-listing-timeout", async c =>
        {
            try
            {
                EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
                // The held commit must outlive the bot's 30 s operation deadline, which the scenario clock spends in a few wall seconds; the
                // 5 s wall settlement budget would otherwise cancel it first and the late commit would never land.
                economy.Options.SettlementBudgetSeconds = 300;
                ScenarioBot bot = await c.LoginAsync("Econslow");
                await c.GiveMoneyAsync(bot, 5000);
                await c.GiveItemAsync(bot, BoltA, 3);
                uint moneyBefore = await bot.ReadAsync(p => p.Money);
                Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
                gate.Arm();
                await StartVisitAsync(c, driver);
                await c.WaitUntilAsync("the listing waits on its commit", () => driver.Economy.Phase == PlayerbotEconomyPhase.AwaitSell && gate.Held == 1);

                // The settlement is still pending when the 30 s deadline passes (the commit is held in wall time): the visit is dropped.
                await c.WaitUntilAsync("the visit is dropped at the deadline", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle, TimeSpan.FromSeconds(60));
                ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
                ScenarioContext.Expect(await bot.ReadAsync(p => p.IsQuestSettlementPending), "the settlement is still pending");

                // While the settlement is pending the bot starts nothing (Settlements.CanAct), even when a visit is requested.
                await c.ReadAsync(() => { driver.Economy.RequestVisit(); return true; });
                await c.IdleAsync(TimeSpan.FromSeconds(5));
                ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "no visit starts beside a pending settlement");
                ScenarioContext.ExpectEqual(1, gate.Held, "commits started");

                gate.Release();
                await c.WaitUntilAsync("the late commit lands", () => !bot.RequirePlayer().IsQuestSettlementPending);
                await c.ReadAsync(() => driver.Active = false);
                AuctionHouseEntry house = HouseOf(economy);
                ItemTemplate a = Template(economy, BoltA);
                uint deposit = AuctionHouseRules.Deposit(house, a.SellPrice, 3, PlayerbotAuctionPricing.DurationMinutes, economy.Options.AuctionDepositMin, economy.Options.AuctionRateDeposit);
                ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "the committed listing exists once");
                ScenarioContext.ExpectEqual(moneyBefore - deposit, await bot.ReadAsync(p => p.Money), "the deposit was taken once");
                await c.ExpectItemCountAsync(bot, BoltA, 0);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    [Fact]
    public async Task MailTakeWhoseCommitOutlastsTheOperationTimeout_DropsTheVisit_AndTheNextVisitFinishesWithoutPayingTwice()
    {
        var gate = new Gate();
        await using ScenarioTestWorld world = await StartAsync(services => services.AddScoped<IEconomyStore>(sp => new GateStore(gate, new EfEconomyStore(sp.GetRequiredService<CharacterDbContext>()))));
        await RunAsync(world, "economy-mail-timeout", async c =>
        {
            try
            {
                EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
                // The held commit must outlive the bot's 30 s operation deadline, which the scenario clock spends in a few wall seconds; the
                // 5 s wall settlement budget would otherwise cancel it first and the late commit would never land.
                economy.Options.SettlementBudgetSeconds = 300;
                ScenarioBot bot = await c.LoginAsync("Econmail");
                ScenarioBot other = await c.LoginAsync("Econother");
                Letters seeded = await SeedLettersAsync(c, economy, bot, other, foreign: false);
                uint moneyBefore = await bot.ReadAsync(p => p.Money);
                Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5, budget: 0f));
                // The box lists the newest letter first: the expiry's item is taken (one commit) and its emptied letter deleted (a second) before the
                // sale letter's money take, the third commit, which is the one held.
                gate.Arm(skip: 2);
                await StartVisitAsync(c, driver);
                // Paced, not raced: the two commits before the held one run on real time, and the racing clock would spend the bot's 10 s
                // delete and 30 s take deadlines in a fraction of a wall second under load, dropping the visit before it reaches the held take.
                await PaceAsync(c, "the money take waits on its commit", () => driver.Economy.Phase == PlayerbotEconomyPhase.AwaitMailResult && gate.Held == 1);
                await c.WaitUntilAsync("the visit is dropped at the deadline", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle, TimeSpan.FromSeconds(60));
                ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
                ScenarioContext.ExpectEqual(0L, await c.ReadAsync(() => driver.Economy.MoneyCollected), "nothing counted as collected");

                gate.Release();
                await PaceAsync(c, "the late take pays", () => bot.RequirePlayer().Money == moneyBefore + seeded.SaleProceeds);
                ScenarioContext.ExpectEqual(moneyBefore + seeded.SaleProceeds, await bot.ReadAsync(p => p.Money), "the late take paid once");

                // The next visit reads the box again: the sale letter the late take emptied is only deleted, no second payment.
                await StartVisitAsync(c, driver);
                await FinishAsync(c, driver, 1);
                ScenarioContext.ExpectEqual(moneyBefore + seeded.SaleProceeds, await bot.ReadAsync(p => p.Money), "money after the next visit");
                ScenarioContext.ExpectEqual(0L, await c.ReadAsync(() => driver.Economy.MoneyCollected), "the emptied letter paid nothing more");
                await c.ExpectItemCountAsync(bot, BoltB, 1);
                ScenarioContext.ExpectEqual(0, (await MailsAsync(c, bot)).Count, "letters left in the box");
                await c.ReadAsync(() => driver.Active = false);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    [Fact]
    public async Task VisitThatWaitedPastTheVisitCap_IsDroppedBeforeItSendsAnotherRequest()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-visit-cap", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econcap");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            driver.PauseWhen = e => e.Phase == PlayerbotEconomyPhase.OwnerList;
            var sent = new List<WorldOpcode>();
            await c.ReadAsync(() => { bot.Session!.ManagedDispatchObserver = sent.Add; return true; });
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("the visit is under way and paused", () => !driver.Active);
            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.OwnerList, await c.ReadAsync(() => driver.Economy.Phase), "phase at the pause");

            await c.IdleAsync(TimeSpan.FromMilliseconds(PlayerbotEconomy.VisitCapMs + 1_000));
            driver.PauseWhen = null;
            await c.ReadAsync(() => driver.Active = true);
            await c.WaitUntilAsync("the visit is dropped", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
            ScenarioContext.Expect(!sent.Contains(WorldOpcode.CmsgAuctionListOwnerItems) && !sent.Contains(WorldOpcode.CmsgAuctionSellItem),
                "the dropped visit asked the house for nothing: " + string.Join(", ", sent));
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "auctions of the bot");
            await c.ExpectItemCountAsync(bot, BoltA, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task VisitRunningWhenTheFeatureIsSwitchedOff_IsDropped_WithoutAnotherRequest()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-disabled", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econoff");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            PlayerbotOptions options = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            Driver driver = await AttachAsync(c, bot, options);
            driver.PauseWhen = e => e.Phase == PlayerbotEconomyPhase.OwnerList;
            var sent = new List<WorldOpcode>();
            await c.ReadAsync(() => { bot.Session!.ManagedDispatchObserver = sent.Add; return true; });
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("the visit is under way and paused", () => !driver.Active);

            await c.ReadAsync(() => { options.Economy.Enabled = false; driver.PauseWhen = null; driver.Active = true; return true; });
            await c.WaitUntilAsync("the visit is dropped", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle);
            await c.IdleAsync(TimeSpan.FromSeconds(2));
            ScenarioContext.Expect(!sent.Contains(WorldOpcode.CmsgAuctionListOwnerItems), "a switched-off economy asked the house for nothing: " + string.Join(", ", sent));
            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "no new visit while it is off");
            await c.ExpectItemCountAsync(bot, BoltA, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Reset_DropsAVisitUnderWay()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reset", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econreset");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            driver.PauseWhen = e => e.Phase == PlayerbotEconomyPhase.OwnerList;
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("the visit is under way and paused", () => !driver.Active);
            await c.ReadAsync(() => { driver.Economy.Reset(); return true; });
            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "phase after Reset");
            ScenarioContext.ExpectEqual(0u, await c.ReadAsync(() => driver.Economy.TargetEntry), "the target entry is cleared");
        });
    }

    [Fact]
    public async Task VisitFarFromTheAuctioneerOverUnknownTerrain_GivesUpAfterThreeFailedWalks()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-no-route", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econlost");
            await c.PlaceAsync(bot, 0, StartX - 30f, StartY, StartZ);
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("the walks fail and the visit is dropped", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle && driver.Ticks > 3);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "auctions of the bot");
            await c.ExpectItemCountAsync(bot, BoltA, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_WalksToTheAuctioneerOverFlatGround_ThenLists()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await world.UseFlatFloorAsync();
        await RunAsync(world, "economy-walks", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econwalk");
            await c.PlaceAsync(bot, 0, StartX - 30f, StartY, StartZ);
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1, TimeSpan.FromSeconds(120));
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "auctions of the bot");
            ScenarioContext.Expect(await bot.ReadAsync(p => MathF.Abs(p.X - (StartX + 2f)) < 6f), "the bot walked to the auctioneer");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    // --- finding the auctioneer and the mailbox ----------------------------------------------------------------------

    [Fact]
    public async Task Destinations_FindTheFactionAuctioneerAndTheMailbox_ByLiveSightingThenByStaticHint()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-destinations", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econseek");
            PlayerbotOptions options = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            await c.StepAsync("beside the auctioneer and the mailbox", async () =>
            {
                (PlayerbotAuctioneerSighting? auctioneer, PlayerbotMailboxSighting? mailbox) = await c.ReadAsync(() =>
                {
                    var destinations = new PlayerbotEconomyDestinations(bot.Session!, options);
                    return (destinations.FindAuctioneer(bot.RequirePlayer(), economy), destinations.FindMailbox(bot.RequirePlayer()));
                });
                ScenarioContext.Expect(auctioneer is not null, "no auctioneer in sight");
                ScenarioContext.ExpectEqual(AuctioneerEntry, auctioneer!.Npc.Entry, "auctioneer entry");
                ScenarioContext.Expect(auctioneer.Interactable, "the auctioneer is within reach");
                ScenarioContext.ExpectEqual(AuctionHouseRules.AllianceHouse, auctioneer.House.Id, "the faction house, not the neutral one");
                ScenarioContext.Expect(mailbox is not null, "no mailbox in sight");
                ScenarioContext.ExpectEqual(Mailbox.Value, mailbox!.Guid.Value, "mailbox guid");
                ScenarioContext.Expect(mailbox.Interactable, "the mailbox is within reach");
            });

            await c.StepAsync("a goblin (neutral) auctioneer is no destination and no hint", async () =>
            {
                uint faction = await c.ReadAsync(() => new PlayerbotEconomyDestinations(bot.Session!, options).FindAuctioneer(bot.RequirePlayer(), economy)!.Npc.FactionId);
                (PlayerbotAuctioneerSighting? seen, bool stepped, int hints) = await c.ReadAsync(() =>
                {
                    economy.Options.NeutralAuctioneerFactions.Add(faction);
                    try
                    {
                        var destinations = new PlayerbotEconomyDestinations(bot.Session!, options);
                        Player player = bot.RequirePlayer();
                        return (destinations.FindAuctioneer(player, economy), destinations.StepToHint(player, economy, 100, bot.Session!.World.NowMs), destinations.HintCount);
                    }
                    finally
                    {
                        economy.Options.NeutralAuctioneerFactions.Remove(faction);
                    }
                });
                ScenarioContext.Expect(seen is null, "a neutral auctioneer was used");
                ScenarioContext.Expect(!stepped && hints == 0, "a neutral auctioneer became a hint");
            });

            await c.StepAsync("far away: still seen, not within reach, and the static hint names it", async () =>
            {
                await c.PlaceAsync(bot, 0, StartX - 30f, StartY, StartZ);
                (PlayerbotAuctioneerSighting? auctioneer, PlayerbotMailboxSighting? mailbox, bool walked, int hints, uint target) = await c.ReadAsync(() =>
                {
                    var destinations = new PlayerbotEconomyDestinations(bot.Session!, options);
                    Player player = bot.RequirePlayer();
                    PlayerbotAuctioneerSighting? seen = destinations.FindAuctioneer(player, economy);
                    PlayerbotMailboxSighting? box = destinations.FindMailbox(player);
                    // No terrain data in this world: no route, so the walker cannot start (the hint is still known).
                    bool step = seen is not null && destinations.WalkTo(player, seen.Position, 100);
                    destinations.StepToHint(player, economy, 100, bot.Session!.World.NowMs);
                    return (seen, box, step, destinations.HintCount, destinations.TargetEntry);
                });
                ScenarioContext.Expect(auctioneer is { Interactable: false }, "the far auctioneer should be seen but not within reach");
                ScenarioContext.Expect(mailbox is { Interactable: false }, "the far mailbox should be seen but not within reach");
                ScenarioContext.Expect(!walked, "a walk started over unknown terrain");
                ScenarioContext.ExpectEqual(1, hints, "static hints on the map");
                ScenarioContext.ExpectEqual(AuctioneerEntry, target, "the hint's auctioneer entry");
            });
        });
    }

    // --- harness -----------------------------------------------------------------------------------------------------

    private static Task<ScenarioTestWorld> StartAsync(Action<IServiceCollection>? more = null) => ScenarioTestWorld.StartAsync(services =>
    {
        var items = (InMemoryItemTemplateSource)services.Last(d => d.ServiceType == typeof(IItemTemplateSource)).ImplementationInstance!;
        items.Templates.AddRange(
        [
            Bolt(BoltA, "Scenario Bolt A", 200),
            Bolt(BoltB, "Scenario Bolt B", 150),
            Bolt(BoltC, "Scenario Bolt C", 100),
            PlayerbotEquipmentTests.Ring(SmallRing, 5) with { Name = "Scenario Ring", Quality = 2, SellPrice = 40, BuyPrice = 200 },
            PlayerbotEquipmentTests.Ring(BigRing, 20) with { Name = "Scenario Great Ring", Quality = 2, SellPrice = 40, BuyPrice = 200 },
            new ItemTemplate { Entry = Filler, Name = "Scenario Pebble", Class = 15, Quality = 0, Stackable = 1, SellPrice = 1 },
        ]);
        UseAuctioneer(services);
        more?.Invoke(services);
    });

    /// <summary>A trade goods stack: reference price well above the vendor price (a quarter of the buy price), so a listing pays.</summary>
    private static ItemTemplate Bolt(uint entry, string name, uint sell)
        => new() { Entry = entry, Name = name, Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 20, SellPrice = sell, BuyPrice = sell * 10 };

    private static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
        => await world.RunPassingAsync(new Script(name, body));

    private static PlayerbotOptions Options(int listChance, int maxListings, float budget) => new()
    {
        Enabled = true,
        Economy = new PlayerbotEconomyOptions { ListChancePerVisit = listChance, MaxListingsPerBot = maxListings, BuyUpgradeBudgetFraction = budget, VisitIntervalMinutes = 30 },
    };

    private static AuctionHouseEntry HouseOf(EconomyFeature economy) => economy.Options.AuctionHouses.Single(h => h.Id == AuctionHouseRules.AllianceHouse);

    private static ItemTemplate Template(EconomyFeature economy, uint entry) => economy.Templates.Find(entry) ?? throw new ScenarioAssertionException($"no template {entry}");

    /// <summary>The scripted bot sells an item through the ordinary packet and returns the auction id.</summary>
    private static async Task<uint> ListAsync(ScenarioBot bot, ObjectGuid item, uint startBid, uint buyout, uint minutes)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionSellItem, ScenarioAuctionWire.Sell(Auctioneer.Value, item.Value, startBid, buyout, minutes)), "sell refused");
        AuctionCommandResultView started = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult, r => r.Action == AuctionAction.Started, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, started.Error, $"{bot.Name} listing result");
        return started.AuctionId;
    }

    /// <summary>
    /// Wait for a condition while the world runs slowly: 50 ms of game time per 5 ms or more of wall time. The scenario clock normally spends
    /// 30 s of game time in well under a second, far faster than a database commit; the bot's own 30 s operation deadline and the economy
    /// settlement budget run on game time, so racing the clock would drop visits whose commit is merely slow.
    /// </summary>
    private static async Task PaceAsync(ScenarioContext c, string what, Func<bool> done, TimeSpan? wall = null)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan limit = wall ?? TimeSpan.FromSeconds(60);
        while (!await c.ReadAsync(done))
        {
            if (clock.Elapsed > limit) throw new ScenarioTimeoutException($"timed out waiting for: {what} ({clock.Elapsed.TotalSeconds:F1}s wall)");
            await c.IdleAsync(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5, c.CancellationToken);
        }
    }

    /// <summary>Wait until the driver has finished <paramref name="visits"/> visits; a timeout names the phase the economy is stuck in.</summary>
    private static async Task FinishAsync(ScenarioContext c, Driver driver, int visits, TimeSpan? timeout = null)
    {
        try
        {
            await PaceAsync(c, $"visit {visits} finishes", () => driver.Economy.VisitsFinished >= visits, timeout);
        }
        catch (ScenarioTimeoutException timedOut)
        {
            string state = await c.ReadAsync(() => $"phase {driver.Economy.Phase}, finished {driver.Economy.VisitsFinished}, ticks {driver.Ticks}, active {driver.Active}, spent {driver.Economy.Spent}, listed {driver.Economy.ListingsPlaced}, bought {driver.Economy.PurchasesMade}");
            throw new ScenarioAssertionException(timedOut.Message + "; " + state);
        }
    }

    private static async Task BuyOutAsync(ScenarioBot bot, uint auction, uint price)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionPlaceBid, ScenarioAuctionWire.PlaceBid(Auctioneer.Value, auction, price)), "bid refused");
        AuctionCommandResultView placed = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult, r => r.Action == AuctionAction.BidPlaced, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, placed.Error, $"{bot.Name} buyout result");
    }

    private static async Task<IReadOnlyList<MailRecord>> MailsAsync(ScenarioContext c, ScenarioBot bot)
    {
        await using AsyncServiceScope scope = c.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEconomyStore>().GetMailsAsync((int)bot.Guid.Low);
    }

    private static async Task<int> ItemRowsAsync(ScenarioContext c, uint itemGuid)
    {
        await using AsyncServiceScope scope = c.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CharacterDbContext>().Set<ItemInstanceRow>().AsNoTracking().CountAsync(i => i.Guid == itemGuid);
    }

    private static async Task<Driver> AttachAsync(ScenarioContext c, ScenarioBot bot, PlayerbotOptions options)
    {
        var driver = new Driver(bot, options);
        if (!await c.Services.GetRequiredService<ManagedPlayerbotFeature>().SetControllerAsync(bot.BotId, driver))
            throw new ScenarioAssertionException($"{bot.Name} could not be handed to the economy driver");
        return driver;
    }

    private static Task StartVisitAsync(ScenarioContext c, Driver driver) => c.ReadAsync(() =>
    {
        driver.Economy.RequestVisit();
        driver.Active = true;
        return true;
    });

    /// <summary>
    /// Mail for <paramref name="bot"/>: it sells three <see cref="BoltA"/> (bought out by <paramref name="other"/>: a sale letter) and lists one
    /// <see cref="BoltB"/> for two hours that expires (an expiry letter with the item); with <paramref name="foreign"/> <paramref name="other"/>
    /// also sends it a text letter and a letter with an item and cash on delivery.
    /// </summary>
    private static async Task<Letters> SeedLettersAsync(ScenarioContext c, EconomyFeature economy, ScenarioBot bot, ScenarioBot other, bool foreign)
    {
        AuctionHouseEntry house = HouseOf(economy);
        await c.GiveMoneyAsync(bot, 2000);
        await c.GiveMoneyAsync(other, 5000);
        ObjectGuid sold = await c.GiveItemAsync(bot, BoltA, 3);
        ObjectGuid unsold = await c.GiveItemAsync(bot, BoltB, 1);
        uint soldId = await ListAsync(bot, sold, 300, 600, 480);
        uint expiringId = await ListAsync(bot, unsold, 500, 900, 120);
        uint deposit = await c.ReadAsync(() => economy.FindAuction(soldId)!.Deposit);
        await BuyOutAsync(other, soldId, 600);
        if (foreign)
        {
            ObjectGuid cloth = await c.GiveItemAsync(other, LinenCloth);
            long mark = other.Mark();
            ScenarioContext.Expect(await other.SendMailAsync(Mailbox, bot.Name, "hello", "just words"), "text letter refused");
            await other.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult, r => r.Action == MailAction.Send, mark);
            mark = other.Mark();
            ScenarioContext.Expect(await other.SendMailAsync(Mailbox, bot.Name, "pay me", "cash on delivery", cloth, 0, 50), "cash on delivery letter refused");
            await other.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult, r => r.Action == MailAction.Send, mark);
        }

        // Three hours pass: the two-hour listing expires (its item comes back by mail) and the letters with items are delivered.
        ((ScenarioTimeProvider)c.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromHours(3));
        await c.ReadAsync(() =>
        {
            economy.RunExpirySweep();
            return true;
        });
        await c.WaitUntilAsync("the expired listing is settled", () => economy.FindAuction(expiringId) is null);
        return new Letters(AuctionHouseRules.Proceeds(600, deposit, AuctionHouseRules.Cut(house, 600)));
    }

    /// <summary>
    /// Two fresh bots with 4000 copper each start a visit on the same world tick against the auction <paramref name="auction"/>; returns when both
    /// visits finished.
    /// </summary>
    private static async Task<Race> RaceAsync(ScenarioContext c, EconomyFeature economy, uint auction)
    {
        ScenarioBot first = await c.LoginAsync("Econracea");
        ScenarioBot second = await c.LoginAsync("Econraceb");
        await c.GiveMoneyAsync(first, 4000);
        await c.GiveMoneyAsync(second, 4000);
        Driver a = await AttachAsync(c, first, Options(listChance: 0, maxListings: 5, budget: 0.25f));
        Driver b = await AttachAsync(c, second, Options(listChance: 0, maxListings: 5, budget: 0.25f));
        int bids = 0;
        await c.ReadAsync(() =>
        {
            first.Session!.ManagedDispatchObserver = op => bids += op == WorldOpcode.CmsgAuctionPlaceBid ? 1 : 0;
            second.Session!.ManagedDispatchObserver = op => bids += op == WorldOpcode.CmsgAuctionPlaceBid ? 1 : 0;
            a.Economy.RequestVisit();
            b.Economy.RequestVisit();
            a.Active = true;
            b.Active = true;
            return true;
        });
        await PaceAsync(c, "both visits finish", () => a.Economy.VisitsFinished == 1 && b.Economy.VisitsFinished == 1);
        await c.ReadAsync(() =>
        {
            a.Active = false;
            b.Active = false;
            return true;
        });
        ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is null), "the auction left the house");
        Result[] results = await c.ReadAsync(() => new[]
        {
            new Result(first, first.RequirePlayer().Money, a.Economy.PurchasesMade),
            new Result(second, second.RequirePlayer().Money, b.Economy.PurchasesMade),
        });
        return new Race(results.Where(r => r.Purchases > 0).ToList(), results.Length == 2 && results[0].Purchases == 0 ? results[0] : results[1], await c.ReadAsync(() => bids), [first, second]);
    }

    /// <summary>The item exists exactly once: one durable row, held by exactly one of the bots (bags or an uncollected letter), the auction gone.</summary>
    private static async Task ExpectItemOnceAsync(ScenarioContext c, EconomyFeature economy, uint auction, uint itemGuid, Race race)
    {
        ScenarioContext.ExpectEqual(1, await ItemRowsAsync(c, itemGuid), "durable rows of the item");
        int held = 0;
        int inLetters = 0;
        int heldByLoser = 0;
        foreach (ScenarioBot bot in race.Bots)
        {
            int mine = await bot.ReadAsync(p => p.Inventory.AllItems.Count(i => i.Guid.Low == itemGuid));
            held += mine;
            if (bot == race.Loser.Bot) heldByLoser = mine;
            inLetters += (await MailsAsync(c, bot)).Count(m => m.ItemGuid == itemGuid);
        }

        ScenarioContext.ExpectEqual(1, held + inLetters, "holders of the item (bags and letters)");
        ScenarioContext.ExpectEqual(1, held, "the winner collected the item from its won letter");
        ScenarioContext.ExpectEqual(0, heldByLoser, "items held by the loser");
        ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is null), "the auction is gone");
    }

    private sealed record Letters(uint SaleProceeds);

    private sealed record Result(ScenarioBot Bot, uint Money, int Purchases);

    private sealed record Race(List<Result> Winners, Result Loser, int BidsSent, ScenarioBot[] Bots);

    private sealed class Script(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => "PlayerbotEconomyWorldTests: " + name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>Calls <see cref="PlayerbotEconomy.Update"/> once per world tick for one scripted bot, the way the brain does (world thread only).</summary>
    private sealed class Driver : IPlayerbotController
    {
        public Driver(ScenarioBot bot, PlayerbotOptions options)
        {
            Economy = new PlayerbotEconomy(bot.Session!, options) { RandomSource = () => new Random(7) };
        }

        public PlayerbotEconomy Economy { get; }

        public bool Active { get; set; }

        public int Ticks { get; private set; }

        /// <summary>Stop driving (world thread) once this holds after an update.</summary>
        public Func<PlayerbotEconomy, bool>? PauseWhen { get; set; }

        void IPlayerbotController.Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (!Active || context.Player is not { } player) return;
            Ticks++;
            Economy.Update(player, elapsedMs);
            if (PauseWhen?.Invoke(Economy) == true) Active = false;
        }

        void IPlayerbotController.Detached(Guid botId)
        {
        }
    }

    /// <summary>
    /// Holds one commit until <see cref="Release"/> (before it reaches the database, so a held commit has changed nothing): the first after
    /// <see cref="Arm"/>, or with <c>skip</c> the one after that many commits have passed through.
    /// </summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _skip;
        private int _held;

        public int Held => Volatile.Read(ref _held);

        public void Arm(int skip = 0)
        {
            Volatile.Write(ref _skip, skip);
            Volatile.Write(ref _armed, 1);
        }

        public void Release() => _release.TrySetResult();

        public async Task PassAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0) return;
            if (Interlocked.Decrement(ref _skip) >= 0) return;
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            Interlocked.Increment(ref _held);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class GateStore(Gate gate, IEconomyStore inner) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            await gate.PassAsync(cancellationToken).ConfigureAwait(false);
            return await inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.IsCommittedAsync(operationId, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default) => inner.GetMailsAsync(receiverId, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default) => inner.GetExpiredMailsAsync(now, max, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetMailsInvolvingAsync(characterId, cancellationToken);

        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default) => inner.GetItemTextAsync(itemTextId, cancellationToken);

        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default) => inner.GetAuctionsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
            => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);

        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default) => inner.GetAuctionSnapshotAsync(filter, cancellationToken);

        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => inner.GetIdSeedAsync(cancellationToken);
    }
}
