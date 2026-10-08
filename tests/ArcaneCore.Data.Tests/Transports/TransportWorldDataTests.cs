using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Transports;
using ArcaneCore.Kernel.WorldData.Transports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Transports;

/// <summary>
/// World schema 41 (<see cref="TransportWorldDataModule"/>): the vmangos <c>transports</c> table, its upgrade from the version below,
/// the store, and the build selection of <c>TransportMgr::LoadTransportTemplates</c>.
/// </summary>
public sealed class TransportWorldDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public void Module_IsWorldVersion45_WithOneNewTable()
    {
        var module = new TransportWorldDataModule();

        Assert.Equal(41, TransportWorldDataModule.Version);
        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal("transports", Assert.IsType<CreateTableChange>(Assert.Single(module.SchemaChanges)).Table);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= TransportWorldDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromVersion44_KeepsRows_CreatesTheTable_AndTheStoreReadsIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, SchemaProbe.ThroughVersion(WorldDbContext.Schema, TransportWorldDataModule.Version - 1));
            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 1, Class = 1, MapId = 0, X = 13 });
            await db.SaveChangesAsync();

            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema); // a second start changes nothing
            Assert.Equal(13f, (await db.PlayerCreateInfo.SingleAsync()).X);
            db.Set<TransportRow>().AddRange(
                new TransportRow { Entry = 20808, Build = 0, Name = "Ratchet and Booty Bay", Period = 339575 },
                new TransportRow { Entry = 20808, Build = 4695, Name = "Ratchet and Booty Bay", Period = 350818 },
                new TransportRow { Entry = 175080, Build = 0, Name = "Grom'Gol Base Camp and Orgrimmar", Period = 303463 });
            await db.SaveChangesAsync();
        }

        var store = new EfTransportDataStore(new Factory(connection));
        IReadOnlyList<TransportPeriodRow> rows = await store.LoadAsync();

        Assert.Equal(
            [(20808u, (ushort)0, 339575u), (20808u, (ushort)4695, 350818u), (175080u, (ushort)0, 303463u)],
            rows.Select(r => (r.Entry, r.Build, r.Period)));
        Assert.Equal("Grom'Gol Base Camp and Orgrimmar", rows[2].Name);
        IReadOnlyDictionary<uint, uint> periods = TransportPeriods.Select(rows);
        Assert.Equal(350818u, periods[20808]);
        Assert.Equal(303463u, periods[175080]);
    }

    [Fact]
    public void Select_TakesTheNewestBuildAtOrBelow5875_PerEntry()
    {
        TransportPeriodRow[] rows =
        [
            new(1, 0, "a", 100),
            new(1, 4695, "a", 200),
            new(1, 6005, "a", 300), // a later client's period is ignored
            new(2, 6005, "b", 400), // only later builds: no override
        ];

        IReadOnlyDictionary<uint, uint> periods = TransportPeriods.Select(rows);

        Assert.Equal(200u, periods[1]);
        Assert.False(periods.ContainsKey(2));
    }

    [Fact]
    public async Task ProductionRegistration_ProvidesTheScopedStore()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = connection.ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorldDatabase(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using (WorldDbContext db = await provider.GetRequiredService<IDbContextFactory<WorldDbContext>>().CreateDbContextAsync())
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        using IServiceScope scope = provider.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ITransportDataStore>().LoadAsync());
    }

    private sealed class Factory(DatabaseConnectionOptions connection) : IDbContextFactory<WorldDbContext>
    {
        public WorldDbContext CreateDbContext() => TestContexts.Create<WorldDbContext>(connection);
    }
}
