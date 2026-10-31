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
/// The economy bot (<see cref="PlayerbotEconomy"/>) in a real scenario world (docs/areas/playbots-economy.md): the ordinary world thread, the real
/// auction and mail handlers, a SQLite character database and a Stormwind auctioneer two yards from the start (<see cref="ScenarioTestContent.UseAuctioneer"/>).
/// A bot's economy is driven by a controller that calls <see cref="PlayerbotEconomy.Update"/> once per world tick, exactly as the brain does; the
/// seller, the buyer of a sale and the mail senders are scripted scenario bots using the ordinary client packets.
/// </summary>
public abstract class PlayerbotEconomyWorldTestBase
{
    private protected const uint BoltA = 990301;
    private protected const uint BoltB = 990302;
    private protected const uint BoltC = 990303;
    private protected const uint BoltD = 990304;
    private protected const uint BoltE = 990305;
    private protected const uint BoltF = 990306;
    private protected const uint Elixir = 990340;
    private protected const uint SmallRing = 990310;
    private protected const uint BigRing = 990311;
    private protected const uint Filler = 990320;
    private protected const uint LevelFiftyCloth = 990350;

    /// <summary>
    /// The most a bot pays for the Scenario Ring (sell 40, buy 200, quality 2) at the default options and the Alliance house (cut 5 %): its bot floor
    /// is max(vendor floor 44, 300 % of 40 = 120, reference 141) = 141 and the buy cap 50 % of that, rounded down.
    /// </summary>
    private protected const uint RingBuyout = 70;
    private protected const uint RingStartBid = 40;

    private protected static ObjectGuid Auctioneer => ObjectGuid.WithEntry(HighGuid.Unit, AuctioneerEntry, AuctioneerSpawn);

    private protected sealed record ReagentMarket(uint Cap, uint Within, uint WithinId, uint OverId, uint SingleId, uint ExtraId = 0);

    /// <summary>
    /// Per craft the bot wants two linen cloth (set on the world thread, the seam the economy reads instead of the spellbook). The vendor-stocked
    /// seam is pinned too, so no scenario depends on whether the scenario world has a service index: by default no entry is vendor stocked.
    /// </summary>
    private protected static Task SetReagentNeedsAsync(ScenarioContext c, Driver driver, Func<uint, bool>? vendorStocked = null, uint entry = LinenCloth, bool pinVendorStocked = true) => c.ReadAsync(() =>
    {
        driver.Economy.ReagentNeeds = _ => new Dictionary<uint, uint> { [entry] = 2 };
        // pinVendorStocked: false leaves the seam null, so the economy reads the world's service index (and fails closed without one).
        if (pinVendorStocked) driver.Economy.VendorStocked = vendorStocked ?? (_ => false);
        return true;
    });

    /// <summary>The world's service index as the economy would resolve it, read on the world thread: whether it was built, how many items it lists as vendor stocked, and whether linen is one.</summary>
    private protected static Task<(bool Built, int StockedCount, bool LinenStocked)> IndexAsync(ScenarioContext c, ScenarioBot bot) => c.ReadAsync(() =>
    {
        var services = bot.Session!.Services.GetService<QuestNpcFeature>()?.Services;
        PlayerbotServiceIndex? index = services is null ? null : PlayerbotServiceIndex.Get(bot.Session!, services, bot.RequirePlayer());
        return (index is not null, index?.VendorStockedCount ?? 0, index?.IsVendorStocked(LinenCloth) ?? false);
    });

    private protected const uint VendorEntry = 990060;

    /// <summary>Adds a spawned vendor (beside the start) that stocks exactly <paramref name="stocked"/>, to the content the scenario world loads.</summary>
    private protected static void UseVendor(IServiceCollection services, uint stocked)
    {
        ICreatureDataStore inner = (ICreatureDataStore)services.Last(d => d.ServiceType == typeof(ICreatureDataStore)).ImplementationInstance!;
        services.AddSingleton<ICreatureDataStore>(new WithVendor(inner));
        services.AddSingleton<INpcContentStore>(new VendorStock(stocked));
    }

    private protected sealed class WithVendor(ICreatureDataStore inner) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await inner.LoadAsync(cancellationToken);
            var vendor = new CreatureTemplate
            {
                Entry = VendorEntry, Name = "Scenario Peddler", Faction = 12, DisplayIds = [49], NpcFlags = (uint)NpcFlags.Vendor,
                MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = global::ArcaneCore.Game.Creatures.Creature.ExtraFlagNoAggro,
            };
            var spawn = new CreatureSpawn { Guid = VendorEntry, Entry = VendorEntry, MapId = 0, X = StartX + 6f, Y = StartY, Z = StartZ };

