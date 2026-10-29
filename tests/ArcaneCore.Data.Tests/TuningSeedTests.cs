using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Tuning;
using ArcaneCore.Kernel.Tuning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// World version 62 tuning tables: the world initializer seeds <see cref="TuningDefaults"/> into each empty table (a fresh or upgraded
/// database does not start with empty tables, so no behaviour changes), and leaves a table an operator edited alone.
/// </summary>
public sealed class TuningSeedTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheInitializerSeedsEmptyTables_AndAnEditedTableIsKept(DatabaseProvider provider)
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
            TuningContent content = await new EfTuningContentStore(db).LoadAsync();
            Assert.Equal(TuningDefaults.Content.RowCount, content.RowCount);
            Assert.Empty(TuningCatalog.Build(content).Rejected);

            db.RemoveRange(await db.Set<PlayerbotShipRouteRow>().Where(r => r.Entry != 20808).ToListAsync());
            db.RemoveRange(await db.Set<PlayerbotTramNetworkRow>().ToListAsync());
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            TuningContent content = await new EfTuningContentStore(db).LoadAsync();
            Assert.Equal(20808u, Assert.Single(content.Ships).Entry);
            Assert.Equal(new TramNetworkRecord(369, 60f), Assert.Single(content.Trams));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
