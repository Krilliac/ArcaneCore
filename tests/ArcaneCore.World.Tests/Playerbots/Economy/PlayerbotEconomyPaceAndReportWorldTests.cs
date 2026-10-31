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
/// Walking to the auctioneer, destinations, the money goal, the hourly pace caps, and the economy report. Split from the former <c>PlayerbotEconomyWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotEconomyWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotEconomyPaceAndReportWorldTests : PlayerbotEconomyWorldTestBase
{
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

    // --- the money goal, the hourly caps and the surplus consumables ------------------------------------------------

    [Fact]
    public async Task Visit_MoneyGoal_ListsEveryVisitAndBeyondTheUsualCap_WhenTheBotSavesForMoreThanItHas()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-money-goal", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econgoal");
            await c.GiveMoneyAsync(bot, 20_000);
            foreach ((uint entry, uint count) in new (uint, uint)[] { (BoltA, 3), (BoltB, 2), (BoltD, 2), (BoltE, 2), (BoltF, 2) })
                await c.GiveItemAsync(bot, entry, count);

            // A 1% chance would almost never list (the driver seeds Random(7); the precondition keeps the test honest about it), and the usual cap is two.
            ScenarioContext.Expect(new Random(7).Next(100) >= 1, "precondition: the seeded roll must fail a 1% chance");
            PlayerbotOptions options = Options(listChance: 1, maxListings: 2, budget: 0.25f);
            options.Budget.DiscretionaryFloorFraction = 0f;
            Driver driver = await AttachAsync(c, bot, options);
            var budget = new PlayerbotBudget(bot.Session!, options);
            budget.Pin(new PlayerbotBudgetLine(PlayerbotSpendKind.Riding, 1_000_000));
            await c.ReadAsync(() => { driver.Economy.SpendBudget = budget; return true; });

            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(5, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed: 2 slots + 3 for the money goal");
            ScenarioContext.ExpectEqual(5, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "auctions of the bot");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.MoneyGoalActive), "the money goal is active");
            string report = await c.ReadAsync(() => driver.Economy.Report(bot.Session!.World.NowMs));
            ScenarioContext.Expect(report.Contains("moneygoal=on", StringComparison.Ordinal), "the report names the active goal: " + report);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_MoneyGoalOff_KeepsTheUsualRollAndCap_WhateverTheBotSavesFor()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-money-goal-off", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econgoaloff");
            await c.GiveMoneyAsync(bot, 20_000);
            await c.GiveItemAsync(bot, BoltA, 3);

            ScenarioContext.Expect(new Random(7).Next(100) >= 1, "precondition: the seeded roll must fail a 1% chance");
            PlayerbotOptions options = Options(listChance: 1, maxListings: 2, budget: 0.25f);
            options.Economy.MoneyGoal = false;
            options.Budget.DiscretionaryFloorFraction = 0f;
            Driver driver = await AttachAsync(c, bot, options);
            var budget = new PlayerbotBudget(bot.Session!, options);
            budget.Pin(new PlayerbotBudgetLine(PlayerbotSpendKind.Riding, 1_000_000));
            await c.ReadAsync(() => { driver.Economy.SpendBudget = budget; return true; });

            // The bot has nothing else to do (the roll fails, the budget reserves its money from buying), so no visit starts: the decision
            // not to visit is what is waited for, and the visit counter stays at 0.
            await StartVisitAsync(c, driver);
            await PaceAsync(c, "the visit decision",
                () => driver.Ticks > 0 && driver.Economy.Phase == PlayerbotEconomyPhase.Idle && !driver.Economy.Report(bot.Session!.World.NowMs).Contains("next=due", StringComparison.Ordinal));
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "visits finished: the failed roll starts no visit");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed with the money goal off");
            ScenarioContext.Expect(!await c.ReadAsync(() => driver.Economy.MoneyGoalActive), "the money goal is not active");
            await c.ExpectItemCountAsync(bot, BoltA, 3);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task AuctionPace_BotCap_StopsListingAtMaxListingsPerHour_AndListsAgainAnHourLater()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-bot-listings", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econpacel");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltD, 2);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            PlayerbotOptions options = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            options.Economy.MaxListingsPerHour = 2;
            Driver driver = await AttachAsync(c, bot, options);

            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings of the first visit (the hourly cap is two)");
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsThisHour(bot.Session!.World.NowMs)), "listings in the window");
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)bot.Guid.Low)), "auctions of the bot");

            // A second visit inside the hour lists nothing more (the window is full) though a stack is left.
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings after the second visit");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => bot.RequirePlayer().Inventory.AllItems.Count(i => i.Entry is BoltA or BoltB or BoltD)), "one stack is still in the bags");

            // An hour of world time later the window has emptied and the last stack is listed. The driver stops while the clock races: left
            // running it would start (and count) visits of its own, and visit 3 below would be satisfied by one of those.
            await c.ReadAsync(() => driver.Active = false);
            await c.IdleAsync(TimeSpan.FromMinutes(61));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 3);
            ScenarioContext.ExpectEqual(3, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings after the hour");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => bot.RequirePlayer().Inventory.AllItems.Count(i => i.Entry is BoltA or BoltB or BoltD)), "no stack is left in the bags");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task AuctionPace_BotCap_StopsBuyingAtMaxBuyoutsPerHour_AndBuysAgainAnHourLater()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-bot-buyouts", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot seller = await c.LoginAsync("Econpaces");
            ScenarioBot buyer = await c.LoginAsync("Econpaceb");
            await c.GiveMoneyAsync(seller, 3000);
            uint first = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout, 480);
            uint second = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout, 480);
            await c.GiveMoneyAsync(buyer, 4000);
            PlayerbotOptions options = Options(listChance: 0, maxListings: 5, budget: 0.25f);
            options.Economy.MaxBuyoutsPerHour = 1;
            Driver driver = await AttachAsync(c, buyer, options);
            int Remaining() => new[] { first, second }.Count(id => economy.FindAuction(id) is not null);

            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases of the first visit");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.BuyoutsThisHour(buyer.Session!.World.NowMs)), "buyouts in the window");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(Remaining), "auctions left in the house");

            // Inside the hour the cap holds: the second visit (kept alive by a stack to list) buys nothing.
            await c.GiveItemAsync(buyer, BoltA, 3);
            await c.ReadAsync(() => { options.Economy.ListChancePerVisit = 100; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases after the second visit");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(Remaining), "the other ring is still for sale");

            // An hour later the window has emptied and the second ring is bought. The driver stops while the clock races: left running it
            // would start (and count) visits of its own, and visit 3 below would be satisfied by one of those.
            await c.ReadAsync(() => driver.Active = false);
            await c.IdleAsync(TimeSpan.FromMinutes(61));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 3);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.PurchasesMade), "purchases after the hour");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(Remaining), "no ring is left in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task AuctionPace_HouseCap_StopsASecondBotListing()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-house-listings", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot botA = await c.LoginAsync("Econhousea");
            ScenarioBot botB = await c.LoginAsync("Econhouseb");
            foreach (ScenarioBot bot in new[] { botA, botB })
            {
                await c.GiveMoneyAsync(bot, 5000);
                await c.GiveItemAsync(bot, BoltA, 3);
            }

            PlayerbotOptions optionsA = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            PlayerbotOptions optionsB = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            optionsA.Economy.HouseListingsPerHour = 1;
            optionsB.Economy.HouseListingsPerHour = 1;
            Driver driverA = await AttachAsync(c, botA, optionsA);
            Driver driverB = await AttachAsync(c, botB, optionsB);

            await StartVisitAsync(c, driverA);
            await FinishAsync(c, driverA, 1);
            await c.ReadAsync(() => driverA.Active = false);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driverA.Economy.ListingsPlaced), "listings of the first bot");

            await StartVisitAsync(c, driverB);
            await FinishAsync(c, driverB, 1);
            await c.ReadAsync(() => driverB.Active = false);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driverB.Economy.ListingsPlaced), "listings of the second bot: the house is at its cap");
            await c.ExpectItemCountAsync(botB, BoltA, 3);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => economy.Auctions.Count(v => v.Auction.SellerId == (int)botB.Guid.Low)), "auctions of the second bot");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => PlayerbotAuctionPace.For(economy).Listings(AuctionHouseRules.AllianceHouse, botB.Session!.World.NowMs)), "listings the house counts");
        });
    }

    [Fact]
    public async Task AuctionPace_HouseCap_StopsASecondBotBuying()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-house-buyouts", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot seller = await c.LoginAsync("Econhsell");
            ScenarioBot botA = await c.LoginAsync("Econhbuya");
            ScenarioBot botB = await c.LoginAsync("Econhbuyb");
            await c.GiveMoneyAsync(seller, 3000);
            uint first = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout, 480);
            uint second = await ListAsync(seller, await c.GiveItemAsync(seller, SmallRing), RingStartBid, RingBuyout, 480);
            await c.GiveMoneyAsync(botA, 4000);
            await c.GiveMoneyAsync(botB, 4000);
            PlayerbotOptions optionsA = Options(listChance: 0, maxListings: 5, budget: 0.25f);
            PlayerbotOptions optionsB = Options(listChance: 0, maxListings: 5, budget: 0.25f);
            optionsA.Economy.HouseBuyoutsPerHour = 1;
            optionsB.Economy.HouseBuyoutsPerHour = 1;
            Driver driverA = await AttachAsync(c, botA, optionsA);
            Driver driverB = await AttachAsync(c, botB, optionsB);

            await StartVisitAsync(c, driverA);
            await FinishAsync(c, driverA, 1);
            await c.ReadAsync(() => driverA.Active = false);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driverA.Economy.PurchasesMade), "purchases of the first bot");

            await StartVisitAsync(c, driverB);
            await FinishAsync(c, driverB, 1);
            await c.ReadAsync(() => driverB.Active = false);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driverB.Economy.PurchasesMade), "purchases of the second bot: the house is at its cap");
            ScenarioContext.ExpectEqual(4000u, await botB.ReadAsync(p => p.Money), "money of the second bot");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { first, second }.Count(id => economy.FindAuction(id) is not null)), "one ring is still for sale");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => PlayerbotAuctionPace.For(economy).Buyouts(AuctionHouseRules.AllianceHouse, botB.Session!.World.NowMs)), "buyouts the house counts");
        });
    }

    [Fact]
    public async Task AuctionPace_KeepsHousesAndKindsApart_AndIsOnePerEconomyFeature()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-pace-unit", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            (bool same, bool listA, bool listB, bool buyA, int listingsA, int buyoutsA, int listingsB, bool buyAtZero, bool listAtZero, bool freed) = await c.ReadAsync(() =>
            {
                PlayerbotAuctionPace pace = PlayerbotAuctionPace.For(economy);
                const uint houseA = 2, houseB = 6, now = 10_000;
                pace.NoteListing(houseA, now, 1);
                return (ReferenceEquals(pace, PlayerbotAuctionPace.For(economy)), pace.MayList(houseA, now, 1), pace.MayList(houseB, now, 1),
                    pace.MayBuy(houseA, now, 1), pace.Listings(houseA, now), pace.Buyouts(houseA, now), pace.Listings(houseB, now),
                    pace.MayBuy(houseA, now, 0), pace.MayList(houseB, now, 0), pace.MayList(houseA, now + PlayerbotHourlyWindow.WindowMs, 1));
            });
            ScenarioContext.Expect(same, "For() returns the same pace for one economy feature");
            ScenarioContext.Expect(!listA, "house A is at its listing limit");
            ScenarioContext.Expect(listB, "house B has its own listing window");
            ScenarioContext.Expect(buyA, "buyouts and listings are counted apart");
            ScenarioContext.ExpectEqual((1, 0, 0), (listingsA, buyoutsA, listingsB), "counts per house and kind");
            ScenarioContext.Expect(!buyAtZero && !listAtZero, "a limit of 0 allows nothing");
            ScenarioContext.Expect(freed, "the stamp is free an hour later");
        });
    }

    [Fact]
    public async Task Visit_ListsASurplusConsumableStack_ButKeepsTheKeepCount()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-surplus-consumable", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econsurplus");
            await c.GiveMoneyAsync(bot, 5000);
            // Three full stacks of five elixirs (the bot does not use them): 15 held.
            for (int i = 0; i < 3; i++) await c.GiveItemAsync(bot, Elixir, 5);
            ScenarioContext.ExpectEqual(3, await c.ReadAsync(() => bot.RequirePlayer().Inventory.AllItems.Count(i => i.Entry == Elixir)), "stacks given");

            PlayerbotOptions options = Options(listChance: 100, maxListings: 5, budget: 0.25f);
            options.Economy.ConsumableKeepCount = 5;
            options.Economy.ListSurplusConsumables = false;
            Driver driver = await AttachAsync(c, bot, options);

            // The switch off: consumables stay in the bags, as before this change.
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings with the surplus rule off");
            await c.ExpectItemCountAsync(bot, Elixir, 15);

            // The switch on, keep 5: two stacks are surplus, exactly the keep count stays.
            await c.ReadAsync(() => { options.Economy.ListSurplusConsumables = true; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings with the surplus rule on");
            await c.ExpectItemCountAsync(bot, Elixir, 5);
            ScenarioContext.ExpectEqual(10, await c.ReadAsync(() => economy.Auctions.Where(v => v.Auction.SellerId == (int)bot.Guid.Low && v.Auction.ItemEntry == Elixir).Sum(v => (int)v.Auction.ItemCount)),
                "elixirs on the market");

            // The kept stack is the keep count and no more: a further visit lists nothing.
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 3);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings after the third visit");
            await c.ExpectItemCountAsync(bot, Elixir, 5);
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_EconomyReport_NamesTheCountersTheMoneyGoalAndTheHourlyWindows()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-report", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econreport");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5, budget: 0.25f));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            string report = await c.ReadAsync(() => driver.Economy.Report(bot.Session!.World.NowMs));
            ScenarioContext.Expect(report.Contains("phase=Idle", StringComparison.Ordinal), "phase: " + report);
            ScenarioContext.Expect(report.Contains("visits=1", StringComparison.Ordinal), "visits: " + report);
            ScenarioContext.Expect(report.Contains("listed=2", StringComparison.Ordinal), "listed: " + report);
            ScenarioContext.Expect(report.Contains("bought=0", StringComparison.Ordinal), "bought: " + report);
            ScenarioContext.Expect(report.Contains("moneygoal=", StringComparison.Ordinal), "moneygoal: " + report);
            ScenarioContext.Expect(report.Contains("hour=buyouts:0/6,listings:2/10", StringComparison.Ordinal), "hour: " + report);
            ScenarioContext.Expect(!report.Contains('\n'), "one line: " + report);
            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsThisHour(bot.Session!.World.NowMs)), "listings this hour");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.BuyoutsThisHour(bot.Session!.World.NowMs)), "buyouts this hour");
            await c.ReadAsync(() => driver.Active = false);
        });
    }
}
