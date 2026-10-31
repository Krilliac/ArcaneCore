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
/// Reagent buyouts: caps, repeat buys, pending bids and settlement without a result packet. Split from the former <c>PlayerbotEconomyWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotEconomyWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotEconomyReagentBidWorldTests : PlayerbotEconomyWorldTestBase
{
    [Fact]
    public async Task Visit_BuysNoReagent_WhenTheOnlyCoveringRowIsOverTheCap()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-over-cap-only", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, overCapOnly: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The house holds only a pair one copper per unit over the cap; the bot lacks two and can afford it, yet buys nothing.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.OverId) is { BidderId: 0 }), "the over-priced pair is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoReagentPricedAtTwiceTheReference_ButBuysOneAtTheDerivedCap()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-derived-cap", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot seller = await c.LoginAsync("Econseller");
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            // The item templates load with the first login.
            ItemTemplate silk = Template(economy, LevelFiftyCloth);
            // Reference 5000 (20 * 50^2 / 10): twice that is 10000. The bot floor is also 5000 (500 % of 400 and the vendor floor are lower), the cap half of it.
            uint twiceReference = PlayerbotAuctionPricing.CapMultiple * PlayerbotAuctionPricing.ReferencePerUnit(silk);
            uint cap = BuyCap(economy, silk);
            ScenarioContext.ExpectEqual((10_000u, 2_500u), (twiceReference, cap), "twice the reference and the buy cap per unit of the level 50 cloth");
            uint oldCap = twiceReference;

            await c.GiveMoneyAsync(seller, 5000);
            await c.GiveMoneyAsync(buyer, 100_000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);

            // A pair at twice the reference per unit: 20000 copper in total, inside the visit budget (a quarter of 100000) and the purse, so only the cap refuses it.
            uint dear = 2 * oldCap;
            uint dearId = await ListAsync(seller, await c.GiveItemAsync(seller, LevelFiftyCloth, 2), dear / 2, dear, 480);
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            ScenarioContext.Expect(dear < moneyBefore / 4, "the dear pair must fit the visit budget");
            await SetReagentNeedsAsync(c, driver, entry: LevelFiftyCloth);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases of a pair at twice the reference");
            ScenarioContext.ExpectEqual(moneyBefore, await buyer.ReadAsync(p => p.Money), "money after the first visit");
            await c.ExpectItemCountAsync(buyer, LevelFiftyCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(dearId) is { BidderId: 0 }), "the dear pair is still in the house");

            // A pair at the derived cap per unit joins it: the next visit buys that one and still leaves the dear pair alone.
            uint fair = 2 * cap;
            uint fairId = await ListAsync(seller, await c.GiveItemAsync(seller, LevelFiftyCloth, 2), fair / 2, fair, 480);
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 2);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after the second visit");
            ScenarioContext.ExpectEqual(fair, await c.ReadAsync(() => driver.Economy.Spent), "spent");
            ScenarioContext.ExpectEqual(moneyBefore - fair, await buyer.ReadAsync(p => p.Money), "money after the second visit");
            await c.ExpectItemCountAsync(buyer, LevelFiftyCloth, 2);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(fairId) is null), "the pair at the cap left the house");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(dearId) is { BidderId: 0 }), "the dear pair is still in the house");
            await c.ReadAsync(() => driver.Active = false);
        });
    }

    [Fact]
    public async Task Visit_BuysNoReagent_WhenBuyReagentsIsTurnedOffAfterTheVisitStarted()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-off-mid-visit", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            PlayerbotOptions options = Options(listChance: 0, maxListings: 5, budget: 0.25f);
            Driver driver = await AttachAsync(c, buyer, options);
            await SetReagentNeedsAsync(c, driver);

            // A live reload: the switch goes off on the world thread right after the visit has started (it planned its reagent shortfalls with the
            // switch on), before any reagent bid. The first update after the request leaves Idle; the flip happens in that same tick.
            PlayerbotEconomyPhase? flippedIn = null;
            driver.PauseWhen = e =>
            {
                if (flippedIn is null && e.Phase != PlayerbotEconomyPhase.Idle)
                {
                    flippedIn = e.Phase;
                    options.Economy.BuyReagents = false;
                }
                return false;
            };
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            // The visit may enter the reagent phase in the tick that leaves Idle; the flip still precedes any bid (no AwaitBid yet).
            ScenarioContext.Expect(flippedIn is not null && flippedIn != PlayerbotEconomyPhase.AwaitBid,
                $"the switch was turned off before any reagent bid (flipped in {flippedIn})");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after the switch went off");
            ScenarioContext.ExpectEqual(4000u, await buyer.ReadAsync(p => p.Money), "money");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is { BidderId: 0 }), "the in-cap pair is still in the house");
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
            // Paced like FinishAsync (bounded by wall time, the world idling between checks), not by a game-time budget: the buyout settles
            // through the character database, which the scenario clock can run far ahead of (the spell-batching CI lane spent a 30 s
            // game budget in 0 s of wall time before the bid had settled).
            await PaceAsync(c, "visit 1 is cut off at the mail step", () => !driver.Active);
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

    // --- a reagent bid whose outcome the bot does not see ------------------------------------------------------------

    [Fact]
    public async Task Visit_DoesNotBuyAReagentAgain_WhenTheVisitWasResetWhileItsBidWasPending()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-reset-pending-bid", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);
            uint nowOf() => buyer.Session!.World.NowMs;

            // Visit 1 is dropped (Reset: the same path as combat, death, a map change or the visit cap) on the world thread in the tick that
            // placed the reagent bid, while its settlement is still pending. The house has no gear, so the first bid of the visit is the reagent bid.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            driver.PauseWhen = e =>
            {
                if (e.Phase != PlayerbotEconomyPhase.AwaitBid) return false;
                e.Reset();
                return true;
            };
            await StartVisitAsync(c, driver);
            await PaceAsync(c, "visit 1 is reset while its reagent bid is pending", () => !driver.Active);
            ScenarioContext.ExpectEqual(PlayerbotEconomyPhase.Idle, await c.ReadAsync(() => driver.Economy.Phase), "phase after the reset");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.VisitsFinished), "a dropped visit is not a finished one");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "the reset visit counted no purchase (its outcome was unknown)");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsAwaitingCollection(LinenCloth, nowOf())), "the aborted pending bid was booked: the linen is awaiting collection");

            // The settlement lands in the database after the reset: the buyout is paid and the won letter waits in the mailbox.
            await PaceAsync(c, "the settlement of the dropped bid lands", () => buyer.RequirePlayer().Money == moneyBefore - market.Within && !buyer.RequirePlayer().IsQuestSettlementPending);
            ScenarioContext.ExpectEqual(1, (await MailsAsync(c, buyer)).Count, "the won letter waits in the mailbox");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is not null)),
                "exactly one of the two in-cap pairs was bought");

            // Visit 2 buys nothing: the booked pair is on its way, so it is no shortfall.
            await c.ReadAsync(() => { driver.PauseWhen = null; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases after visit 2 (no second buy)");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is { BidderId: 0 })),
                "the other in-cap pair is still for sale");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money after visit 2: only the first buyout, once");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ItemsCollected), "visit 2 collected the won letter");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsHeld(LinenCloth, nowOf())), "still held against listing");
            await c.ReadAsync(() => { driver.Economy.BidSettlementPendingOverride = null; driver.Economy.DropBidResultForTest = false; driver.Active = false; return true; });
        });
    }

    [Fact]
    public async Task Visit_RefusesAReagentBid_ThatSettledAtOnceWithoutTakingMoney()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-settled-no-money", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);

            // The house refuses the bid at once: the purse is emptied from inside the vendor-stocked predicate, which the planner calls while
            // it chooses the auction, after it computed what the bot can spend and before it sends the bid (no settlement is started and
            // no money is taken). The pending-settlement answer is pinned false as well: a refusal never leaves a settlement pending.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            bool emptied = false;
            await c.ReadAsync(() =>
            {
                driver.Economy.ReagentNeeds = _ => new Dictionary<uint, uint> { [LinenCloth] = 2 };
                driver.Economy.VendorStocked = _ =>
                {
                    if (!emptied && driver.Economy.Phase == PlayerbotEconomyPhase.BuyingReagents)
                    {
                        emptied = true;
                        buyer.RequirePlayer().Money = 0;
                    }

                    return false;
                };
                driver.Economy.BidSettlementPendingOverride = _ => false;
                return true;
            });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.Expect(emptied, "the purse was emptied while the planner chose the auction");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases of a refused bid");
            ScenarioContext.ExpectEqual(0u, await c.ReadAsync(() => driver.Economy.Spent), "spent");
            ScenarioContext.ExpectEqual(0u, await buyer.ReadAsync(p => p.Money), "money (nothing was taken)");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 0);
            ScenarioContext.Expect(await c.ReadAsync(() => !driver.Economy.Bought.IsHeld(LinenCloth, buyer.Session!.World.NowMs)), "a refused bid is not booked as bought");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.WithinId) is { BidderId: 0 }), "the in-cap pair is still for sale, without a bidder");
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(market.ExtraId) is { BidderId: 0 }), "the second in-cap pair is still for sale");
            ScenarioContext.ExpectEqual(0, (await MailsAsync(c, buyer)).Count, "no letter");
            await c.ReadAsync(() => { driver.Economy.BidSettlementPendingOverride = null; driver.Economy.DropBidResultForTest = false; driver.Active = false; return true; });
        });
    }

    [Fact]
    public async Task Visit_BooksAReagentBuyout_WhoseSettlementEndsWithoutAResultPacket()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-reagent-no-result", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ReagentMarket market = await SeedReagentMarketAsync(c, economy, secondInCapPair: true);
            ScenarioBot buyer = await c.LoginAsync("Econbuyer");
            await c.GiveMoneyAsync(buyer, 4000);
            uint moneyBefore = await buyer.ReadAsync(p => p.Money);
            uint nowOf() => buyer.Session!.World.NowMs;

            // The economy discards the bid's result packet, so the only sign of the buyout is the money that left the purse once the
            // settlement ended. The visit books it from that, once.
            Driver driver = await AttachAsync(c, buyer, Options(listChance: 0, maxListings: 5, budget: 0.25f));
            await SetReagentNeedsAsync(c, driver);
            await c.ReadAsync(() => { driver.Economy.DropBidResultForTest = true; return true; });
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);

            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ReagentPurchasesMade), "reagent purchases");
            ScenarioContext.ExpectEqual(market.Within, await c.ReadAsync(() => driver.Economy.Spent), "spent");
            ScenarioContext.ExpectEqual(moneyBefore - market.Within, await buyer.ReadAsync(p => p.Money), "money: the buyout once");
            ScenarioContext.Expect(await c.ReadAsync(() => driver.Economy.Bought.IsHeld(LinenCloth, nowOf())), "the bought entry is held against listing");
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ItemsCollected), "the won letter was collected");
            await c.ExpectItemCountAsync(buyer, LinenCloth, 2);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => new[] { market.WithinId, market.ExtraId }.Count(id => economy.FindAuction(id) is not null)),
                "exactly one of the two in-cap pairs was bought");
            await c.ReadAsync(() => { driver.Economy.BidSettlementPendingOverride = null; driver.Economy.DropBidResultForTest = false; driver.Active = false; return true; });
        });
    }
}
