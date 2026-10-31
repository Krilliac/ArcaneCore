using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>
/// H1 hardening: the economy bot's anomaly reports (<c>bot-economy/*</c>: ah-post-failed, sell-failed, item-not-found, money-invalid).
/// Each key fires in a real scenario world (real auction and mail handlers, a SQLite character database) and stays silent on the healthy
/// and on the legitimately blocked paths. Failures the handlers cannot produce on their own are made by a store that refuses commits
/// (<see cref="FailSwitch"/>) or by an observer that changes a rule between the bot's plan and the handler (<c>ManagedDispatchObserver</c>).
/// Reports are read from the shared ledger through <see cref="PlayerbotAnomalies.CaptureFor"/> (this bot's subject only).
/// </summary>
public sealed class PlayerbotEconomyAnomalyTests
{
    private const uint BoltA = 990401;
    private const uint BoltB = 990402;
    private const uint Filler = 990420;

    private static readonly string AhPostFailed = PlayerbotAnomalies.Key("economy", "ah-post-failed");
    private static readonly string SellFailed = PlayerbotAnomalies.Key("economy", "sell-failed");
    private static readonly string ItemNotFound = PlayerbotAnomalies.Key("economy", "item-not-found");
    private static readonly string MoneyInvalid = PlayerbotAnomalies.Key("economy", "money-invalid");

    private static ObjectGuid Auctioneer => ObjectGuid.WithEntry(HighGuid.Unit, AuctioneerEntry, AuctioneerSpawn);

