using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Graveyards;

/// <summary>
/// The graveyard world tables on every available provider. SQLite always runs; MariaDB and PostgreSQL run only when the
/// ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES servers exist (hosted CI), where these are written for the real
/// semantics: MariaDB DDL commits implicitly (the schema is created by the bootstrapper before any import transaction
/// starts), PostgreSQL DDL is transactional, names are lower-case snake_case in both so quoting does not differ, a
/// <c>float</c> column round-trips with single precision (asserted with a tolerance: MariaDB returns FLOAT text with 6 significant digits), and ids are unsigned 32-bit values
/// above <c>int.MaxValue</c> in at least one row (PostgreSQL has no unsigned type).
/// </summary>
public sealed class GraveyardStoreTests : IAsyncLifetime
{
    private const string Dump = """
        CREATE TABLE `world_safe_locs` (`id` int unsigned NOT NULL, `map` int unsigned NOT NULL, `x` float NOT NULL, `y` float NOT NULL, `z` float NOT NULL, `o` float NOT NULL, `name` varchar(50) NOT NULL, PRIMARY KEY (`id`));
        INSERT INTO `world_safe_locs` VALUES (1,0,-9100.123,400.456,93.789,3.14159,'Elwynn Forest, Goldshire'),(4000000000,1,10.5,20.5,30.5,0.5,'Zul\'Gurub Entrance, éè'),(7,30,1,2,3,0,'');
        CREATE TABLE `game_graveyard_zone` (`id` int unsigned NOT NULL, `ghost_loc` int unsigned NOT NULL, `link_kind` tinyint unsigned NOT NULL DEFAULT '0', `faction` int unsigned NOT NULL, PRIMARY KEY (`id`,`ghost_loc`,`link_kind`));
        INSERT INTO `game_graveyard_zone` VALUES (1,12,0,469),(1,40,0,0),(4000000000,33,0,67);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_IsDiscovered_AtItsConstantVersion_AndRegistersTheStore()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is GraveyardDataModule);

        Assert.Equal(GraveyardDataModule.Version, module.SchemaVersion);
        Assert.Equal(["world_safe_locs", "game_graveyard_zone"], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= GraveyardDataModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IGraveyardDataStore) && d.ImplementationType == typeof(EfGraveyardDataStore));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_RoundTripsBothTables_IncludingLargeIdsAndFloats(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema); // DDL first, in its own steps (MariaDB commits it implicitly)
            GraveyardImportReport report = await Import(Dump).WriteAsync(db, replace: true);

            Assert.Equal((3, 3, 0), (report.SafeLocs, report.Links, report.SkippedLinks));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        GraveyardContent content = await new EfGraveyardDataStore(verify).LoadAsync();

        Assert.Equal([1u, 7u, 4000000000u], content.SafeLocs.Select(l => l.Id));
        WorldSafeLoc elwynn = content.SafeLocs[0];
        Assert.Equal((0u, "Elwynn Forest, Goldshire"), (elwynn.MapId, elwynn.Name));
        // MariaDB stores the exact single (CAST(x AS DOUBLE) = -9100.123046875) but returns an unconstrained FLOAT as text
        // rounded to 6 significant digits ("-9100.12"), so a value of magnitude 1e3..1e4 reads back within 5e-3.
        Assert.InRange(elwynn.X, -9100.123f - 5e-3f, -9100.123f + 5e-3f);
        Assert.InRange(elwynn.Orientation, 3.14159f - 1e-4f, 3.14159f + 1e-4f);
        Assert.Equal("Zul'Gurub Entrance, éè", content.SafeLocs[2].Name);
        Assert.Equal(
            [(1u, 12u, 469u), (1u, 40u, 0u), (4000000000u, 33u, 67u)],
            content.Links.Select(l => (l.SafeLocId, l.ZoneId, l.Team)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithReplace_EmptiesBothTablesFirst(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new WorldSafeLocRow { Id = 99, MapId = 1, Name = "old" });
            db.Add(new GraveyardZoneRow { Id = 99, GhostZone = 1, Team = 0 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await Import(Dump).WriteAsync(db, replace: true);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.DoesNotContain(await verify.Set<WorldSafeLocRow>().ToListAsync(), r => r.Id == 99);
        Assert.DoesNotContain(await verify.Set<GraveyardZoneRow>().ToListAsync(), r => r.Id == 99);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Import(Dump).WriteAsync(db, replace: false);

            // The exception type differs per provider; the contract is that the write fails and nothing of it stays.
            string second = Dump + "\nINSERT INTO `world_safe_locs` VALUES (500,0,1,1,1,0,'new');";
            await Assert.ThrowsAnyAsync<Exception>(() => Import(second).WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(3, await verify.Set<WorldSafeLocRow>().CountAsync());
        Assert.Equal(3, await verify.Set<GraveyardZoneRow>().CountAsync());
        Assert.DoesNotContain(await verify.Set<WorldSafeLocRow>().ToListAsync(), r => r.Id == 500);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AFreshWorldDatabase_HasEmptyGraveyardTables(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

        GraveyardContent content = await new EfGraveyardDataStore(db).LoadAsync();

        Assert.Empty(content.SafeLocs);
        Assert.Empty(content.Links);
    }

    private static GraveyardDumpImporter Import(string dump)
    {
        var importer = new GraveyardDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}
