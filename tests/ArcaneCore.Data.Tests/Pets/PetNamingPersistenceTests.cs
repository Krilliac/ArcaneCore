using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

public sealed class PetNamingPersistenceTests
{
    [Fact]
    public async Task Sqlite_CurrentPet_RoundTripsRenamedNameTimestampAndPermission()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using (CharacterDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(
                17, 9001, 3510, 25, 123, 810, 42, 6000, 1, [], [],
                Name: "Rex", NameTimestamp: 1_700_000_000, RenameAllowed: false));
        }

        await using CharacterDbContext verify = new(options);
        PersistentPetSnapshot? actual = await new EfPersistentPetStore(verify).LoadCurrentAsync(17);
        Assert.NotNull(actual);
        Assert.Equal(("Rex", 1_700_000_000u, false), (actual!.Name, actual.NameTimestamp, actual.RenameAllowed));
    }

    [Fact]
    public async Task Sqlite_V23PetRow_UpgradesToNamingColumnsWithoutLosingTheRow()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using (CharacterDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(18, 2, 9, 4, 0, 20, 2, 3, 1, [], []));
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, PetNamingDataModule.Version - 1));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN Name");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN NameTimestamp");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN RenameAllowed");
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            PersistentPetSnapshot? upgraded = await new EfPersistentPetStore(db).LoadCurrentAsync(18);
            Assert.NotNull(upgraded);
            Assert.Equal(("", 0u, true), (upgraded!.Name, upgraded.NameTimestamp, upgraded.RenameAllowed));
        }
    }
}