    // --- fires -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListingWhoseCommitIsRefused_ReportsAhPostFailedAndSellFailedOnce_AndNothingElse()
    {
        var fail = new FailSwitch();
        await using ScenarioTestWorld world = await StartAsync(services => services.AddScoped<IEconomyStore>(sp => new FailStore(fail, new EfEconomyStore(sp.GetRequiredService<CharacterDbContext>()))));
        await RunAsync(world, "economy-anomaly-commit-refused", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econanomone");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            uint moneyBefore = await bot.ReadAsync(p => p.Money);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));

            fail.Refuse = true;
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            // The database refused: the item stayed in the bags and the purse is untouched, so the listing really failed.
            await c.ExpectItemCountAsync(bot, BoltA, 3);
            ScenarioContext.ExpectEqual(moneyBefore, await bot.ReadAsync(p => p.Money), "money after a refused listing");
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.ExpectEqual(1, capture.Count(AhPostFailed), capture.Describe());
            ScenarioContext.ExpectEqual(1, capture.Count(SellFailed), capture.Describe());
            ScenarioContext.ExpectEqual(0, capture.Count(ItemNotFound), "a database refusal is not an item-not-found: " + capture.Describe());
            ScenarioContext.ExpectEqual(0, capture.Count(MoneyInvalid), capture.Describe());
            AnomalyNotice notice = capture.Notices.First(n => n.Key == AhPostFailed);
            ScenarioContext.ExpectEqual(AnomalySeverity.Warning, notice.Severity, "severity");
            ScenarioContext.Expect(notice.Detail.Contains("Database", StringComparison.Ordinal), "the detail names the auction error: " + notice.Detail);
        });
    }

    [Fact]
    public async Task ListingRefusedAtOnceByTheHandler_ReportsAhPostFailedAndSellFailed_WithTheImmediateDetail()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-immediate", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econanomtwo");
            await c.GiveMoneyAsync(bot, 5000);
            ObjectGuid cloth = await c.GiveItemAsync(bot, LinenCloth);
            await ListAsync(bot, cloth, 20, 60, 480);
            await c.GiveItemAsync(bot, BoltA, 3);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));

            // One active auction per account: the bot's own cap (five) does not know it, the handler answers at once and takes nothing.
            await c.ReadAsync(() => { economy.Options.AuctionAccountConcurrentLimit = 1; return true; });
            try
            {
                await StartVisitAsync(c, driver);
                await FinishAsync(c, driver, 1);
            }
            finally
            {
                await c.ReadAsync(() => { economy.Options.AuctionAccountConcurrentLimit = 0; driver.Active = false; return true; });
            }

            await c.ExpectItemCountAsync(bot, BoltA, 3);
            ScenarioContext.ExpectEqual(1, capture.Count(AhPostFailed), capture.Describe());
            ScenarioContext.ExpectEqual(1, capture.Count(SellFailed), capture.Describe());
            ScenarioContext.ExpectEqual(0, capture.Count(ItemNotFound), capture.Describe());
            ScenarioContext.Expect(capture.Notices.First(n => n.Key == AhPostFailed).Detail.Contains("rejected immediately", StringComparison.Ordinal), capture.Describe());
        });
    }

    [Fact]
    public async Task ListingSettlementThatOutlivesTheDeadline_ReportsAhPostFailedAndSellFailed_AsATimeout()
    {
        var gate = new Gate();
        await using ScenarioTestWorld world = await StartAsync(services => services.AddScoped<IEconomyStore>(sp => new GateStore(gate, new EfEconomyStore(sp.GetRequiredService<CharacterDbContext>()))));
        await RunAsync(world, "economy-anomaly-timeout", async c =>
        {
            try
            {
                EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
                economy.Options.SettlementBudgetSeconds = 300;
                ScenarioBot bot = await c.LoginAsync("Econanomtre");
                await c.GiveMoneyAsync(bot, 5000);
                await c.GiveItemAsync(bot, BoltA, 3);
                using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
                Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));
                gate.Arm();
                await StartVisitAsync(c, driver);
                await c.WaitUntilAsync("the listing waits on its commit", () => driver.Economy.Phase == PlayerbotEconomyPhase.AwaitSell && gate.Held == 1);
                await c.WaitUntilAsync("the visit is dropped at the deadline", () => driver.Economy.Phase == PlayerbotEconomyPhase.Idle, TimeSpan.FromSeconds(60));
                await c.ReadAsync(() => driver.Active = false);

                ScenarioContext.ExpectEqual(1, capture.Count(AhPostFailed), capture.Describe());
                ScenarioContext.ExpectEqual(1, capture.Count(SellFailed), capture.Describe());
                ScenarioContext.Expect(capture.Notices.First(n => n.Key == AhPostFailed).Detail.Contains("timed out", StringComparison.Ordinal), capture.Describe());
            }
            finally
            {
                gate.Release();
            }
        });
    }

    [Fact]
    public async Task ListingChargedMoreThanThePlannedDeposit_ReportsMoneyInvalidAsAnError_AndStopsListing()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-deposit", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econanomfour");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));

            // The bot plans its deposit with the house rate; the rate doubles in the instant before the handler computes the real one.
            await c.ReadAsync(() =>
            {
                bot.Session!.ManagedDispatchObserver = op =>
                {
                    if (op == WorldOpcode.CmsgAuctionSellItem) economy.Options.AuctionRateDeposit = 2.0f;
                };
                return true;
            });
            try
            {
                await StartVisitAsync(c, driver);
                await FinishAsync(c, driver, 1);
            }
            finally
            {
                await c.ReadAsync(() => { economy.Options.AuctionRateDeposit = 1.0f; bot.Session!.ManagedDispatchObserver = null; driver.Active = false; return true; });
            }

            ScenarioContext.ExpectEqual(1, capture.Count(MoneyInvalid), "one mismatch, then the fault flag ends the listing (the second stack is not listed): " + capture.Describe());
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == MoneyInvalid);
            ScenarioContext.ExpectEqual(AnomalySeverity.Error, notice.Severity, "severity");
            ScenarioContext.Expect(notice.Detail.Contains("deposit mismatch", StringComparison.Ordinal), notice.Detail);
            ScenarioContext.ExpectEqual(1, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed before the fault flag stopped the rest");
        });
    }

    [Fact]
    public async Task QueuedItemThatChangedInTheBags_ReportsItemNotFound_PerQueuedItem()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-queued-changed", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econanomfive");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));
            driver.PauseWhen = e => e.Phase == PlayerbotEconomyPhase.Listing;
            await StartVisitAsync(c, driver);
            await c.WaitUntilAsync("the queue is built", () => !driver.Active && driver.Economy.Phase == PlayerbotEconomyPhase.Listing);

            // Both stacks grow after the bot queued them: neither is the stack it planned to list any more.
            await c.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                ScenarioContext.Expect(player.Inventory.AddItem(BoltA, 1, out _) == InventoryResult.Ok, "bolt A added");
                ScenarioContext.Expect(player.Inventory.AddItem(BoltB, 1, out _) == InventoryResult.Ok, "bolt B added");
                driver.PauseWhen = null;
                driver.Active = true;
                return true;
            });
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            ScenarioContext.ExpectEqual(2, capture.Count(ItemNotFound), capture.Describe());
            ScenarioContext.ExpectEqual(0, capture.Count(AhPostFailed), "nothing was sent to the house: " + capture.Describe());
            ScenarioContext.ExpectEqual(0, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "listings placed");
            ScenarioContext.Expect(capture.Notices.First(n => n.Key == ItemNotFound).Detail.Contains("missing or changed", StringComparison.Ordinal), capture.Describe());
        });
    }

    [Fact]
    public async Task MailLettersWhoseTakeIsRefused_ReportSellFailedPerLetter_AndItemNotFoundForTheItemLetter()
    {
        var fail = new FailSwitch();
        await using ScenarioTestWorld world = await StartAsync(services => services.AddScoped<IEconomyStore>(sp => new FailStore(fail, new EfEconomyStore(sp.GetRequiredService<CharacterDbContext>()))));
        await RunAsync(world, "economy-anomaly-mail-refused", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econanomsix");
            ScenarioBot other = await c.LoginAsync("Econanomoth");
            await SeedLettersAsync(c, economy, bot, other);
            uint moneyBefore = await bot.ReadAsync(p => p.Money);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5));

            fail.Refuse = true;
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            // Two letters (an item letter from the expiry and a money letter from the sale), both refused by the database.
            ScenarioContext.ExpectEqual(moneyBefore, await bot.ReadAsync(p => p.Money), "no money was collected");
            await c.ExpectItemCountAsync(bot, BoltB, 0);
            ScenarioContext.ExpectEqual(1, capture.Count(ItemNotFound), capture.Describe());
            ScenarioContext.ExpectEqual(2, capture.Count(SellFailed), capture.Describe());
            ScenarioContext.ExpectEqual(0, capture.Count(MoneyInvalid), capture.Describe());
            ScenarioContext.Expect(capture.Notices.Any(n => n.Key == SellFailed && n.Detail.Contains("auction mail money", StringComparison.Ordinal)), "the money letter: " + capture.Describe());
            ScenarioContext.Expect(capture.Notices.Any(n => n.Key == SellFailed && n.Detail.Contains("auction mail item", StringComparison.Ordinal)), "the item letter: " + capture.Describe());
        });
    }

    // --- silent ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HealthyListingVisit_ReportsNoEconomyAnomaly()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-silent-listing", async c =>
        {
            ScenarioBot bot = await c.LoginAsync("Econsilone");
            await c.GiveMoneyAsync(bot, 5000);
            await c.GiveItemAsync(bot, BoltA, 3);
            await c.GiveItemAsync(bot, BoltB, 2);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 100, maxListings: 5));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            ScenarioContext.ExpectEqual(2, await c.ReadAsync(() => driver.Economy.ListingsPlaced), "the healthy visit really listed both stacks");
            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("economy")).Count, capture.Describe());
        });
    }

    [Fact]
    public async Task HealthyMailVisit_ReportsNoEconomyAnomaly()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-silent-mail", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econsiltwo");
            ScenarioBot other = await c.LoginAsync("Econsiloth");
            uint proceeds = await SeedLettersAsync(c, economy, bot, other);
            uint moneyBefore = await bot.ReadAsync(p => p.Money);
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            ScenarioContext.ExpectEqual(moneyBefore + proceeds, await bot.ReadAsync(p => p.Money), "the sale's money was collected");
            await c.ExpectItemCountAsync(bot, BoltB, 1);
            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("economy")).Count, capture.Describe());
        });
    }

    [Fact]
    public async Task MailVisitWithFullBags_LeavesTheItemLetterQuietly()
    {
        await using ScenarioTestWorld world = await StartAsync();
        await RunAsync(world, "economy-anomaly-silent-full-bags", async c =>
        {
            EconomyFeature economy = c.Services.GetRequiredService<EconomyFeature>();
            ScenarioBot bot = await c.LoginAsync("Econsiltre");
            ScenarioBot other = await c.LoginAsync("Econsilothb");
            await SeedLettersAsync(c, economy, bot, other);
            int filled = await c.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                int n = 0;
                while (n < 200 && player.Inventory.AddItem(Filler, 1, out _) == InventoryResult.Ok) n++;
                return n;
            });
            ScenarioContext.Expect(filled > 0 && await c.ReadAsync(() => PlayerbotTownGoals.FreeBagSlots(bot.RequirePlayer())) == 0, "the bags are full");
            using AnomalyCapture capture = await bot.ReadAsync(PlayerbotAnomalies.CaptureFor);
            Driver driver = await AttachAsync(c, bot, Options(listChance: 0, maxListings: 5));
            await StartVisitAsync(c, driver);
            await FinishAsync(c, driver, 1);
            await c.ReadAsync(() => driver.Active = false);

            await c.ExpectItemCountAsync(bot, BoltB, 0);
            ScenarioContext.ExpectEqual(0, capture.OfSubsystem(PlayerbotAnomalies.Subsystem("economy")).Count, capture.Describe());
        });
    }

    // --- harness (a trimmed copy of the PlayerbotEconomyWorldTestBase helpers) -----------------------------

    private static Task<ScenarioTestWorld> StartAsync(Action<IServiceCollection>? more = null) => ScenarioTestWorld.StartAsync(services =>
    {
        var items = (InMemoryItemTemplateSource)services.Last(d => d.ServiceType == typeof(IItemTemplateSource)).ImplementationInstance!;
        items.Templates.AddRange(
        [
            Bolt(BoltA, "Anomaly Bolt A", 200),
            Bolt(BoltB, "Anomaly Bolt B", 150),
            new ItemTemplate { Entry = Filler, Name = "Anomaly Pebble", Class = 15, Quality = 0, Stackable = 1, SellPrice = 1 },
        ]);
        UseAuctioneer(services);
        more?.Invoke(services);
    });

    private static ItemTemplate Bolt(uint entry, string name, uint sell)
        => new() { Entry = entry, Name = name, Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 20, SellPrice = sell, BuyPrice = sell * 10 };

    private static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
        => await world.RunPassingAsync(new Script(name, body));

    private static PlayerbotOptions Options(int listChance, int maxListings) => new()
    {
        Enabled = true,
        Economy = new PlayerbotEconomyOptions { ListChancePerVisit = listChance, MaxListingsPerBot = maxListings, BuyUpgradeBudgetFraction = listChance > 0 ? 0.25f : 0f, VisitIntervalMinutes = 30 },
    };

    private static AuctionHouseEntry HouseOf(EconomyFeature economy) => economy.Options.AuctionHouses.Single(h => h.Id == AuctionHouseRules.AllianceHouse);

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

    private static async Task<uint> ListAsync(ScenarioBot bot, ObjectGuid item, uint startBid, uint buyout, uint minutes)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionSellItem, ScenarioAuctionWire.Sell(Auctioneer.Value, item.Value, startBid, buyout, minutes)), "sell refused");
        AuctionCommandResultView started = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult, r => r.Action == AuctionAction.Started, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, started.Error, $"{bot.Name} listing result");
        return started.AuctionId;
    }

    private static async Task BuyOutAsync(ScenarioBot bot, uint auction, uint price)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionPlaceBid, ScenarioAuctionWire.PlaceBid(Auctioneer.Value, auction, price)), "bid refused");
        AuctionCommandResultView placed = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult, r => r.Action == AuctionAction.BidPlaced, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, placed.Error, $"{bot.Name} buyout result");
    }

    /// <summary>
    /// Mail for <paramref name="bot"/>: it sells three <see cref="BoltA"/> (bought out by <paramref name="other"/>: a sale letter) and lists one
    /// <see cref="BoltB"/> for two hours that expires (an expiry letter with the item). Returns what the sale letter pays.
    /// </summary>
    private static async Task<uint> SeedLettersAsync(ScenarioContext c, EconomyFeature economy, ScenarioBot bot, ScenarioBot other)
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
        ((ScenarioTimeProvider)c.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromHours(3));
        await c.ReadAsync(() =>
        {
            economy.RunExpirySweep();
            return true;
        });
        await c.WaitUntilAsync("the expired listing is settled", () => economy.FindAuction(expiringId) is null);
        return AuctionHouseRules.Proceeds(600, deposit, AuctionHouseRules.Cut(house, 600));
    }

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

    private static async Task FinishAsync(ScenarioContext c, Driver driver, int visits)
    {
        try
        {
            await PaceAsync(c, $"visit {visits} finishes", () => driver.Economy.VisitsFinished >= visits);
        }
        catch (ScenarioTimeoutException timedOut)
        {
            string state = await c.ReadAsync(() => $"phase {driver.Economy.Phase}, finished {driver.Economy.VisitsFinished}, ticks {driver.Ticks}, active {driver.Active}");
            throw new ScenarioAssertionException(timedOut.Message + "; " + state);
        }
    }

    private sealed class Script(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => "PlayerbotEconomyAnomalyTests: " + name;

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

    /// <summary>While <see cref="Refuse"/> is set every commit is answered <see cref="EconomyCommitResult.Conflict"/> (nothing written).</summary>
    private sealed class FailSwitch
    {
        public volatile bool Refuse;
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _held;

        public int Held => Volatile.Read(ref _held);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public async Task PassAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            Interlocked.Increment(ref _held);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private abstract class DelegatingStore(IEconomyStore inner) : IEconomyStore
    {
        public abstract Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default);

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

        protected IEconomyStore Inner => inner;
    }

    private sealed class FailStore(FailSwitch fail, IEconomyStore inner) : DelegatingStore(inner)
    {
        public override Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
            => fail.Refuse ? Task.FromResult(EconomyCommitResult.Conflict) : Inner.CommitAsync(request, cancellationToken);
    }

    private sealed class GateStore(Gate gate, IEconomyStore inner) : DelegatingStore(inner)
    {
        public override async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            await gate.PassAsync(cancellationToken).ConfigureAwait(false);
            return await Inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
