using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// The four gaps the AH bot doc listed: gameobject loot from spawned respawning chests only, a dynamic max level and a MaxItemLevel
/// option, a deleted seller's standing bot bid not counted as spent, and a rebuild refill that cannot take every economy transaction slot.
/// </summary>
public sealed class AuctionBotGapTests
{
    private static GameObjectTemplate Chest(uint entry, uint lootId)
    {
        var data = new uint[GameObjectTemplate.DataCount];
        data[1] = lootId;
        return new GameObjectTemplate { Entry = entry, Type = 3, Data = data };
    }

    private static GameObjectSpawn Spawn(uint guid, uint entry, int respawnSeconds, int? respawnMax = null)
        => new() { Guid = guid, Entry = entry, MapId = 0, X = 0, Y = 0, Z = 0, SpawnTimeSeconds = respawnSeconds, SpawnTimeMaxSeconds = respawnMax };

    private static ItemTemplate Item(uint entry, uint requiredLevel, uint itemLevel) => new()
    {
        Entry = entry, Quality = 2, Class = 4, BuyPrice = 1000, SellPrice = 250, Stackable = 1, RequiredLevel = requiredLevel, ItemLevel = itemLevel, Name = $"item {entry}",
    };

    // 1. Gameobject loot: only tables of objects that are spawned and respawn.

    [Fact]
    public void GameObjectLoot_TakesOnlyTheTablesOfSpawnedRespawningObjects()
    {
        var content = new GameObjectContent(
            [Chest(1000, 500), Chest(1001, 501), Chest(1002, 502), Chest(1003, 503)],
            [Spawn(1, 1000, 300), Spawn(2, 1001, 0), Spawn(3, 1003, -1, 600)],
            [], [], []);

        HashSet<uint> ids = AuctionBotLootSources.RespawningLootIds(content);

        Assert.Equal([500u, 503u], ids.Order().ToArray());   // 501: respawn 0; 502: never spawned; 503: its max delay respawns
    }

    [Fact]
    public void GameObjectLoot_ASpawnThatBecomesOtherEntriesCountsForEach()
    {
        var content = new GameObjectContent([Chest(1000, 500), Chest(1001, 501)], [Spawn(1, 1000, 300)], [], [], [], [(1u, 1001u)]);

        Assert.Equal([500u, 501u], AuctionBotLootSources.RespawningLootIds(content).Order().ToArray());
    }

