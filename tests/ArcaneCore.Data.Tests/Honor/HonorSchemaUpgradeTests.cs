using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Honor;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Honor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Honor;

/// <summary>
/// The honor tables reach a fresh database and one a version behind, and applying the step again over tables
/// that already exist does not fail. MariaDB DDL commits implicitly, so a crash between the CREATE TABLE
/// statements and the version row leaves some tables behind a version that still reads one step earlier
/// (PostgreSQL and SQLite DDL is transactional). Only SQLite executes locally.
/// </summary>
public sealed class HonorSchemaUpgradeTests : IAsyncLifetime
{
    private static readonly int Previous = CharacterHonorDataModule.Version - 1;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void TheModule_TakesItsOwnVersion_AndIsPartOfTheCharactersSchema()
    {
        Assert.Contains(DataModules.For(DatabaseComponent.Characters), m => m is CharacterHonorDataModule { SchemaVersion: CharacterHonorDataModule.Version });
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == CharacterHonorDataModule.Version);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CharacterHonorDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AFreshDatabase_HasAllTables_AndASecondBootstrapIsIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Empty(await db.Set<CharacterHonorRow>().ToListAsync());
            Assert.Empty(await db.Set<HonorCpRow>().ToListAsync());
            Assert.Empty(await db.Set<HonorMaintenanceRow>().ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADatabaseOneVersionBehind_GainsTheTables_KeepingItsRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            db.Set<GuildRow>().Add(new GuildRow { Id = 1, Name = "Existing", LeaderId = 5, Motd = "m", Info = "i" });
            await db.SaveChangesAsync();
            await DropAsync(db, CharacterHonorDataModule.CpTable, CharacterHonorDataModule.StateTable, CharacterHonorDataModule.MaintenanceTable);
            await SetVersionAsync(db, Previous);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.True(await TableExistsAsync(db, CharacterHonorDataModule.StateTable));
            Assert.True(await TableExistsAsync(db, CharacterHonorDataModule.CpTable));
            Assert.True(await TableExistsAsync(db, CharacterHonorDataModule.MaintenanceTable));
            Assert.Equal("Existing", (await db.Set<GuildRow>().SingleAsync()).Name);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AHalfAppliedStep_WithOneTablePresent_CompletesOnTheNextBootstrap_AndKeepsItsRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            db.Characters.Add(new ArcaneCore.Kernel.Characters.CharacterRecord { AccountId = 1, Name = "Half", Race = 1, Class = 1 });
            await db.SaveChangesAsync();
            int id = db.Characters.Single().Id;
            await new EfHonorStore(db).SaveStateAsync(id, CharacterHonorState.Empty with { RankPoints = 77f });

            // The table created first survives; the later two never made it and the version row is one behind.
            await DropAsync(db, CharacterHonorDataModule.CpTable, CharacterHonorDataModule.MaintenanceTable);
            await SetVersionAsync(db, Previous);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            Assert.Equal(77f, (await db.Set<CharacterHonorRow>().AsNoTracking().SingleAsync()).RankPoints);
            Assert.True(await TableExistsAsync(db, CharacterHonorDataModule.CpTable));
            Assert.True(await TableExistsAsync(db, CharacterHonorDataModule.MaintenanceTable));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheCpTable_HasItsLookupIndexes_OnAFreshAndAnUpgradedDatabase(DatabaseProvider provider)
    {
        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await AssertIndexedAsync(db);
            await DropAsync(db, CharacterHonorDataModule.CpTable);
            await SetVersionAsync(db, Previous);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await AssertIndexedAsync(db);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static async Task AssertIndexedAsync(CharacterDbContext db)
    {
        // Indexed lookups by character and by date are what the weekly queries and login load rely on.
        IReadOnlyList<CatalogIndex> indexes = await SchemaCatalog.ReadIndexesAsync(db, CharacterHonorDataModule.CpTable);
        Assert.Contains(indexes, i => i.Columns.SequenceEqual(["character_id", "date"], StringComparer.OrdinalIgnoreCase));
        Assert.Contains(indexes, i => i.Columns.SequenceEqual(["date"], StringComparer.OrdinalIgnoreCase));
    }

    private static async Task DropAsync(DbContext db, params string[] tables)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        foreach (string table in tables)
        {
            string statement = $"DROP TABLE {sql.DelimitIdentifier(table)}";
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    private static async Task SetVersionAsync(CharacterDbContext db, int version)
    {
        SchemaVersionRow row = await db.Set<SchemaVersionRow>().SingleAsync();
        row.Version = version;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Task<bool> TableExistsAsync(DbContext db, string table) => SchemaCatalog.TableExistsAsync(db, table, CancellationToken.None);
}
