using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Warden;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Warden;

/// <summary>World version 47 <c>warden_checks</c>: the world initializer seeds the vmangos 5875 set into an empty table and leaves an edited one alone.</summary>
public sealed class WardenCheckTableTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheSeedFillsAnEmptyTable_AndAnEditedTableIsKept(DatabaseProvider provider)
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
            List<WardenCheckRow> rows = await WardenCheckStore.LoadAsync(db);
            Assert.Equal(WardenCheckSeed.Rows.Count, rows.Count);
            WardenCheckRow door = rows.Single(r => r.Id == 97);
            Assert.Equal(4, door.Type);
            Assert.Equal("World\\KhazModan\\Blackrock\\PassiveDoodads\\Doors\\BlackRockDoorSingle.m2", door.Str);

            db.RemoveRange(rows.Where(r => r.Id != 8));
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            Assert.Equal(8u, Assert.Single(await WardenCheckStore.LoadAsync(db)).Id);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
