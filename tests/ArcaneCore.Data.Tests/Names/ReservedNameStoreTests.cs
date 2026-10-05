using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Kernel.WorldData.Names;
using Xunit;

namespace ArcaneCore.Data.Tests.Names;

public sealed class ReservedNameStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public async Task ProductionRegistration_ProvidesFactoryAndScopedReservedStore()
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
        IDbContextFactory<WorldDbContext> factory = provider.GetRequiredService<IDbContextFactory<WorldDbContext>>();
        await using (WorldDbContext db = await factory.CreateDbContextAsync())
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<ReservedNameRow>().Add(new ReservedNameRow { Name = "Policy" });
            await db.SaveChangesAsync();
        }
        using IServiceScope scope = provider.CreateScope();
        Assert.Contains("policy", await scope.ServiceProvider.GetRequiredService<IReservedNameStore>().LoadAsync());
    }

    [Fact]
    public async Task SqliteSchema21_RetainsRowsAndNormalizesImmutableCatalog()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, SchemaProbe.ThroughVersion(WorldDbContext.Schema, ReservedNameWorldDataModule.Version - 1));
            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 1, Class = 1, MapId = 0, X = 13 });
            await db.SaveChangesAsync();
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(13f, (await db.PlayerCreateInfo.SingleAsync()).X);
            db.Set<ReservedNameRow>().AddRange(new ReservedNameRow { Name = "Reserved" }, new ReservedNameRow { Name = "RESERVED" },
                new ReservedNameRow { Name = "ÄRTHAS" });
            await db.SaveChangesAsync();
        }

        var store = new EfReservedNameStore(new Factory(cs));
        IReadOnlySet<string> names = await store.LoadAsync();
        Assert.Equal(["reserved", "ärthas"], names.Order(StringComparer.Ordinal));
        Assert.Contains("reserved", names);
        Assert.Contains("ärthas", names);
        var mutable = Assert.IsAssignableFrom<ISet<string>>(names);
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable.Add("changed"));
        var catalog = new NameCatalog([], [], [], names);
        Assert.Equal(NameCatalogResult.Reserved, catalog.Check("RESERVED"));
        Assert.Equal(NameCatalogResult.Reserved, catalog.Check("Ärthas"));
    }

    private sealed class Factory(DatabaseConnectionOptions connection) : IDbContextFactory<WorldDbContext>
    {
        public WorldDbContext CreateDbContext() => TestContexts.Create<WorldDbContext>(connection);
    }
}
