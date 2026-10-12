using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Quest journal and taxi-mask writes must obey the character ownership fence.</summary>
public sealed class QuestOwnershipFencingTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CurrentTokenWritesQuestAndTaxi_StaleAndMissingTokensLeaveBothTablesUnchanged(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int characterId;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            characterId = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
            {
                AccountId = 1, Name = "QuestFence", Race = 1, Class = 1, Level = 10,
            })).Id;
        }

        CharacterOwnershipToken first;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
            first = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim)
                .TryAcquireAsync("world", "first", Guid.NewGuid()));

        CharacterQuestStatus original = Row(characterId, 900001, status: 3);
        uint[] originalTaxi = [1, 2, 4, 8, 0, 0, 0, 0];
        await using (CharacterDbContext current = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterQuestStore(current);
            await store.SaveQuestsAsync(characterId, [original], ownership: first);
            await store.SaveTaxiMaskAsync(characterId, originalTaxi, ownership: first);
        }

        CharacterOwnershipToken second;
        await using (CharacterDbContext takeover = TestContexts.Create<CharacterDbContext>(connection))
            second = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(takeover)
                .TryTakeoverAsync(first, "second", Guid.NewGuid()));

        CharacterQuestStatus changed = original with { Status = 1, Rewarded = true };
        uint[] changedTaxi = [99, 98];
        await using (CharacterDbContext stale = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterQuestStore(stale);
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() =>
                store.SaveQuestsAsync(characterId, [changed], ownership: first));
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() =>
                store.SaveTaxiMaskAsync(characterId, changedTaxi, ownership: first));
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() =>
                store.SaveQuestsAsync(characterId, [changed]));
            await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() =>
                store.SaveTaxiMaskAsync(characterId, changedTaxi));
        }

        await using (CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection))
        {
            CharacterQuestData saved = await new EfCharacterQuestStore(verify).LoadAsync(characterId);
            Assert.Equal([original], saved.Quests);
            Assert.Equal(originalTaxi, saved.TaxiMask);
        }

        // The active token remains usable after rejected attempts.
        await using CharacterDbContext active = TestContexts.Create<CharacterDbContext>(connection);
        await new EfCharacterQuestStore(active).SaveQuestsAsync(characterId, [changed], ownership: second);
        Assert.Equal(changed, Assert.Single((await new EfCharacterQuestStore(active).LoadAsync(characterId)).Quests));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerTransactionRollback_RollsBackQuestAndTaxiWritesTogether(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int characterId;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            characterId = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
            {
                AccountId = 2, Name = "QuestRollback", Race = 1, Class = 1, Level = 10,
            })).Id;
        }

        CharacterOwnershipToken token;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
            token = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim)
                .TryAcquireAsync("world", "rollback", Guid.NewGuid()));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync();
            var store = new EfCharacterQuestStore(db);
            await store.SaveQuestsAsync(characterId, [Row(characterId, 900002, status: 3)], ownership: token);
            await store.SaveTaxiMaskAsync(characterId, [7, 8, 9], ownership: token);
            await transaction.RollbackAsync();
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        CharacterQuestData saved = await new EfCharacterQuestStore(verify).LoadAsync(characterId);
        Assert.Empty(saved.Quests);
        Assert.Empty(saved.TaxiMask);
    }

    private static CharacterQuestStatus Row(int characterId, uint quest, byte status)
        => new(characterId, quest, status, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
