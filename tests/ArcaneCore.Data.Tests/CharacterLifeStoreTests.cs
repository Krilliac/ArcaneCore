using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Vitals, experience, death window, ghost state and corpse (character_vitals, character_corpse)
/// written in the same transaction as the rest of the state snapshot, on every engine.
/// </summary>
public sealed class CharacterLifeStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static CharacterLife Alive() => new(
        Health: 432, Powers: [10, 20, 30, 40, 50], Xp: 1234, DeathExpireUnix: 1_700_000_300, IsGhost: false, Corpse: null);

    private static CharacterLife Ghost() => new(
        Health: 1, Powers: [0, 0, 0, 0, 0], Xp: 99, DeathExpireUnix: 1_700_000_600, IsGhost: true,
        Corpse: new CorpseSnapshot(1, -618.5f, -4251.6f, 38.7f, 3.1f, 1_700_000_100, 2));

    private async Task<(CharacterDbContext Db, int Id)> NewCharacterAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord created = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 3, Name = "Lifer", Race = 1, Class = 1, Level = 1 });
        return (db, created.Id);
    }

    private static CharacterState State(int id, CharacterLife? life, uint played = 10)
        => new(id, 0, 12, 1, 2, 3, 0, 1, played, Life: life);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Life_RoundTripsThroughTheStateSave(DatabaseProvider provider)
    {
        (CharacterDbContext db, int id) = await NewCharacterAsync(provider);
        await using (db)
        {
            var characters = new EfCharacterStore(db);
            var lives = new EfCharacterLifeStore(db);
            Assert.Null(await lives.LoadAsync(id));

            await characters.SaveStateAsync(State(id, Alive()));
            CharacterLife back = Assert.IsType<CharacterLife>(await lives.LoadAsync(id));
            Assert.Equal((432u, 1234u, 1_700_000_300L, false), (back.Health, back.Xp, back.DeathExpireUnix, back.IsGhost));
            Assert.Equal([10u, 20u, 30u, 40u, 50u], back.Powers);
            Assert.Null(back.Corpse);

            await characters.SaveStateAsync(State(id, Ghost()));
            back = Assert.IsType<CharacterLife>(await lives.LoadAsync(id));
            Assert.True(back.IsGhost);
            CorpseSnapshot corpse = Assert.IsType<CorpseSnapshot>(back.Corpse);
            Assert.Equal((1u, -618.5f, -4251.6f, 38.7f, 3.1f, 1_700_000_100L, (byte)2),
                (corpse.MapId, corpse.X, corpse.Y, corpse.Z, corpse.Orientation, corpse.GhostTimeUnix, corpse.Type));

            // Back to alive: the corpse row goes away.
            await characters.SaveStateAsync(State(id, Alive()));
            Assert.Null((await lives.LoadAsync(id))!.Corpse);
            Assert.Equal(0, await db.Set<CharacterCorpseRow>().CountAsync(r => r.CharacterId == id));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ASaveWithoutLife_LeavesTheStoredLifeAlone(DatabaseProvider provider)
    {
        (CharacterDbContext db, int id) = await NewCharacterAsync(provider);
        await using (db)
        {
            var characters = new EfCharacterStore(db);
            await characters.SaveStateAsync(State(id, Ghost()));
            await characters.SaveStateAsync(State(id, null, played: 11));

            CharacterLife back = Assert.IsType<CharacterLife>(await new EfCharacterLifeStore(db).LoadAsync(id));
            Assert.True(back.IsGhost);
            Assert.NotNull(back.Corpse);
            Assert.Equal(11u, (await characters.GetByIdAsync(id))!.PlayedTime);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingTheCharacter_RemovesItsLifeRows(DatabaseProvider provider)
    {
        (CharacterDbContext db, int id) = await NewCharacterAsync(provider);
        await using (db)
        {
            var characters = new EfCharacterStore(db);
            await characters.SaveStateAsync(State(id, Ghost()));
            Assert.Equal((1, 1), (await db.Set<CharacterVitalsRow>().CountAsync(), await db.Set<CharacterCorpseRow>().CountAsync()));

            Assert.True(await characters.DeleteAsync(id, 3));

            Assert.Equal((0, 0), (await db.Set<CharacterVitalsRow>().CountAsync(), await db.Set<CharacterCorpseRow>().CountAsync()));
            Assert.Null(await new EfCharacterLifeStore(db).LoadAsync(id));
        }
    }

    [Fact]
    public void TheModule_IsAnAllocatedCharactersVersion_WithItsOwnCleanup()
    {
        var module = new CharacterLifeDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterLifeDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterLifeDataModule);
        Assert.Equal(
            [CharacterLifeDataModule.VitalsTable, CharacterLifeDataModule.CorpseTable],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
