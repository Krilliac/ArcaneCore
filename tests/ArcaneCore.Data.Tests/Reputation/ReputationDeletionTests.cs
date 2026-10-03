using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Reputation;

/// <summary>
/// Character deletion removes the character's reputation rows and watched faction in the deletion
/// transaction (<see cref="ICharacterDataCleanup"/>, docs/integration/character-delete.md) and
/// leaves other characters' rows alone, on every engine.
/// </summary>
public sealed class ReputationDeletionTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void ReputationModule_IsACharacterDataCleanup()
        => Assert.Contains(CharacterDataCleanups.All, c => c is CharacterReputationDataModule);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_RemovesFactionsAndWatch_OfThatCharacterOnly(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int gone;
        int kept;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            var characters = new EfCharacterStore(db);
            gone = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Gone" })).Id;
            kept = (await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Kept" })).Id;
            var store = new EfCharacterReputationStore(db);
            foreach (int id in new[] { gone, kept })
            {
                await store.SaveFactionsAsync(id, [new(id, 72, 3500, 0x11), new(id, 21, -100, 0x03)]);
                await store.SaveWatchedFactionAsync(id, 13);
            }
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(gone, accountId: 1));
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await verify.Set<CharacterReputationEntity>().CountAsync(r => r.CharacterId == gone));
        Assert.Equal(0, await verify.Set<CharacterReputationWatchEntity>().CountAsync(r => r.CharacterId == gone));
        CharacterReputationData other = await new EfCharacterReputationStore(verify).LoadAsync(kept);
        Assert.Equal(2, other.Factions.Count);
        Assert.Equal(13, other.WatchedFaction);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
