using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="ICharacterReputationStore.DeleteDeletedCharacterAsync"/> (the queued removal after a
/// deletion) removes only rows of an id that has no <c>characters</c> row, while
/// <see cref="ICharacterReputationStore.DeleteCharacterAsync"/> stays unconditional for the stale-row
/// clear at creation (docs/integration/character-delete.md).
/// </summary>
public sealed class ReputationLifetimeFenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LateDeletedCharacterRemoval_KeepsTheRecreatedCharactersReputation(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var store = new EfCharacterReputationStore(db);
            await store.SaveFactionsAsync(id, [new CharacterReputationRow(id, 72, 500, 1)]);
            await store.SaveWatchedFactionAsync(id, 3);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterReputationStore(db).DeleteDeletedCharacterAsync(id);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await read.Set<CharacterReputationEntity>().CountAsync(r => r.CharacterId == id));
        Assert.Equal(1, await read.Set<CharacterReputationWatchEntity>().CountAsync(r => r.CharacterId == id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletedCharacterRemoval_StillRemovesOrphans_AndTheUnconditionalDeleteStillClearsAll(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        }

        // Rows can only be written for an existing character; delete the character afterwards
        // without its module cleanups to leave orphans, as a retained or late write would.
        int orphan;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var characters = new EfCharacterStore(db);
            orphan = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Orphan" })).Id;
            var store = new EfCharacterReputationStore(db);
            await store.SaveFactionsAsync(orphan, [new CharacterReputationRow(orphan, 72, 500, 1)]);
            await store.SaveWatchedFactionAsync(orphan, 3);
            Assert.True(await characters.DeleteAsync(orphan, 1, [], CancellationToken.None));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.Equal(1, await db.Set<CharacterReputationEntity>().CountAsync(r => r.CharacterId == orphan));
            await new EfCharacterReputationStore(db).DeleteDeletedCharacterAsync(orphan);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await read.Set<CharacterReputationEntity>().CountAsync(r => r.CharacterId == orphan));
        Assert.Equal(0, await read.Set<CharacterReputationWatchEntity>().CountAsync(r => r.CharacterId == orphan));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheUnconditionalDelete_StillClearsALiveCharactersRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await RecreateAsync(cs);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterReputationStore(db).SaveFactionsAsync(id, [new CharacterReputationRow(id, 72, 500, 1)]);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCharacterReputationStore(db).DeleteCharacterAsync(id); // creation's stale-row clear
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await read.Set<CharacterReputationEntity>().CountAsync(r => r.CharacterId == id));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static async Task<int> RecreateAsync(DatabaseConnectionOptions cs)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        int id = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "First" })).Id;
        Assert.True(await characters.DeleteAsync(id, 1));
        return (await characters.CreateAsync(new CharacterRecord { Id = id, AccountId = 1, Name = "Reborn" })).Id;
    }
}
