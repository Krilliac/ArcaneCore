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
    public async Task Tick_before_the_items_are_loaded_does_not_run_the_item_load_on_the_calling_thread()
    {
        var source = new ThreadRecordingTemplates();
        await using var fixture = await Fixture.CreateAsync(templateSource: source, loadItems: false);

        fixture.Bot.Tick(); // stands for the world thread: SQLite runs async calls inline, so the load must be handed to the pool

        int loadThread = await source.LoadThread.WaitAsync(Fixture.Budget);
        Assert.NotEqual(Environment.CurrentManagedThreadId, loadThread);
    }

    [Fact]
    public async Task Reload_reads_the_ahbot_items_off_the_world_thread_and_replies_on_it()
    {
        var itemStore = new ThreadRecordingItemStore();
        await using var fixture = await Fixture.CreateAsync(itemStore: itemStore);
        var replied = new TaskCompletionSource<(int Thread, int Overrides)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int worldThread = await fixture.OnWorld(() => Environment.CurrentManagedThreadId);

        await fixture.OnWorld(() =>
        {
            fixture.Bot.Reload((_, _) => replied.TrySetResult((Environment.CurrentManagedThreadId, fixture.Bot.Overrides.Count)));
            return true;
        });

        (int thread, int overrides) = await replied.Task.WaitAsync(Fixture.Budget);
        Assert.Equal(worldThread, thread);
        Assert.Equal(1, overrides);
        Assert.NotEqual(worldThread, itemStore.LastLoadThread);
    }

    [Fact]
    public async Task Reload_that_read_its_rows_before_a_SetItemData_does_not_overwrite_the_change()
    {
        var itemStore = new GatedItemStore();
        await using var fixture = await Fixture.CreateAsync(itemStore: itemStore);
        itemStore.Hold = true;
        ItemTemplate template = fixture.Items.Templates.Find(Fixture.Entry)!;
        var reloaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await fixture.OnWorld(() =>
        {
            fixture.Bot.Reload((_, applied) => reloaded.TrySetResult(applied));
            return true;
        });
        await itemStore.Read(0).Started.WaitAsync(Fixture.Budget);
        await fixture.OnWorld(() => fixture.Bot.SetItemData(template, new AuctionBotItemOverride(Fixture.Entry, 999, 50, 1, 1)));
        itemStore.Read(0).Release([new AuctionBotItemOverride(Fixture.Entry, 7, 100, 1, 1)]);

        Assert.False(await reloaded.Task.WaitAsync(Fixture.Budget)); // superseded: reported, not silently applied
        Assert.Equal(999u, await fixture.OnWorld(() => fixture.Bot.Overrides[Fixture.Entry].Value));
    }

    [Fact]
    public async Task Two_reloads_finishing_in_reverse_order_apply_only_the_later_started_one()
    {
        var itemStore = new GatedItemStore();
        await using var fixture = await Fixture.CreateAsync(itemStore: itemStore);
        itemStore.Hold = true;
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await fixture.OnWorld(() =>
        {
            fixture.Bot.Reload((_, applied) => first.TrySetResult(applied));
            return true;
        });
        await itemStore.Read(0).Started.WaitAsync(Fixture.Budget);
        await fixture.OnWorld(() =>
        {
            fixture.Bot.Reload((_, applied) => second.TrySetResult(applied));
            return true;
        });
        await itemStore.Read(1).Started.WaitAsync(Fixture.Budget);

        itemStore.Read(1).Release([new AuctionBotItemOverride(Fixture.Entry, 22, 100, 1, 1)]);
        Assert.True(await second.Task.WaitAsync(Fixture.Budget));
        itemStore.Read(0).Release([new AuctionBotItemOverride(Fixture.Entry, 11, 100, 1, 1)]);
        Assert.False(await first.Task.WaitAsync(Fixture.Budget));

        Assert.Equal(22u, await fixture.OnWorld(() => fixture.Bot.Overrides[Fixture.Entry].Value));
    }

    [Fact]
    public async Task Sell_mints_each_listing_once_as_seller_zero_and_the_ledger_balances()
    {
        await using var fixture = await Fixture.CreateAsync(extra: new() { ["AuctionHouseBot:SellChance"] = "0", ["AuctionHouseBot:BuyChance"] = "0" });
        int started = await fixture.OnWorld(() => fixture.Bot.Sell(House));
        Assert.Equal(3, started); // the daily item budget
        await fixture.TickUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);

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
    public async Task A_listing_whose_commit_is_unknown_still_fills_the_house_cap()
    {
        // The insert committed, then its acknowledgement was lost: the auction is reserved and absent from the cache.
        // It still occupies the one slot of this house, so another sell pass must not mint a second one.
        var lost = new LostBotListingAck();
        await using var fixture = await Fixture.CreateAsync(wrapStore: lost.Wrap, extra: new()
        {
            ["AuctionHouseBot:SellChance"] = "0",
            ["AuctionHouseBot:BuyChance"] = "0",
            ["AuctionHouseBot:MaxAuctionsPerHouse"] = "1",
        });
        Assert.Equal(1u, fixture.Bot.Options.MaxAuctionsPerHouse);
        Assert.Equal(1, await fixture.OnWorld(() => fixture.Bot.Sell(House)));
        await fixture.WaitUntilAsync(() =>
        {
            AuctionBotCustodyRow? pending = fixture.Bot.Ledger.Pending.SingleOrDefault();
            return pending is not null && fixture.Economy.IsAuctionQuarantined(pending.AuctionId)
                && fixture.Economy.FindAuction(pending.AuctionId) is null;
        });

        Assert.Equal(1, await fixture.CountAsync<AuctionRow>(a => a.SellerId == EconomyFeature.BotSellerId));
        await fixture.OnWorld(() =>
        {
            Assert.DoesNotContain(fixture.Economy.Auctions, view => view.Auction.SellerId == EconomyFeature.BotSellerId);
            Assert.Equal(1, fixture.Economy.BotAuctionCount(House));
            Assert.Equal(0, fixture.Bot.Sell(House));
            return true;
        });
        Assert.Equal(1, await fixture.CountAsync<AuctionRow>(a => a.SellerId == EconomyFeature.BotSellerId));
        Assert.Single(fixture.Bot.Ledger.Pending);
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
        await using var fixture = await Fixture.CreateAsync(extra: new() { ["AuctionHouseBot:SellChance"] = "0", ["AuctionHouseBot:BuyChance"] = "0" });
        int started = await fixture.OnWorld(() => fixture.Bot.Buy(House));
        Assert.Equal(1, started);
        await fixture.TickUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);

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

    [Theory]
    [InlineData(0)]
    [InlineData(2)] // both rechecks outlive the economy read budget, as a loaded host can make them
    public async Task Reserved_rows_from_before_a_restart_are_rechecked_against_the_operation_ledger(int stalledRechecks)
    {
        var reads = new StallingCommitReads(stalledRechecks);
        var item = new ItemInstanceData { Guid = 7000, Entry = Fixture.Entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] };
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var fixture = await Fixture.CreateAsync(persist: true, wrapStore: reads.Wrap,
            extra: new() { ["AuctionHouseBot:SellChance"] = "0", ["AuctionHouseBot:BuyChance"] = "0" },
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

        // A recheck the store cannot answer within its budget settles nothing: the row keeps its budget until a later tick asks again.
        await reads.AllExpired.WaitAsync(Fixture.Budget);
        if (stalledRechecks > 0)
        {
            Assert.Equal(2, await fixture.OnWorld(() => fixture.Bot.Ledger.Pending.Count()));
        }

        // The update timer keeps ticking in production (AuctionHouseBot:UpdateIntervalSeconds); here the test is the timer.
        await fixture.TickUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);
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
        await fixture.TickUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);
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
        var stalls = new StallingCommitReads(0, stalledCommits: 1);
        // Buy.Value 40 %: the bot values the greaves at 720-880, under the 1000 buyout but over the 500 start bid.
        await using var fixture = await Fixture.CreateAsync(clock: clock, wrapStore: stalls.Wrap,
            extra: new() { ["AuctionHouseBot:BuyValuePercent"] = "40", ["AuctionHouseBot:SellChance"] = "0", ["AuctionHouseBot:BuyChance"] = "0" });
        Assert.Equal(1, await fixture.OnWorld(() => fixture.Bot.Buy(House)));
        await fixture.TickUntilAsync(() => !fixture.Bot.Ledger.Pending.Any() && !fixture.Bot.HasUnsavedChanges);
        Assert.Equal(AuctionBotCustodyState.TerminalBack, Assert.Single(fixture.Bot.Ledger.Rows).State);
        // The first commit was cancelled by its own budget before storage began. A later bot pass retries the still-open auction.
        AuctionBotCustodyRow bid = await fixture.BuyUntilCommittedAsync();
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

    /// <summary>
    /// A bot listing's commit succeeds and then the acknowledgement is lost, and neither the ledger re-read nor a recovery
    /// snapshot can be answered. The auction stays reserved and out of the cache, which is the window where the house cap
    /// used to see an empty slot.
    /// </summary>
    private sealed class LostBotListingAck
    {
        public IEconomyStore Wrap(IEconomyStore inner) => new Store(inner);

        private sealed class Store(IEconomyStore inner) : IEconomyStore
        {
            public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
            {
                EconomyCommitResult result = await inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
                if (request.Changes.Any(change => change is InsertAuction { Auction.SellerId: EconomyFeature.BotSellerId }))
                {
                    throw new IOException("lost acknowledgement of a bot listing");
                }

                return result;
            }

            public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
                => Task.FromException<bool>(new IOException("bot listing reconciliation unavailable"));

            public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
                => filter.AuctionId is not null
                    ? Task.FromException<AuctionSnapshot>(new IOException("bot listing recovery snapshot unavailable"))
                    : inner.GetAuctionSnapshotAsync(filter, cancellationToken);

            public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
                => inner.GetMailsAsync(receiverId, cancellationToken);

            public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
                => inner.GetExpiredMailsAsync(now, max, cancellationToken);

            public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
                => inner.GetMailsInvolvingAsync(characterId, cancellationToken);

            public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
                => inner.GetItemTextAsync(itemTextId, cancellationToken);

            public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
                => inner.GetAuctionsAsync(cancellationToken);

            public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids,
                CancellationToken cancellationToken = default)
                => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);

            public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
                => inner.GetIdSeedAsync(cancellationToken);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A template source that records the thread its load ran on (the calling thread when the load is started inline).</summary>
    private sealed class ThreadRecordingTemplates : IItemTemplateSource
    {
        private readonly TaskCompletionSource<int> _loadThread = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> LoadThread => _loadThread.Task;

        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
        {
            _loadThread.TrySetResult(Environment.CurrentManagedThreadId);
            return Task.FromResult<IReadOnlyList<ItemTemplate>>([]);
        }

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    /// <summary>An <c>ahbot_items</c> store with one row that records the thread of its latest load.</summary>
    private sealed class ThreadRecordingItemStore : IAuctionBotItemStore
    {
        private int _lastLoadThread;

        public int LastLoadThread => Volatile.Read(ref _lastLoadThread);

        public Task<IReadOnlyList<AuctionBotItemOverride>> LoadAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _lastLoadThread, Environment.CurrentManagedThreadId);
            return Task.FromResult<IReadOnlyList<AuctionBotItemOverride>>([new AuctionBotItemOverride(Fixture.Entry, 7, 100, 1, 1)]);
        }

        public Task SaveAsync(AuctionBotItemOverride value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(uint item, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>
    /// An <c>ahbot_items</c> store whose loads are answered at once until <see cref="Hold"/> is set, then each load waits for the
    /// test to release it with the rows to return (so a test chooses the order the reads finish in).
    /// </summary>
    private sealed class GatedItemStore : IAuctionBotItemStore
    {
        private readonly object _gate = new();
        private readonly List<HeldRead> _reads = [];

        public volatile bool Hold;

        /// <summary>The held read number <paramref name="index"/> (created on first use, so the test can wait for a read that has not started).</summary>
        public HeldRead Read(int index)
        {
            lock (_gate)
            {
                while (_reads.Count <= index)
                {
                    _reads.Add(new HeldRead());
                }

                return _reads[index];
            }
        }

        private int _next;

        public async Task<IReadOnlyList<AuctionBotItemOverride>> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (!Hold)
            {
                return [new AuctionBotItemOverride(Fixture.Entry, 7, 100, 1, 1)];
            }

            HeldRead read = Read(Interlocked.Increment(ref _next) - 1);
            read.StartedSource.TrySetResult();
            return await read.Rows.Task.ConfigureAwait(false);
        }

        public Task SaveAsync(AuctionBotItemOverride value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(uint item, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public sealed class HeldRead
        {
            public TaskCompletionSource StartedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<IReadOnlyList<AuctionBotItemOverride>> Rows { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Started => StartedSource.Task;

            public void Release(IReadOnlyList<AuctionBotItemOverride> rows) => Rows.TrySetResult(rows);
        }
    }

    private sealed class Templates(IReadOnlyList<ItemTemplate> templates) : IItemTemplateSource
    {
        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(templates);

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    /// <summary>
    /// The first <c>stalls</c> operation-ledger reads never answer before their budget runs out (what a starved thread pool or a busy
    /// disk does to a loaded host); every other call goes straight to the real store.
    /// </summary>
    private sealed class StallingCommitReads(int stalls, int stalledCommits = 0)
    {
        private readonly int _stalls = stalls;
        private int _remainingCommits = stalledCommits;
        private int _remaining = stalls;
        private int _expired;
        private readonly TaskCompletionSource _allExpired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once every stalled read has been abandoned by its budget.</summary>
        public Task AllExpired => _stalls == 0 ? Task.CompletedTask : _allExpired.Task;

        public IEconomyStore Wrap(IEconomyStore inner) => new Store(inner, this);

        private sealed class Store(IEconomyStore inner, StallingCommitReads owner) : IEconomyStore
        {
            public async Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Decrement(ref owner._remaining) >= 0)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    finally
                    {
                        if (Interlocked.Increment(ref owner._expired) == owner._stalls)
                        {
                            owner._allExpired.TrySetResult();
                        }
                    }
                }

                return await inner.IsCommittedAsync(operationId, cancellationToken);
            }

            public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Decrement(ref owner._remainingCommits) >= 0)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return await inner.CommitAsync(request, cancellationToken);
            }

            public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
                => inner.GetMailsAsync(receiverId, cancellationToken);

            public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
                => inner.GetExpiredMailsAsync(now, max, cancellationToken);

            public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
                => inner.GetMailsInvolvingAsync(characterId, cancellationToken);

            public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
                => inner.GetItemTextAsync(itemTextId, cancellationToken);

            public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
                => inner.GetAuctionsAsync(cancellationToken);

            public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids,
                CancellationToken cancellationToken = default)
                => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);

            public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
                => inner.GetAuctionSnapshotAsync(filter, cancellationToken);

            public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
                => inner.GetIdSeedAsync(cancellationToken);
        }
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

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, ServiceProvider services, int sellerId, TimeProvider? clock, bool loadItems)
        {
            _path = path;
            _options = options;
            _services = services;
            SellerId = sellerId;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Items = services.GetRequiredService<ItemsFeature>();
            if (loadItems)
            {
                Items.EnsureLoadedAsync().GetAwaiter().GetResult();
            }

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

        /// <summary>
        /// Wait as the bot's update timer would: tick, then check. A row whose outcome a read could not learn in time (a loaded host can
        /// spend the whole economy read budget) is only rechecked by a later tick, so waiting without ticking can wait forever.
        /// Only for tests whose sell and buy chances are 0, so a tick reconciles and flushes but starts nothing.
        /// </summary>
        public async Task TickUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(() =>
                   {
                       Assert.Equal(0u, Bot.Options.SellChance);
                       Assert.Equal(0u, Bot.Options.BuyChance);
                       if (predicate())
                       {
                           return true;
                       }

                       Bot.Tick();
                       return false;
                   }).WaitAsync(timeout.Token))
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        public async Task<AuctionBotCustodyRow> BuyUntilCommittedAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (true)
            {
                AuctionBotCustodyRow? committed = await OnWorld(() =>
                {
                    Assert.Equal(0u, Bot.Options.SellChance);
                    Assert.Equal(0u, Bot.Options.BuyChance);
                    AuctionBotCustodyRow? standing = Bot.Ledger.Rows.SingleOrDefault(r =>
                        r.Role == AuctionBotCustodyRole.Bid && r.State == AuctionBotCustodyState.TerminalOk);
                    if (standing is not null)
                    {
                        return standing;
                    }

                    Economy.RecoverAuctions();
                    if (!Bot.Ledger.Pending.Any() && !Bot.HasUnsavedChanges && !Economy.IsAuctionBusy(1))
                    {
                        Assert.Equal(1, Bot.Buy(House));
                    }

                    Bot.Tick();
                    return null;
                }).WaitAsync(timeout.Token);
                if (committed is not null)
                {
                    return committed;
                }

                await Task.Delay(50, timeout.Token);
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
            Dictionary<string, string?>? extra = null, Func<CharacterDbContext, Task>? seed = null, Func<IEconomyStore, IEconomyStore>? wrapStore = null,
            IItemTemplateSource? templateSource = null, bool loadItems = true, IAuctionBotItemStore? itemStore = null)
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
            services.AddScoped<IEconomyStore>(provider =>
            {
                IEconomyStore store = new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>());
                return wrapStore is null ? store : wrapStore(store);
            });
            services.AddScoped<IItemStore>(provider => new EfItemStore(provider.GetRequiredService<CharacterDbContext>()));
            if (persist)
            {
                services.AddScoped<IAuctionBotCustodyStore>(provider => new EfAuctionBotCustodyStore(provider.GetRequiredService<CharacterDbContext>()));
            }

            services.AddSingleton(templateSource ?? new Templates([template]));
            if (itemStore is not null)
            {
                services.AddSingleton(itemStore);
            }

            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            services.AddSingleton<ItemsFeature>();
            services.AddSingleton(provider => new EconomyFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EconomyFeature>.Instance, clock));
            return new Fixture(path, options, services.BuildServiceProvider(), sellerId, clock, loadItems);
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
