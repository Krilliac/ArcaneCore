using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

public sealed class PetPersistenceTests
{
    [Fact]
    public async Task Sqlite_CurrentPet_RoundTripsAndDeletesWithCharacter()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>()
            .UseSqlite(connection).Options;

        await using (CharacterDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(
                17, 9001, 3510, 25, 123, 810, 42, 6000, 1, [7, 8, 9],
                [new PersistentPetSpell(3044, true, false)], Cooldowns: [new PersistentPetCooldown(0, 3044, 12, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)]));
        }

        await using (CharacterDbContext db = new(options))
        {
            PersistentPetSnapshot? actual = await new EfPersistentPetStore(db).LoadCurrentAsync(17);
            Assert.NotNull(actual);
            Assert.Equal((9001u, 3510u, (byte)25, 810u, 6000u),
                (actual!.PetNumber, actual.Entry, actual.Level, actual.Health, actual.Happiness));
            Assert.Equal([7u, 8u, 9u], actual.ActionBar);
            Assert.Equal(new PersistentPetSpell(3044, true, false), Assert.Single(actual.Spells));
            Assert.Equal(3044u, Assert.Single(actual.Cooldowns!).SpellId);
            await new PersistentPetDataModule().DeleteCharacterDataAsync(db, 17, default);
        }

        await using CharacterDbContext verify = new(options);
        Assert.Null(await new EfPersistentPetStore(verify).LoadCurrentAsync(17));
    }

    [Fact]
    public async Task SaveFailure_RollsBackPetAndCooldownReplacement()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using (CharacterDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            var store = new EfPersistentPetStore(db);
            await store.SaveCurrentAsync(new PersistentPetSnapshot(18, 1, 2, 3, 0, 10, 1, 2, 1, [], [],
                Cooldowns: [new PersistentPetCooldown(0, 100, 4, 10_000)]));
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveCurrentAsync(new PersistentPetSnapshot(18, 2, 9, 4, 0, 20, 2, 3, 1, [], [],
                Cooldowns: [new PersistentPetCooldown(0, 200, 5, 20_000), new PersistentPetCooldown(0, 200, 5, 21_000)])));
            PersistentPetSnapshot? afterFailure = await store.LoadCurrentAsync(18);
            Assert.NotNull(afterFailure);
            Assert.Equal(1u, afterFailure!.PetNumber);
            await store.SaveCurrentAsync(new PersistentPetSnapshot(18, 2, 9, 4, 0, 22, 2, 3, 1, [], [],
                Cooldowns: [new PersistentPetCooldown(0, 200, 5, 22_000)]));
        }
        await using CharacterDbContext verify = new(options);
        PersistentPetSnapshot? restored = await new EfPersistentPetStore(verify).LoadCurrentAsync(18);
        Assert.NotNull(restored);
        Assert.Equal((2u, 9u, 22u), (restored!.PetNumber, restored.Entry, restored.Health));
        PersistentPetCooldown cooldown = Assert.Single(restored.Cooldowns!);
        Assert.Equal((200u, 22_000L), (cooldown.SpellId, cooldown.EndsAtUnixMs));
    }
}