    [Fact]
    public void TheGameObjectSource_DropsTablesThatNoRespawningObjectUses()
    {
        var loot = new LootContent(
            [
                (LootTableKind.GameObject, new LootStoreRow(500, 1, 100, 0, 1, 1)),
                (LootTableKind.GameObject, new LootStoreRow(501, 2, 100, 0, 1, 1)),
                (LootTableKind.GameObject, new LootStoreRow(502, 3, 100, 0, 1, 1)),
            ],
            []);
        var options = new AuctionBotOptions { LootGameobject = "1,1,1,1" };
        var chests = new GameObjectContent([Chest(1000, 500), Chest(1001, 501)], [Spawn(1, 1000, 300), Spawn(2, 1001, 0)], [], [], []);

        var filtered = new AuctionBotLootSources(loot, _ => null, options, new Random(3), null, AuctionBotLootSources.RespawningLootIds(chests));
        var unfiltered = new AuctionBotLootSources(loot, _ => null, options, new Random(3));

        Assert.Equal([500u], filtered.Sources.Single(s => s.Name == "gameobject").Tables);
        Assert.Equal([500u, 501u, 502u], unfiltered.Sources.Single(s => s.Name == "gameobject").Tables);
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal([(1u, 1u)], filtered.Roll().ToArray()));
    }

    // 2. A dynamic max level and a MaxItemLevel option.

    [Fact]
    public void TheDynamicMaxLevel_IsTheHighestOnlineLevel_AndKeepsTheCurrentOneWhenNobodyIsOnline()
    {
        Assert.Equal(34u, AuctionBotItemPool.DynamicMaxLevel([12u, 34u, 20u], 60));
        Assert.Equal(255u, AuctionBotItemPool.DynamicMaxLevel([300u], 60));
        Assert.Equal(45u, AuctionBotItemPool.DynamicMaxLevel([], 45));
    }

    [Fact]
    public void TheDynamicMaxLevel_CountsStaffAccountsUnlessIgnoreGmAccountsIsOn()
    {
        (uint, bool)[] online = [(20u, false), (60u, true), (35u, false)];

        Assert.False(new AuctionBotOptions().DynamicIgnoreGmAccounts);
        Assert.Equal(60u, AuctionBotItemPool.DynamicMaxLevel(AuctionBotItemPool.CountedOnlineLevels(online, false), 10));
        Assert.Equal(35u, AuctionBotItemPool.DynamicMaxLevel(AuctionBotItemPool.CountedOnlineLevels(online, true), 10));
        // Only staff online with the option on: nobody counts, so the cap in force stays.
        Assert.Equal(45u, AuctionBotItemPool.DynamicMaxLevel(AuctionBotItemPool.CountedOnlineLevels([(60u, true)], true), 45));
    }

    [Fact]
    public void TheItemLevelCap_FollowsMaxItemLevel_ElseTheCmangosRule()
    {
        Assert.Equal(45u, AuctionBotItemPool.ItemLevelCap(40, 0));
        Assert.Equal(uint.MaxValue, AuctionBotItemPool.ItemLevelCap(60, 0));
        Assert.Equal(70u, AuctionBotItemPool.ItemLevelCap(60, 70));
        Assert.Equal(30u, AuctionBotItemPool.ItemLevelCap(40, 30));
    }

    [Fact]
    public void ThePool_UsesTheDynamicLevelAndMaxItemLevel()
    {
        var options = new AuctionBotOptions { MaxRequiredLevel = 60 };
        var pricing = new AuctionBotPricing(options);
        ItemTemplate[] templates = [Item(1, 10, 15), Item(2, 30, 35), Item(3, 55, 60), Item(4, 20, 80)];

        Assert.Equal([1u, 2u, 3u, 4u], new AuctionBotItemPool(templates, options, pricing).All.Select(t => t.Entry).ToArray());
        Assert.Equal([1u, 2u], new AuctionBotItemPool(templates, options, pricing, maxRequiredLevel: 30).All.Select(t => t.Entry).ToArray());   // 30 + 5 caps item 4

        var capped = new AuctionBotOptions { MaxRequiredLevel = 60, MaxItemLevel = 50 };
        Assert.Equal([1u, 2u], new AuctionBotItemPool(templates, capped, pricing).All.Select(t => t.Entry).ToArray());
    }

    [Fact]
    public void TheNewOptions_DefaultToTheOldBehaviour_AndAreClamped()
    {
        var options = new AuctionBotOptions();
        Assert.False(options.DynamicMaxRequiredLevel);
        Assert.Equal(0u, options.MaxItemLevel);
        Assert.Equal(10u, options.DynamicRefreshMinutes);
        Assert.Empty(options.Normalize());

        var bad = new AuctionBotOptions { MaxItemLevel = 5000, DynamicRefreshMinutes = 0 };
        IReadOnlyList<string> fixes = bad.Normalize();
        Assert.Equal(2, fixes.Count);
        Assert.Equal(0u, bad.MaxItemLevel);
        Assert.Equal(10u, bad.DynamicRefreshMinutes);
    }

    // 3. A deleted seller's standing bot bid is not counted as spent.

    [Fact]
    public async Task ADeletedSeller_ReleasesTheBotBidOnTheirAuction_InsteadOfLeavingItToBeSettledAsWon()
    {
        await using var fixture = await BidFixture.CreateAsync();
        var raised = new List<(uint AuctionId, bool Won)>();
        await fixture.OnWorld(() =>
        {
            fixture.Economy.BotBidSettled += (id, won) => raised.Add((id, won));
            fixture.Economy.ResyncDeletedCharacter(fixture.SellerId, [], new Dictionary<uint, ItemInstanceData>(), []);
            return true;
        });

        Assert.Equal([(BidFixture.AuctionId, false)], raised);
        Assert.Null(await fixture.OnWorld(() => fixture.Economy.FindAuction(BidFixture.AuctionId)));
    }

    [Fact]
    public async Task ADeletedSeller_WithoutABotBidRaisesNothing()
    {
        await using var fixture = await BidFixture.CreateAsync(botBid: 0);
        var raised = new List<(uint, bool)>();
        Assert.NotNull(await fixture.OnWorld(() => fixture.Economy.FindAuction(BidFixture.AuctionId)));
        await fixture.OnWorld(() =>
        {
            fixture.Economy.BotBidSettled += (id, won) => raised.Add((id, won));
            fixture.Economy.ResyncDeletedCharacter(fixture.SellerId, [], new Dictionary<uint, ItemInstanceData>(), []);
            return true;
        });

        Assert.Empty(raised);
    }

    // 4. A rebuild refill cannot hold all 16 economy transaction slots.

    [Fact]
    public void ARebuildRefill_IsCappedBelowTheEconomysTransactionSlots()
    {
        Assert.True(AuctionBotFeature.RebuildMaxInFlight > 0);
        Assert.True(AuctionBotFeature.RebuildMaxInFlight < EconomySettlements.MaxConcurrentOperations);
        Assert.Equal(AuctionBotFeature.RebuildMaxInFlight, AuctionBotFeature.RebuildRefillShare(0));
        Assert.Equal(AuctionBotFeature.RebuildMaxInFlight - 3, AuctionBotFeature.RebuildRefillShare(3));
        Assert.Equal(0, AuctionBotFeature.RebuildRefillShare(AuctionBotFeature.RebuildMaxInFlight));
        Assert.Equal(0, AuctionBotFeature.RebuildRefillShare(EconomySettlements.MaxConcurrentOperations + 5));
    }

    /// <summary>An economy over a SQLite store holding one player auction that carries a standing bot bid (bidder 0, bid above 0).</summary>
    private sealed class BidFixture : IAsyncDisposable
    {
        public const uint AuctionId = 1;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
        private readonly string _path;
        private readonly ServiceProvider _services;

        private BidFixture(string path, ServiceProvider services, int sellerId)
        {
            _path = path;
            _services = services;
            SellerId = sellerId;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Economy = services.GetRequiredService<EconomyFeature>();
            Economy.Options.ExpirySweepSeconds = 0;
            Economy.Attach(World);
            World.Start();
        }

        public int SellerId { get; }

        public WorldRuntime World { get; }

        public EconomyFeature Economy { get; }

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public static async Task<BidFixture> CreateAsync(uint botBid = 750)
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-ahbot-gap-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            int sellerId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                sellerId = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Seller", Race = 1, Class = 1, Level = 20 })).Id;
                var item = new ItemInstanceRow { Guid = 500 };
                item.CopyFrom(0, new ItemInstanceData { Guid = 500, Entry = 4000, Count = 1 });
                db.Add(item);
                var auction = new AuctionRow();
                auction.CopyFrom(new AuctionRecord
                {
                    Id = AuctionId, HouseId = AuctionHouseRules.NeutralHouse, ItemGuid = 500, ItemEntry = 4000, ItemCount = 1, SellerId = sellerId,
                    StartBid = 500, Buyout = 2000, ExpireTime = now + (48 * 3600), Deposit = 50, BidderId = 0, Bid = botBid,
                });
                db.Add(auction);
                await db.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddScoped<IItemStore>(provider => new EfItemStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            services.AddSingleton(provider => new EconomyFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EconomyFeature>.Instance, null));
            return new BidFixture(path, services.BuildServiceProvider(), sellerId);
        }

        public async ValueTask DisposeAsync()
        {
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
