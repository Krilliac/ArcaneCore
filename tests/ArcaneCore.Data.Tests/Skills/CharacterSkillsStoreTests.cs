using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Skills;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Skills;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Skills;

/// <summary>
/// The skill tables (characters schema, vmangos characters.sql character_skills / character_forgotten_skills)
/// on the provider matrix: round trip, replace-snapshot semantics, per-character isolation, and removal with
/// the character.
/// </summary>
public sealed class CharacterSkillsStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Module_IsACharactersModuleWithItsOwnCleanup_AndItsVersionIsOneConstant()
    {
        var module = DataModules.For(DatabaseComponent.Characters).OfType<CharacterSkillsDataModule>().Single();
        Assert.Equal(CharacterSkillsDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterSkillsDataModule);
        Assert.Equal(
            ["character_skills", "character_forgotten_skills"],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_ThenLoad_RoundTripsSkillsAndForgottenValues(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int id, _) = await CreateAsync(provider);
        var snapshot = new CharacterSkillSnapshot(
            [new((ushort)SkillIds.Swords, 12, 50), new((ushort)SkillIds.Mining, 75, 150)],
            [new((ushort)SkillIds.Axes, 33)]);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.True(await new EfCharacterSkillStore(db).ReplaceSnapshotAsync(id, snapshot));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        CharacterSkillSnapshot loaded = await new EfCharacterSkillStore(verify).LoadAsync(id);
        Assert.Equal(snapshot.Skills, loaded.Skills);
        Assert.Equal(snapshot.Forgotten, loaded.Forgotten);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_SupersedesEverythingEarlier_RemovedSkillsAreDeleted_AndIsIdempotent(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int id, _) = await CreateAsync(provider);
        var first = new CharacterSkillSnapshot(
            [new((ushort)SkillIds.Swords, 12, 50), new((ushort)SkillIds.Mining, 75, 150), new((ushort)SkillIds.Axes, 1, 50)],
            [new((ushort)SkillIds.Axes, 33), new((ushort)SkillIds.Swords, 20)]);
        var second = new CharacterSkillSnapshot(
            [new((ushort)SkillIds.Swords, 13, 50)],
            [new((ushort)SkillIds.Axes, 40)]);

        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
            var store = new EfCharacterSkillStore(db);
            await store.ReplaceSnapshotAsync(id, first);
            await store.ReplaceSnapshotAsync(id, second);
            await store.ReplaceSnapshotAsync(id, second);
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        CharacterSkillSnapshot loaded = await new EfCharacterSkillStore(verify).LoadAsync(id);
        Assert.Equal(second.Skills, loaded.Skills);
        Assert.Equal(second.Forgotten, loaded.Forgotten);

        await new EfCharacterSkillStore(verify).ReplaceSnapshotAsync(id, new CharacterSkillSnapshot([], []));
        CharacterSkillSnapshot empty = await new EfCharacterSkillStore(verify).LoadAsync(id);
        Assert.Empty(empty.Skills);
        Assert.Empty(empty.Forgotten);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_KeepsOtherCharactersAlone_AndIgnoresAMissingCharacter(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int a, int b) = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterSkillStore(db);
            await store.ReplaceSnapshotAsync(a, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 5, 50)], []));
            await store.ReplaceSnapshotAsync(b, new CharacterSkillSnapshot([new((ushort)SkillIds.Axes, 7, 50)], [new((ushort)SkillIds.Swords, 9)]));
            await store.ReplaceSnapshotAsync(a, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 6, 50)], []));
            Assert.False(await store.ReplaceSnapshotAsync(99999, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 1, 5)], [])));
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        var read = new EfCharacterSkillStore(verify);
        Assert.Equal([new CharacterSkillRow((ushort)SkillIds.Swords, 6, 50)], (await read.LoadAsync(a)).Skills);
        CharacterSkillSnapshot other = await read.LoadAsync(b);
        Assert.Equal([new CharacterSkillRow((ushort)SkillIds.Axes, 7, 50)], other.Skills);
        Assert.Equal([new ForgottenSkillRow((ushort)SkillIds.Swords, 9)], other.Forgotten);
        Assert.Empty((await read.LoadAsync(99999)).Skills);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletion_RemovesTheCharactersSkillRows_OnlyThose(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int gone, int kept) = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterSkillStore(db);
            foreach (int id in new[] { gone, kept })
            {
                await store.ReplaceSnapshotAsync(id, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 5, 50)], [new((ushort)SkillIds.Axes, 9)]));
            }
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(gone, accountId: 1));
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(0, await verify.Set<CharacterSkillEntity>().CountAsync(r => r.CharacterId == gone));
        Assert.Equal(0, await verify.Set<CharacterForgottenSkillEntity>().CountAsync(r => r.CharacterId == gone));
        CharacterSkillSnapshot survivor = await new EfCharacterSkillStore(verify).LoadAsync(kept);
        Assert.Single(survivor.Skills);
        Assert.Single(survivor.Forgotten);
    }

    private async Task<(DatabaseConnectionOptions Connection, int First, int Second)> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        int first = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Skilled", Race = 1, Class = 1, Level = 10 })).Id;
        int second = (await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Other", Race = 1, Class = 1, Level = 10 })).Id;
        return (connection, first, second);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();
}
