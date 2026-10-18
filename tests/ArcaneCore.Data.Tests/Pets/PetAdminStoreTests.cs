using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

/// <summary>GM access to stored pets by number (vmangos HandlePetRenameCommand / HandlePetDeleteCommand on character_pet).</summary>
public sealed class PetAdminStoreTests
{
    private static async Task<(SqliteConnection Connection, CharacterDbContext Db)> OpenAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return (connection, db);
    }

    private static PersistentPetSnapshot Pet(int owner, uint number, string name) =>
        new(owner, number, 3823, 14, 321, 120, 40, 500_000, 1, [1u, 2u], [new PersistentPetSpell(17253, true, false)],
            Cooldowns: [new PersistentPetCooldown(0, 17253, 0, 9_999_999_999_999)], Name: name, LoyaltyLevel: 3);

    [Fact]
    public async Task CurrentAndStabledPets_AreFoundRenamedAndDeleted_ByNumber()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var pets = new EfPersistentPetStore(db);
            var stable = new EfPetStableStore(db);
            var admin = new EfPetAdminStore(db);
            await pets.SaveCurrentAsync(Pet(7, 501, "Fang"));
            await stable.SetPurchasedSlotsAsync(7, 2);
            await stable.StableAsync(7, 1, Pet(7, 501, "Fang"));
            await pets.SaveCurrentAsync(Pet(7, 502, "Claw"));

            Assert.Equal(new StoredPetLocation(502, 7, "Claw", false), await admin.FindAsync(502));
            Assert.Equal(new StoredPetLocation(501, 7, "Fang", true), await admin.FindAsync(501));
            Assert.Null(await admin.FindAsync(999));

            Assert.True(await admin.RenameAsync(502, "Bitey"));
            Assert.True(await admin.RenameAsync(501, "Stripes"));
            Assert.False(await admin.RenameAsync(999, "Nobody"));
            Assert.Equal("Bitey", (await pets.LoadCurrentAsync(7))!.Name);
            StabledPet stabled = Assert.Single((await stable.LoadAsync(7)).Pets);
            Assert.Equal("Stripes", stabled.Pet.Name);
            Assert.Equal((byte)3, stabled.Pet.LoyaltyLevel);

            // The stabled pet keeps its new name when it comes back out.
            Assert.True(await admin.DeleteAsync(502));
            Assert.Null(await pets.LoadCallableAsync(7));
            Assert.Empty(await db.Set<PersistentPetCooldownRow>().Where(r => r.PetNumber == 502).ToListAsync());
            Assert.Equal("Stripes", (await stable.UnstableAsync(7, 501, null))!.Name);

            Assert.True(await admin.DeleteAsync(501));
            Assert.Null(await admin.FindAsync(501));
            Assert.False(await admin.DeleteAsync(501));
        }
    }
}