            // The scenario content has templates and spawns only (no waypoints, models or addons).
            return new CreatureContent([.. content.Templates, vendor], [.. content.MapsWithSpawns.SelectMany(content.GetSpawns), spawn], [], [], []);
        }
    }

    private protected sealed class VendorStock(uint stocked) : INpcContentStore
    {
        public Task<NpcContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(NpcContent.Empty with { VendorItems = [new VendorItem { Entry = VendorEntry, Item = stocked, Slot = 1 }] });
    }

    /// <summary>
    /// A seller lists linen cloth three ways: a pair exactly at the reagent cap per unit (the one to buy), a pair one copper per unit over the cap,
    /// and a single cheap cloth that does not cover a shortfall of two.
    /// </summary>
    private protected static async Task<ReagentMarket> SeedReagentMarketAsync(ScenarioContext c, EconomyFeature economy, bool secondInCapPair = false, bool overCapOnly = false)
    {
        ScenarioBot seller = await c.LoginAsync("Econseller");
        await c.GiveMoneyAsync(seller, 3000);
        uint cap = BuyCap(economy, Template(economy, LinenCloth));
        ScenarioContext.ExpectEqual(50u, cap, "the linen buy cap per unit: half of the bot floor 100 (500 % of 13 is 65, the reference is 100)");
        uint within = 2 * cap;
        uint over = (2 * cap) + 2;
        ScenarioContext.Expect(cap > 0 && over < 1000, $"the linen cap {cap} must leave the over-priced pair inside the 1000 copper visit budget");

        // One stack at a time: a second stack of the same entry would merge into the first one in the bags.
        // With overCapOnly nothing but the over-priced pair is listed (the in-cap pair and the single have id 0).
        uint withinId = overCapOnly ? 0 : await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), within / 2, within, 480);
        uint overId = await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), over / 2, over, 480);
        uint singleId = overCapOnly ? 0 : await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 1), 10, cap / 4 + 1, 480);
        uint extraId = secondInCapPair ? await ListAsync(seller, await c.GiveItemAsync(seller, LinenCloth, 2), within / 2, within, 480) : 0;
        return new ReagentMarket(cap, within, withinId, overId, singleId, extraId);
    }

    // --- harness -----------------------------------------------------------------------------------------------------

    private protected static Task<ScenarioTestWorld> StartAsync(Action<IServiceCollection>? more = null) => ScenarioTestWorld.StartAsync(services =>
    {
        var items = (InMemoryItemTemplateSource)services.Last(d => d.ServiceType == typeof(IItemTemplateSource)).ImplementationInstance!;
        items.Templates.AddRange(
        [
            Bolt(BoltA, "Scenario Bolt A", 200),
            Bolt(BoltB, "Scenario Bolt B", 150),
            Bolt(BoltC, "Scenario Bolt C", 100),
            Bolt(BoltD, "Scenario Bolt D", 200),
            Bolt(BoltE, "Scenario Bolt E", 180),
            Bolt(BoltF, "Scenario Bolt F", 170),
            new ItemTemplate { Entry = Elixir, Name = "Scenario Elixir", Class = (uint)ItemClass.Consumable, Quality = 1, Stackable = 5, SellPrice = 100, BuyPrice = 1000 },
            PlayerbotEquipmentTests.Ring(SmallRing, 5) with { Name = "Scenario Ring", Quality = 2, SellPrice = 40, BuyPrice = 200 },
            PlayerbotEquipmentTests.Ring(BigRing, 20) with { Name = "Scenario Great Ring", Quality = 2, SellPrice = 40, BuyPrice = 200 },
            new ItemTemplate { Entry = Filler, Name = "Scenario Pebble", Class = 15, Quality = 0, Stackable = 1, SellPrice = 1 },
            new ItemTemplate { Entry = LevelFiftyCloth, Name = "Scenario Level 50 Cloth", Class = (uint)ItemClass.TradeGoods, Quality = 1, ItemLevel = 50, SellPrice = 400, BuyPrice = 1600, Stackable = 20 },
        ]);
        UseAuctioneer(services);
        more?.Invoke(services);
    });

    /// <summary>A trade goods stack: reference price well above the vendor price (a quarter of the buy price), so a listing pays.</summary>
    private protected static ItemTemplate Bolt(uint entry, string name, uint sell)
        => new() { Entry = entry, Name = name, Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 20, SellPrice = sell, BuyPrice = sell * 10 };

    private protected static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
        => await world.RunPassingAsync(new Script(name, body));

    private protected static PlayerbotOptions Options(int listChance, int maxListings, float budget) => new()
    {
        Enabled = true,
        Economy = new PlayerbotEconomyOptions { ListChancePerVisit = listChance, MaxListingsPerBot = maxListings, BuyUpgradeBudgetFraction = budget, VisitIntervalMinutes = 30 },
    };

    private protected static AuctionHouseEntry HouseOf(EconomyFeature economy) => economy.Options.AuctionHouses.Single(h => h.Id == AuctionHouseRules.AllianceHouse);

    /// <summary>The most a bot pays per unit for the template: the same expression the economy uses (the Alliance house cut times the cut rate).</summary>
    private protected static uint BuyCap(EconomyFeature economy, ItemTemplate template, PlayerbotEconomyOptions? options = null)
    {
        options ??= new PlayerbotEconomyOptions();
        return PlayerbotAuctionPricing.BuyCapPerUnit(template, HouseOf(economy).CutPercent * economy.Options.AuctionRateCut,
            options.ListMarkupPercentFor(template.Quality), options.BuyBelowAskPercent);
    }

    private protected static ItemTemplate Template(EconomyFeature economy, uint entry) => economy.Templates.Find(entry) ?? throw new ScenarioAssertionException($"no template {entry}");

    /// <summary>The scripted bot sells an item through the ordinary packet and returns the auction id.</summary>
    private protected static async Task<uint> ListAsync(ScenarioBot bot, ObjectGuid item, uint startBid, uint buyout, uint minutes)
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
    private protected static async Task PaceAsync(ScenarioContext c, string what, Func<bool> done, TimeSpan? wall = null)
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
    private protected static async Task FinishAsync(ScenarioContext c, Driver driver, int visits, TimeSpan? timeout = null)
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

    private protected static async Task BuyOutAsync(ScenarioBot bot, uint auction, uint price)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionPlaceBid, ScenarioAuctionWire.PlaceBid(Auctioneer.Value, auction, price)), "bid refused");
        AuctionCommandResultView placed = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult, r => r.Action == AuctionAction.BidPlaced, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, placed.Error, $"{bot.Name} buyout result");
    }

    private protected static async Task<IReadOnlyList<MailRecord>> MailsAsync(ScenarioContext c, ScenarioBot bot)
    {
        await using AsyncServiceScope scope = c.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEconomyStore>().GetMailsAsync((int)bot.Guid.Low);
    }

    private protected static async Task<int> ItemRowsAsync(ScenarioContext c, uint itemGuid)
    {
        await using AsyncServiceScope scope = c.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CharacterDbContext>().Set<ItemInstanceRow>().AsNoTracking().CountAsync(i => i.Guid == itemGuid);
    }

    private protected static async Task<Driver> AttachAsync(ScenarioContext c, ScenarioBot bot, PlayerbotOptions options)
    {
        var driver = new Driver(bot, options);
        if (!await c.Services.GetRequiredService<ManagedPlayerbotFeature>().SetControllerAsync(bot.BotId, driver))
            throw new ScenarioAssertionException($"{bot.Name} could not be handed to the economy driver");
        return driver;
    }

    private protected static Task StartVisitAsync(ScenarioContext c, Driver driver) => c.ReadAsync(() =>
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
    private protected static async Task<Letters> SeedLettersAsync(ScenarioContext c, EconomyFeature economy, ScenarioBot bot, ScenarioBot other, bool foreign)
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
    private protected static async Task<Race> RaceAsync(ScenarioContext c, EconomyFeature economy, uint auction)
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
    private protected static async Task ExpectItemOnceAsync(ScenarioContext c, EconomyFeature economy, uint auction, uint itemGuid, Race race)
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

    private protected sealed record Letters(uint SaleProceeds);

    private protected sealed record Result(ScenarioBot Bot, uint Money, int Purchases);

    private protected sealed record Race(List<Result> Winners, Result Loser, int BidsSent, ScenarioBot[] Bots);

    private protected sealed class Script(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => "PlayerbotEconomyWorldTests: " + name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>Calls <see cref="PlayerbotEconomy.Update"/> once per world tick for one scripted bot, the way the brain does (world thread only).</summary>
    private protected sealed class Driver : IPlayerbotController
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
    private protected sealed class Gate
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

    private protected sealed class GateStore(Gate gate, IEconomyStore inner) : IEconomyStore
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
