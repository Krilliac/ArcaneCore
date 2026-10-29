using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Tuning;
using ArcaneCore.Kernel.Tuning;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The plain tuning tables (<see cref="TuningDataModule"/>, rule engine phase P5): the schema step creates them, the store seeds only empty
/// tables with <see cref="TuningDefaults"/>, reads every row back in key order, and the catalog built from the rows equals the shipped one.
/// </summary>
public sealed class TuningDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public void WorldStep_CreatesTheFiveTuningTables()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == TuningDataModule.Version);
        Assert.Equal(
            ["auction_bot_value", "auction_bot_loot", "playerbot_ship_route", "playerbot_elevator_region", "playerbot_tram_network"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is TuningDataModule);
    }

    [Fact]
    public async Task Seed_FillsEmptyTables_AndTheCatalogOfTheRowsEqualsTheShippedOne()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(0, (await new EfTuningContentStore(db).LoadAsync()).RowCount);
            Assert.Equal(TuningDefaults.Content.RowCount, await new EfTuningContentStore(db).SeedEmptyTablesAsync(TuningDefaults.Content));
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        TuningContent content = await new EfTuningContentStore(read).LoadAsync();
        Assert.Equal(119, content.Values.Count);
        Assert.Equal(9, content.Loot.Count);
        Assert.Equal(8, content.Ships.Count);
        Assert.Equal(7, content.Elevators.Count);
        Assert.Equal(new TramNetworkRecord(369, 60f), Assert.Single(content.Trams));
        TuningCatalog catalog = TuningCatalog.Build(content);
        Assert.Empty(catalog.Rejected);
        Assert.Equal(TuningCatalog.Shipped.ValueMatrix(), catalog.ValueMatrix());
        foreach (string source in TuningCatalog.LootSources)
        {
            Assert.Equal(TuningCatalog.Shipped.LootConfig(source), catalog.LootConfig(source));
        }

        Assert.Equal(TuningCatalog.Shipped.Ships, catalog.Ships);
        Assert.Equal(TuningCatalog.Shipped.Elevators, catalog.Elevators);
        Assert.Equal(TuningCatalog.Shipped.Tram, catalog.Tram);
    }

    [Fact]
    public async Task Seed_LeavesATableThatHoldsRowsAsTheOperatorHasIt()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new PlayerbotTramNetworkRow { MapId = 369, PairYards = 75f });
        await db.SaveChangesAsync();

        int written = await new EfTuningContentStore(db).SeedEmptyTablesAsync(TuningDefaults.Content);

        Assert.Equal(TuningDefaults.Content.RowCount - 1, written);
        Assert.Equal(75f, Assert.Single((await new EfTuningContentStore(db).LoadAsync()).Trams).PairYards);
        Assert.Equal(0, await new EfTuningContentStore(db).SeedEmptyTablesAsync(TuningDefaults.Content));
    }

    [Fact]
    public void Build_RejectsBadRowsAndKeepsTheGoodOnes()
    {
        var content = new TuningContent
        {
            Values = [new(1, 0, 150), new(9, 0, 1), new(1, 20, 1), new(1, 0, 2), new(2, 3, -1)],
            Loot = [new("fishing", 3, 5, 30, 40), new("nonsense", 1, 1, 1, 1), new("skinning", 6, 5, 1, 1), new("gameobject", 1, 2, 5, 4)],
            Ships = [new(1, TuningCatalog.ShipMeansBoat, 0, "ok"), new(2, 9, 0, "bad means"), new(3, TuningCatalog.ShipMeansBoat, 7, "bad access"), new(4, 2, 0, " ")],
            Elevators = [new(1, 1, 1f, 2f, 5, 0, 6, 0, false, false, "ok"), new(2, 1, float.NaN, 2f, 5, 0, 6, 0, false, false, "nan"), new(3, 1, 1f, 2f, 0, 0, 6, 0, false, false, "no zone")],
            Trams = [new(369, 60f), new(370, 60f), new(371, -1f)],
        };

        TuningCatalog catalog = TuningCatalog.Build(content);

        Assert.Equal(150u, catalog.Value(1, 0));
        Assert.Equal(0u, catalog.Value(2, 3));
        Assert.Equal([30, 40], catalog.LootConfig("fishing")[2..]);
        Assert.Equal(new int[4], catalog.LootConfig("skinning"));
        Assert.Equal([1u], catalog.Ships.Select(r => r.Entry));
        Assert.Equal([1], catalog.Elevators.Select(r => r.Id));
        Assert.Equal(369u, catalog.Tram!.MapId);
        Assert.Equal(
            ["auction_bot_value 9/0", "auction_bot_value 1/20", "auction_bot_value 1/0", "auction_bot_value 2/3", "auction_bot_loot nonsense",
                "auction_bot_loot skinning", "auction_bot_loot gameobject", "playerbot_ship_route 2", "playerbot_ship_route 3", "playerbot_ship_route 4",
                "playerbot_elevator_region 2", "playerbot_elevator_region 3", "playerbot_tram_network 370", "playerbot_tram_network 371"],
            catalog.Rejected.Select(r => $"{r.Table} {r.Key}"));
    }
}
