using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Items;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>The auction house bot on a real economy store: listings, buyouts, expiry, budgets and the custody ledger.</summary>
public sealed class AuctionBotFeatureTests
{
    private const uint House = AuctionHouseRules.NeutralHouse;

    [Fact]
    public async Task Disabled_by_default_lists_and_buys_nothing()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        await fixture.OnWorld(() =>
        {
            fixture.Bot.Tick();
            Assert.Equal(0, fixture.Bot.Sell(House));
            Assert.Equal(0, fixture.Bot.Buy(House));
            return true;
        });
        Assert.False(fixture.Bot.Options.Enabled);
        Assert.Empty(fixture.Bot.Ledger.Rows);
        Assert.Equal(1, await fixture.CountAsync<AuctionRow>());
    }

    [Fact]
    public async Task Sell_mints_each_listing_once_as_seller_zero_and_the_ledger_balances()
    {
        await using var fixture = await Fixture.CreateAsync();
        int started = await fixture.OnWorld(() => fixture.Bot.Sell(House));
        Assert.Equal(3, started); // the daily item budget
        await fixture.Economy.DrainAsync().WaitAsync(Fixture.Budget);
        await fixture.WaitUntilAsync(() => fixture.Bot.Ledger.Pending.Count() == 0);

        await using CharacterDbContext db = fixture.NewContext();
        List<AuctionRow> bot = await db.Set<AuctionRow>().AsNoTracking().Where(a => a.SellerId == 0).ToListAsync();
        Assert.Equal(3, bot.Count);
        List<ItemInstanceRow> items = await db.Set<ItemInstanceRow>().AsNoTracking().Where(i => i.Guid != Fixture.PlayerItemGuid).ToListAsync();
        Assert.Equal(bot.Select(a => a.ItemGuid).Order(), items.Select(i => i.Guid).Order());
        Assert.All(items, i => Assert.Equal(0, i.OwnerGuid));
        Assert.All(items, i => Assert.True(i.Guid > Fixture.PlayerItemGuid, "minted GUIDs come from the seeded allocator"));
        Assert.All(bot, a => Assert.True(a.StartBid >= 1 && a.StartBid <= a.Buyout && a.Deposit == 0));

        Assert.All(fixture.Bot.Ledger.Rows, r => Assert.Equal(AuctionBotCustodyState.TerminalOk, r.State));
        Assert.Equal(3u, fixture.Bot.Ledger.Totals(fixture.Economy.Now).ItemsToday);
        Assert.Empty(fixture.Bot.Ledger.Audit());
        Assert.False(fixture.Bot.Halted);
        Assert.Equal(0, await fixture.OnWorld(() => fixture.Bot.Sell(House))); // budget spent
        Assert.Equal(3, await fixture.OnWorld(() => fixture.Economy.BotAuctionCount(House)));
    }

    [Fact]
    public async Task Replaying_a_committed_listing_operation_mints_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = new ItemInstanceData { Guid = 7000, Entry = Fixture.Entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        Guid operation = AuctionBotCustodyLedger.OperationIdFor(AuctionBotCustodyLedger.ListingKey(77));
        Assert.Equal(EconomyOutcome.After, await fixture.ListAsync(77, item, operation));

        // A second store transaction with the same idempotency key (a lost acknowledgement being retried) is AlreadyCommitted.
        await using (CharacterDbContext db = fixture.NewContext())
        {
            var auction = (await db.Set<AuctionRow>().AsNoTracking().SingleAsync(a => a.Id == 77)).ToRecord();
            Assert.Equal(EconomyCommitResult.AlreadyCommitted, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(operation, [], [new MintEscrowItem(item), new InsertAuction(auction)])));
        }

        // The cached auction refuses a second listing under its id.
        Assert.Equal(EconomyOutcome.NotStarted, await fixture.ListAsync(77, item with { Guid = 7001 }, Guid.NewGuid()));
        Assert.Equal(1, await fixture.CountAsync<ItemInstanceRow>(i => i.Guid == 7000));
        Assert.Equal(0, await fixture.CountAsync<ItemInstanceRow>(i => i.Guid == 7001));
    }

    [Fact]
    public async Task Expired_bot_listing_destroys_its_item_and_sends_no_mail()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.CreateAsync(clock: clock);
        var item = new ItemInstanceData { Guid = 7000, Entry = Fixture.Entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        Assert.Equal(EconomyOutcome.After, await fixture.ListAsync(77, item, Guid.NewGuid(), hours: 2));
        clock.Advance(TimeSpan.FromHours(3));
        await fixture.OnWorld(() =>
        {
            fixture.Economy.RunExpirySweep();
            return true;
        });
        await fixture.Economy.DrainAsync().WaitAsync(Fixture.Budget);
        await fixture.WaitUntilAsync(() => fixture.Economy.Auctions.All(v => v.Auction.Id != 77));
        Assert.Equal(0, await fixture.CountAsync<AuctionRow>(a => a.Id == 77));
        Assert.Equal(0, await fixture.CountAsync<ItemInstanceRow>(i => i.Guid == 7000));
        Assert.Equal(0, await fixture.CountAsync<MailRow>());
    }

    [Fact]
    public async Task Buy_buys_out_a_cheap_player_auction_once_and_pays_the_seller()
    {
        await using var fixture = await Fixture.CreateAsync();
        int started = await fixture.OnWorld(() => fixture.Bot.Buy(House));
        Assert.Equal(1, started);
        await fixture.Economy.DrainAsync().WaitAsync(Fixture.Budget);
        await fixture.WaitUntilAsync(() => fixture.Bot.Ledger.Pending.Count() == 0);

        Assert.Equal(0, await fixture.CountAsync<AuctionRow>(a => a.Id == 1));
        Assert.Equal(0, await fixture.CountAsync<ItemInstanceRow>(i => i.Guid == Fixture.PlayerItemGuid));
        await using (CharacterDbContext db = fixture.NewContext())
        {
            MailRow letter = await db.Set<MailRow>().AsNoTracking().SingleAsync();
            Assert.Equal(fixture.SellerId, letter.ReceiverId);
            uint cut = AuctionHouseRules.Cut(new AuctionHouseEntry(House, 25, 15), Fixture.PlayerBuyout);
            Assert.Equal(AuctionHouseRules.Proceeds(Fixture.PlayerBuyout, Fixture.PlayerDeposit, cut), letter.Money);
            Assert.Equal(MailRules.AuctionSubject(Fixture.Entry, AuctionMailAction.Successful), letter.Subject);
        }

        AuctionBotCustodyRow row = Assert.Single(fixture.Bot.Ledger.Rows);
        Assert.Equal((AuctionBotCustodyRole.Buyout, AuctionBotCustodyState.TerminalOk, Fixture.PlayerBuyout), (row.Role, row.State, row.Amount));
        Assert.Equal(0, await fixture.OnWorld(() => fixture.Bot.Buy(House)));
        Assert.Empty(fixture.Bot.Ledger.Audit());
    }

    [Fact]
    public async Task Buyout_at_a_stale_price_or_of_a_bot_auction_is_not_started()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = new TaskCompletionSource<EconomyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.OnWorld(() =>
        {
            fixture.Economy.StartBotBuyout(1, Fixture.PlayerBuyout - 1, Guid.NewGuid(), o => result.TrySetResult(o));
            return true;
        });
        Assert.Equal(EconomyOutcome.NotStarted, await result.Task.WaitAsync(Fixture.Budget));
        Assert.Equal(1, await fixture.CountAsync<AuctionRow>(a => a.Id == 1));

        var item = new ItemInstanceData { Guid = 7000, Entry = Fixture.Entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        Assert.Equal(EconomyOutcome.After, await fixture.ListAsync(77, item, Guid.NewGuid()));
        var own = new TaskCompletionSource<EconomyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.OnWorld(() =>
        {
            fixture.Economy.StartBotBuyout(77, 100, Guid.NewGuid(), o => own.TrySetResult(o));
            return true;
        });
        Assert.Equal(EconomyOutcome.NotStarted, await own.Task.WaitAsync(Fixture.Budget));
    }

    [Fact]
    public async Task Reserved_rows_from_before_a_restart_are_rechecked_against_the_operation_ledger()
    {
        var item = new ItemInstanceData { Guid = 7000, Entry = Fixture.Entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var fixture = await Fixture.CreateAsync(persist: true, extra: new() { ["AuctionHouseBot:SellChance"] = "0", ["AuctionHouseBot:BuyChance"] = "0" },
            seed: async db =>
            {
                // Listing 77 committed but its row was never resolved (the process died); listing 78 never ran.
                var auction = new AuctionRecord
                {
                    Id = 77, HouseId = House, ItemGuid = 7000, ItemEntry = Fixture.Entry, ItemCount = 1, SellerId = 0, StartBid = 75, Buyout = 100,
                    ExpireTime = created + 3600,
                };
                Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(new EconomyCommitRequest(
                    AuctionBotCustodyLedger.OperationIdFor(AuctionBotCustodyLedger.ListingKey(77)), [], [new MintEscrowItem(item), new InsertAuction(auction)])));
                await new EfAuctionBotCustodyStore(db).SaveAsync(
                [
                    new AuctionBotCustodyRecord(AuctionBotCustodyLedger.ListingKey(77), 1, 0, 0, House, 77, 7000, Fixture.Entry, 1, 0, created, 0),
                    new AuctionBotCustodyRecord(AuctionBotCustodyLedger.ListingKey(78), 1, 0, 0, House, 78, 7001, Fixture.Entry, 1, 0, created, 0),
                ], []);
            });
        Assert.True(fixture.Bot.Persistent);
        Assert.Equal(2, await fixture.OnWorld(() => fixture.Bot.Ledger.Pending.Count()));
        await fixture.OnWorld(() =>
        {
            fixture.Bot.Tick();
            return true;
        });
        await fixture.WaitUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);
        Assert.Equal(AuctionBotCustodyState.TerminalOk, fixture.Bot.Ledger.Find(AuctionBotCustodyLedger.ListingKey(77))!.State);
        Assert.Equal(AuctionBotCustodyState.TerminalBack, fixture.Bot.Ledger.Find(AuctionBotCustodyLedger.ListingKey(78))!.State);
        await using (CharacterDbContext db = fixture.NewContext())
        {
            Dictionary<string, byte> states = await db.Set<AhBotCustodyRow>().AsNoTracking().ToDictionaryAsync(r => r.IdemKey, r => r.State);
            Assert.Equal((byte)1, states[AuctionBotCustodyLedger.ListingKey(77)]);
            Assert.Equal((byte)2, states[AuctionBotCustodyLedger.ListingKey(78)]);
        }

        // New listings are durable before they start, and never reuse an auction id a row names.
        // The day's item budget is 3: committed 77 counts, rolled-back 78 does not, so two more fit.
        Assert.Equal(2, await fixture.OnWorld(() => fixture.Bot.Sell(House)));
        await fixture.WaitUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);
        List<AuctionBotCustodyRow> fresh = [.. fixture.Bot.Ledger.Rows.Where(r => r.AuctionId > 78)];
        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, r => Assert.Equal(AuctionBotCustodyState.TerminalOk, r.State));
        foreach (AuctionBotCustodyRow row in fresh)
        {
            Assert.Equal(1, await fixture.CountAsync<AhBotCustodyRow>(r => r.IdemKey == row.IdemKey && r.State == 1));
            Assert.Equal(1, await fixture.CountAsync<AuctionRow>(a => a.Id == row.AuctionId && a.SellerId == 0));
        }
    }

    [Fact]
    public async Task Bot_bid_stands_without_a_bidder_and_wins_at_expiry_destroying_the_item_and_paying_the_seller()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        // Buy.Value 40 %: the bot values the greaves at 720-880, under the 1000 buyout but over the 500 start bid.
        await using var fixture = await Fixture.CreateAsync(clock: clock, extra: new() { ["AuctionHouseBot:BuyValuePercent"] = "40" });
        Assert.Equal(1, await fixture.OnWorld(() => fixture.Bot.Buy(House)));
        await fixture.WaitUntilAsync(() => !fixture.Bot.Ledger.Pending.Any());
        AuctionBotCustodyRow bid = Assert.Single(fixture.Bot.Ledger.Rows);
        Assert.Equal((AuctionBotCustodyRole.Bid, AuctionBotCustodyState.TerminalOk, 500u), (bid.Role, bid.State, bid.Amount));
        await using (CharacterDbContext db = fixture.NewContext())
        {
            AuctionRow row = await db.Set<AuctionRow>().AsNoTracking().SingleAsync(a => a.Id == 1);
            Assert.Equal((0, 500u), (row.BidderId, row.Bid));
        }

        Assert.Equal(0, await fixture.OnWorld(() => fixture.Bot.Buy(House))); // never over its own bid
        clock.Advance(TimeSpan.FromHours(49));
        await fixture.OnWorld(() =>
        {
            fixture.Economy.RunExpirySweep();
            return true;
        });
        await fixture.WaitUntilAsync(() => fixture.Economy.Auctions.All(v => v.Auction.Id != 1));
        Assert.Equal(0, await fixture.CountAsync<ItemInstanceRow>(i => i.Guid == Fixture.PlayerItemGuid));
        await using (CharacterDbContext db = fixture.NewContext())
        {
            MailRow letter = await db.Set<MailRow>().AsNoTracking().SingleAsync();
            Assert.Equal(fixture.SellerId, letter.ReceiverId);
            uint cut = AuctionHouseRules.Cut(new AuctionHouseEntry(House, 25, 15), 500);
            Assert.Equal(AuctionHouseRules.Proceeds(500, Fixture.PlayerDeposit, cut), letter.Money);
            Assert.Equal(MailRules.AuctionSubject(Fixture.Entry, AuctionMailAction.Successful), letter.Subject);
        }

        Assert.Equal(AuctionBotCustodyState.TerminalOk, fixture.Bot.Ledger.Find(bid.IdemKey)!.State);
        Assert.Empty(fixture.Bot.Ledger.Audit());
    }

    [Fact]
    public async Task Item_overrides_round_trip_through_the_world_table()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-ahbot-world-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ArcaneCore.Data.Content.WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new ArcaneCore.Data.Content.WorldDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var store = new EfAuctionBotItemStore(new Factory(options));
            await store.SaveAsync(new AuctionBotItemOverride(10, 500, 30, 1, 5));
            await store.SaveAsync(new AuctionBotItemOverride(10, 600, 0, 2, 2));
            await store.SaveAsync(new AuctionBotItemOverride(11, 0, 0, 1, 1));
            Assert.Equal([new AuctionBotItemOverride(10, 600, 0, 2, 2), new AuctionBotItemOverride(11, 0, 0, 1, 1)], await store.LoadAsync());
            Assert.True(await store.DeleteAsync(10));
            Assert.False(await store.DeleteAsync(10));
            Assert.Equal([new AuctionBotItemOverride(11, 0, 0, 1, 1)], await store.LoadAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class Factory(DbContextOptions<ArcaneCore.Data.Content.WorldDbContext> options) : IDbContextFactory<ArcaneCore.Data.Content.WorldDbContext>
    {
        public ArcaneCore.Data.Content.WorldDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public void Config_check_is_registered_and_reports_out_of_range_values()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuctionHouseBot:SellChance"] = "150",
            ["AuctionHouseBot:ValueRare"] = "1,2,3",
        }).Build();
        List<ConfigIssue> issues = [.. new AuctionBotConfigChecks().Check(config)];
        Assert.Contains(issues, i => i.Key == "AuctionHouseBot:SellChance" && i.Severity == ConfigSeverity.Warning);
        Assert.Contains(issues, i => i.Key == "AuctionHouseBot:ValueRare");
        Assert.Empty(new AuctionBotConfigChecks().Check(new ConfigurationBuilder().Build()));

        IConfiguration broken = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuctionHouseBot:BuyChance"] = "often",
        }).Build();
        Assert.Contains(OpsCli.Validate(broken).Issues, i => i.Key == "AuctionHouseBot" && i.Severity == ConfigSeverity.Error);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class Templates(IReadOnlyList<ItemTemplate> templates) : IItemTemplateSource
    {
        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(templates);

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
        public const uint Entry = 4000;
        public const uint PlayerItemGuid = 500;
        public const uint PlayerBuyout = 1000;
        public const uint PlayerDeposit = 50;
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private readonly ServiceProvider _services;

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, ServiceProvider services, int sellerId, TimeProvider? clock)
        {
            _path = path;
            _options = options;
            _services = services;
            SellerId = sellerId;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Items = services.GetRequiredService<ItemsFeature>();
            Items.EnsureLoadedAsync().GetAwaiter().GetResult();
            Economy = services.GetRequiredService<EconomyFeature>();
            Economy.Options.ExpirySweepSeconds = 0;
            Economy.Attach(World);
            Bot = new AuctionBotFeature(services, NullLogger<AuctionBotFeature>.Instance, clock);
            Bot.Attach(World);
            World.Start();
        }

        public int SellerId { get; }
        public WorldRuntime World { get; }
        public ItemsFeature Items { get; }
        public EconomyFeature Economy { get; }
        public AuctionBotFeature Bot { get; }
        public CharacterDbContext NewContext() => new(_options);
        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(predicate).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>>? where = null)
            where T : class
        {
            await using CharacterDbContext db = NewContext();
            IQueryable<T> set = db.Set<T>().AsNoTracking();
            return await (where is null ? set : set.Where(where)).CountAsync();
        }

        public async Task<EconomyOutcome> ListAsync(uint auctionId, ItemInstanceData item, Guid operation, uint hours = 12)
        {
            var result = new TaskCompletionSource<EconomyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            await OnWorld(() =>
            {
                Economy.StartBotListing(House, auctionId, item, 75, 100, Economy.Now + (hours * 3600L), operation, o => result.TrySetResult(o));
                return true;
            });
            return await result.Task.WaitAsync(Budget);
        }

        public static async Task<Fixture> CreateAsync(bool enabled = true, TimeProvider? clock = null, bool persist = false,
            Dictionary<string, string?>? extra = null, Func<CharacterDbContext, Task>? seed = null)
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-ahbot-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            int sellerId;
            long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                sellerId = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Seller", Race = 1, Class = 1, Level = 20 })).Id;
                var item = new ItemInstanceRow { Guid = PlayerItemGuid };
                item.CopyFrom(0, new ItemInstanceData { Guid = PlayerItemGuid, Entry = Entry, Count = 1 });
                db.Add(item);
                var auction = new AuctionRow();
                auction.CopyFrom(new AuctionRecord
                {
                    Id = 1, HouseId = House, ItemGuid = PlayerItemGuid, ItemEntry = Entry, ItemCount = 1, SellerId = sellerId,
                    StartBid = 500, Buyout = PlayerBuyout, ExpireTime = now + (48 * 3600), Deposit = PlayerDeposit,
                });
                db.Add(auction);
                await db.SaveChangesAsync();
            }

            if (seed is not null)
            {
                // A fresh context: economy commits refuse one with tracked state.
                await using var seedDb = new CharacterDbContext(options);
                await seed(seedDb);
            }

            // Green armor with a vendor price of 1000: the bot values it at 2000 ± 10 %, above the player's 1000 buyout.
            ItemTemplate template = new()
            {
                Entry = Entry, Name = "Bot test greaves", Quality = 2, Class = 4, BuyPrice = 1000, SellPrice = 250, Stackable = 1,
                RequiredLevel = 10, ItemLevel = 15, MaxDurability = 30,
            };
            var settings = new Dictionary<string, string?>
            {
                ["AuctionHouseBot:Enabled"] = enabled ? "true" : "false",
                ["AuctionHouseBot:UpdateIntervalSeconds"] = "3600",
                ["AuctionHouseBot:Houses"] = House.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AuctionHouseBot:TemplatesPerSellMin"] = "40",
                ["AuctionHouseBot:TemplatesPerSellMax"] = "40",
                ["AuctionHouseBot:DailyItemBudget"] = "3",
            };
            foreach ((string key, string? value) in extra ?? [])
            {
                settings[key] = value;
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddScoped<IItemStore>(provider => new EfItemStore(provider.GetRequiredService<CharacterDbContext>()));
            if (persist)
            {
                services.AddScoped<IAuctionBotCustodyStore>(provider => new EfAuctionBotCustodyStore(provider.GetRequiredService<CharacterDbContext>()));
            }

            services.AddSingleton<IItemTemplateSource>(new Templates([template]));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            services.AddSingleton<ItemsFeature>();
            services.AddSingleton(provider => new EconomyFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EconomyFeature>.Instance, clock));
            return new Fixture(path, options, services.BuildServiceProvider(), sellerId, clock);
        }

        public async ValueTask DisposeAsync()
        {
            await Bot.DisposeAsync();
            await Economy.DisposeAsync();
            World.Dispose();
            await _services.GetRequiredService<CharacterSaveQueue>().StopAsync();
            await _services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}
