using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

/// <summary>The hunter pet stable store (vmangos character_pet stable slots and characters.stable_slots).</summary>
public sealed class PetStableStoreTests
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

    private static PersistentPetSnapshot Pet(int owner, uint number, uint entry, string name, uint health = 120) =>
        new(owner, number, entry, 14, 321, health, 40, 500_000, 1, [1u, 2u], [new PersistentPetSpell(17253, true, false)],
            Cooldowns: [new PersistentPetCooldown(0, 17253, 0, 9_999_999_999_999)], Name: name, NameTimestamp: 77, RenameAllowed: false,
            LoyaltyLevel: 3, LoyaltyPoints: 2100, TrainingPoints: 42);

    [Fact]
    public async Task Stable_MovesTheCurrentPetIntoTheSlot_AndUnstableBringsItBackWhole()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var pets = new EfPersistentPetStore(db);
            var stable = new EfPetStableStore(db);
            PersistentPetSnapshot wolf = Pet(7, 501, 3823, "Fang");
            await pets.SaveCurrentAsync(wolf);
            await stable.SetPurchasedSlotsAsync(7, 2);

            await stable.StableAsync(7, 1, wolf);

            Assert.Null(await pets.LoadCallableAsync(7));
            Assert.Empty(await db.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == 7).ToListAsync());
            PetStableState state = await stable.LoadAsync(7);
            Assert.Equal(2, state.PurchasedSlots);
            StabledPet stabled = Assert.Single(state.Pets);
            Assert.Equal((byte)1, stabled.Slot);
            Assert.Equal((501u, 3823u, "Fang", (byte)3, false), (stabled.Pet.PetNumber, stabled.Pet.Entry, stabled.Pet.Name, stabled.Pet.LoyaltyLevel, stabled.Pet.IsCurrent));

            PersistentPetSnapshot? taken = await stable.UnstableAsync(7, 501, null);

            Assert.NotNull(taken);
            Assert.True(taken!.IsCurrent);
            Assert.Empty((await stable.LoadAsync(7)).Pets);
            PersistentPetSnapshot current = (await pets.LoadCurrentAsync(7))!;
            Assert.Equal((501u, 3823u, (byte)14, 321u, 120u, "Fang", 42, 2100), (current.PetNumber, current.Entry, current.Level, current.Experience, current.Health, current.Name, current.TrainingPoints, current.LoyaltyPoints));
            Assert.Equal(17253u, Assert.Single(current.Spells).SpellId);
            Assert.Equal(17253u, Assert.Single(current.Cooldowns!).SpellId);
        }
    }

    [Fact]
    public async Task Unstable_WithAnActivePet_SwapsItIntoTheFreedSlot()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var pets = new EfPersistentPetStore(db);
            var stable = new EfPetStableStore(db);
            PersistentPetSnapshot boar = Pet(9, 600, 1190, "Tusk");
            PersistentPetSnapshot cat = Pet(9, 601, 2042, "Claw");
            await pets.SaveCurrentAsync(boar);
            await stable.StableAsync(9, 2, boar);
            await pets.SaveDetachedAsync(cat);

            PersistentPetSnapshot? taken = await stable.UnstableAsync(9, 600, cat);

            Assert.Equal(600u, taken!.PetNumber);
            Assert.Equal(600u, (await pets.LoadCurrentAsync(9))!.PetNumber);
            StabledPet swapped = Assert.Single((await stable.LoadAsync(9)).Pets);
            Assert.Equal(((byte)2, 601u, "Claw"), (swapped.Slot, swapped.Pet.PetNumber, swapped.Pet.Name));
        }
    }

    [Fact]
    public async Task StableAndUnstable_RefuseWhatTheStableDoesNotAllow()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var pets = new EfPersistentPetStore(db);
            var stable = new EfPetStableStore(db);
            PersistentPetSnapshot first = Pet(3, 700, 1190, "One");
            await stable.StableAsync(3, 1, first);
            PersistentPetSnapshot second = Pet(3, 701, 1190, "Two");
            await pets.SaveCurrentAsync(second);

            await Assert.ThrowsAsync<InvalidOperationException>(() => stable.StableAsync(3, 1, second));
            Assert.Equal(701u, (await pets.LoadCurrentAsync(3))!.PetNumber); // the failed stable changed nothing
            Assert.Null(await stable.UnstableAsync(3, 999, null));
            Assert.Equal(0, (await stable.LoadAsync(3)).PurchasedSlots);
        }
    }

    [Fact]
    public async Task DeletingTheCharacter_RemovesItsStable()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var stable = new EfPetStableStore(db);
            await stable.SetPurchasedSlotsAsync(5, 1);
            await stable.StableAsync(5, 1, Pet(5, 800, 1190, "Gone"));
            await stable.StableAsync(6, 1, Pet(6, 801, 1190, "Kept"));

            await new PetStableDataModule().DeleteCharacterDataAsync(db, 5, CancellationToken.None);

            Assert.Equal(PetStableState.Empty.PurchasedSlots, (await stable.LoadAsync(5)).PurchasedSlots);
            Assert.Empty((await stable.LoadAsync(5)).Pets);
            Assert.Single((await stable.LoadAsync(6)).Pets);
        }
    }
}