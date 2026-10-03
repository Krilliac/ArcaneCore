using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The petition tables reach a fresh database and a database one version behind, and applying the
/// step again over tables that already exist does not fail. That last state is real: MariaDB DDL
/// commits implicitly, so a crash between the CREATE TABLE statements and the version row leaves the
/// tables behind a version that still reads one step earlier (PostgreSQL DDL is transactional, so
/// there the step either happened or did not; SQLite likewise). Only SQLite executes locally.
/// </summary>
public sealed class PetitionSchemaUpgradeTests : IAsyncLifetime
{
    private static readonly int Previous = PetitionDataModule.Version - 1;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void TheModule_TakesItsOwnVersion_AndIsPartOfTheCharactersSchema()
    {
        Assert.Contains(DataModules.For(DatabaseComponent.Characters), m => m is PetitionDataModule { SchemaVersion: PetitionDataModule.Version });
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == PetitionDataModule.Version);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= PetitionDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AFreshDatabase_HasBothTables_AndASecondBootstrapIsIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Empty(await db.Set<PetitionRow>().ToListAsync());
            Assert.Empty(await db.Set<PetitionSignRow>().ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADatabaseOneVersionBehind_GainsTheTables_KeepingItsRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        // A fresh bootstrap creates every table of the current model, so a database one step behind is
        // rebuilt from a complete one: the module's two tables dropped and the version row one step earlier.
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            db.Set<GuildRow>().Add(new GuildRow { Id = 1, Name = "Existing", LeaderId = 5, Motd = "m", Info = "i" });
            await db.SaveChangesAsync();
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            foreach (string table in new[] { "petition_sign", "petition" })
            {
                string statement = $"DROP TABLE {sql.DelimitIdentifier(table)}";
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            SchemaVersionRow row = await db.Set<SchemaVersionRow>().SingleAsync();
            row.Version = Previous;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.False(await TableExistsAsync(db, "petition"));
            Assert.False(await TableExistsAsync(db, "petition_sign"));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.True(await TableExistsAsync(db, "petition"));
            Assert.True(await TableExistsAsync(db, "petition_sign"));
            Assert.Equal("Existing", (await db.Set<GuildRow>().SingleAsync()).Name);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReApplyingTheStep_OverTablesThatAlreadyExist_DoesNotFail_AndKeepsTheirRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await new EfPetitionStore(db).SavePetitionAsync(new PetitionData(1, 10, 700, "Arcane", [new PetitionSignatureData(20, 1020)]));

            // The half-applied upgrade: the tables exist, the version row still reads one step earlier.
            SchemaVersionRow row = await db.Set<SchemaVersionRow>().SingleAsync();
            row.Version = Previous;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            PetitionData kept = Assert.Single(await new EfPetitionStore(db).GetPetitionsAsync());
            Assert.Equal("Arcane", kept.Name);
            Assert.Single(kept.Signatures);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static Task<bool> TableExistsAsync(DbContext db, string table) => SchemaCatalog.TableExistsAsync(db, table, CancellationToken.None);
}
