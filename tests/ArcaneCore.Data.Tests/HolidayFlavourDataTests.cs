using ArcaneCore.Data.Content;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>holiday_flavour</c> (<see cref="HolidayFlavourDataModule"/>) and its shipped content under <c>sql/content/world</c>: the schema step
/// creates the table, the store reads rows back by holiday id, and every provider ships the same rows, deleting its own ids before inserting.
/// </summary>
public sealed class HolidayFlavourDataTests : IAsyncLifetime
{
    private static readonly string[] s_providers = ["sqlite", "mariadb", "postgresql"];

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static string ContentFile(string provider)
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        return Path.Combine(root, "sql", "content", "world", provider, "0002_holiday_flavour.sql");
    }

    [Fact]
    public void WorldStep_IsTheHolidayFlavourTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == HolidayFlavourDataModule.Version);
        Assert.Equal(["holiday_flavour"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is HolidayFlavourDataModule);
    }

    [Fact]
    public async Task Store_ReadsTheRows_ByHolidayId()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.AddRange(
                new HolidayFlavourRow { HolidayId = 341, FlavourKey = "midsummer", Name = "Midsummer", Emote1 = 34, Emote2 = 21, Gather = 1 },
                new HolidayFlavourRow { HolidayId = 0, FlavourKey = "festival", Name = string.Empty, Emote1 = 21, Emote2 = 101, Emote3 = 34, Gather = 1 },
                new HolidayFlavourRow { HolidayId = 301, FlavourKey = "fishing", Name = "Fishing Extravaganza", Gather = 0 });
            await db.SaveChangesAsync();
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        IReadOnlyList<HolidayFlavourRecord> rows = await new EfHolidayFlavourStore(read).LoadAsync();

        Assert.Equal([0u, 301u, 341u], rows.Select(r => r.HolidayId));
        Assert.Equal(new HolidayFlavourRecord(341, "midsummer", "Midsummer", 34, 21, 0, true), rows[2]);
        Assert.False(rows[1].Gather);
    }

    [Fact]
    public async Task TheShippedContent_InstallsOnACleanDatabase_AndAgainToTheSameRows_LeavingOtherIdsAlone()
    {
        string script = await File.ReadAllTextAsync(ContentFile("sqlite"));
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new HolidayFlavourRow { HolidayId = 9999, FlavourKey = "custom", Name = "Custom" });
            await db.SaveChangesAsync();
        }

        await Run(cs, script);
        IReadOnlyList<HolidayFlavourRecord> first = await Read(cs);
        await Run(cs, script);
        IReadOnlyList<HolidayFlavourRecord> second = await Read(cs);

        Assert.Equal(first, second);
        Assert.Equal([0u, 141u, 181u, 201u, 301u, 321u, 324u, 327u, 335u, 341u, 374u, 375u, 9999u], second.Select(r => r.HolidayId));
        Assert.Equal("custom", second[^1].FlavourKey);
    }

    [Fact]
    public async Task TheCodeSeed_EqualsTheShippedContentFile_RowForRow()
    {
        string script = await File.ReadAllTextAsync(ContentFile("sqlite"));
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        await Run(cs, script);

        Assert.Equal(await Read(cs), HolidayFlavourSeed.Rows);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheInitializerSeedsAnEmptyTable_AndAnEditedTableIsKept(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddWorldDatabase(configuration);
        await using ServiceProvider services = collection.BuildServiceProvider();

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            Assert.Equal(HolidayFlavourSeed.Rows, await new EfHolidayFlavourStore(db).LoadAsync());
            Assert.Contains(HolidayFlavourSeed.Rows, r => r is { HolidayId: 301, Gather: false });
            db.RemoveRange(db.Set<HolidayFlavourRow>().Where(r => r.HolidayId != 341));
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            Assert.Equal(341u, Assert.Single(await new EfHolidayFlavourStore(db).LoadAsync()).HolidayId);
        }
    }

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public async Task EveryProviderShipsTheSameRows_AndDeletesBeforeInserting(string provider)
    {
        Assert.Contains(provider, s_providers);
        string script = await File.ReadAllTextAsync(ContentFile(provider));
        string sqlite = await File.ReadAllTextAsync(ContentFile("sqlite"));
        char quote = provider == "mariadb" ? '`' : '"';
        int delete = script.IndexOf($"DELETE FROM {quote}holiday_flavour{quote}", StringComparison.Ordinal);
        int insert = script.IndexOf($"INSERT INTO {quote}holiday_flavour{quote}", StringComparison.Ordinal);

        Assert.True(delete >= 0, $"{provider}: no DELETE FROM holiday_flavour");
        Assert.True(insert > delete, $"{provider}: the INSERT must follow the DELETE");
        Assert.Equal(Rows(sqlite), Rows(script));
    }

    private static string[] Rows(string script) => [.. script.Split('\n').Where(l => l.StartsWith('(')).Select(l => l.TrimEnd(',', ';', '\r'))];

    private static async Task Run(DatabaseConnectionOptions cs, string script)
    {
        await using var connection = new SqliteConnection(cs.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<HolidayFlavourRecord>> Read(DatabaseConnectionOptions cs)
    {
        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        return await new EfHolidayFlavourStore(read).LoadAsync();
    }
}
