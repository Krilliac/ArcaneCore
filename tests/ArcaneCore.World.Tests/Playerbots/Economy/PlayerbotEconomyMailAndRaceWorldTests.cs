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
/// Collecting mail, two bots racing for one auction, and visits dropped by timeouts, the feature switch or a reset. Split from the former <c>PlayerbotEconomyWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotEconomyWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotEconomyMailAndRaceWorldTests : PlayerbotEconomyWorldTestBase
{
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
            uint auction = await ListAsync(seller, ring, RingStartBid, RingBuyout, 480);
            uint itemGuid = ring.Low;

            Race race = await RaceAsync(c, economy, auction);
            ScenarioContext.ExpectEqual(2, race.BidsSent, "both bots sent their bid (the contention was real)");
            ScenarioContext.ExpectEqual(1, race.Winners.Count, "bots that bought the auction");
            ScenarioContext.ExpectEqual(4000u - RingBuyout, race.Winners[0].Money, "the winner paid the buyout once");
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
                economy.StartBotListing(AuctionHouseRules.AllianceHouse, id, item, RingStartBid, RingBuyout, economy.Now + (8 * 3600), row.OperationId,
                    outcome => ledger.Resolve(row.IdemKey, outcome == EconomyOutcome.After, economy.Now));
                return (id, item.Guid);
            });
            await c.WaitUntilAsync("the seller-less listing is committed", () => ledger.Find(AuctionBotCustodyLedger.ListingKey(auction))?.State == AuctionBotCustodyState.TerminalOk);
            ScenarioContext.Expect(await c.ReadAsync(() => economy.FindAuction(auction) is { SellerId: 0 }), "the listing is a seller-less (AH bot) auction");

            Race race = await RaceAsync(c, economy, auction);
            ScenarioContext.ExpectEqual(2, race.BidsSent, "both bots sent their bid (the contention was real)");
            ScenarioContext.ExpectEqual(1, race.Winners.Count, "bots that bought the auction");
            ScenarioContext.ExpectEqual(4000u - RingBuyout, race.Winners[0].Money, "the winner paid the buyout once");
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
}
