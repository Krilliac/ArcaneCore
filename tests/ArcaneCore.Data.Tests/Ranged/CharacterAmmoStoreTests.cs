using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ranged;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests.Ranged;

/// <summary>
/// <c>character_ammo</c> (<see cref="CharacterAmmoDataModule"/>) on every engine: a save round
/// trips, a second save replaces the first, 0 removes the row, characters are isolated, and the
/// deletion paths remove only the deleted character.
/// </summary>
public sealed class CharacterAmmoStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Module_IsOneCharactersStep_WithTheAmmoTable()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is CharacterAmmoDataModule);
        Assert.Equal(CharacterAmmoDataModule.Version, module.SchemaVersion);
        Assert.Equal([CharacterAmmoDataModule.Table], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == module.SchemaVersion && s.Changes.SequenceEqual(module.SchemaChanges));
        Assert.IsAssignableFrom<ICharacterDataCleanup>(module);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Set_RoundTrips_Replaces_RemovesOnZero_AndIsolatesCharacters(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            Assert.Equal(0u, await store.GetAsync(1));
            await store.SetAsync(1, 2512);
            await store.SetAsync(2, 2516);
            Assert.Equal(2512u, await store.GetAsync(1));
            await store.SetAsync(1, 3030);
            Assert.Equal(3030u, await store.GetAsync(1));
            Assert.Equal(2516u, await store.GetAsync(2));
            await store.SetAsync(1, 0);
            Assert.Equal(0u, await store.GetAsync(1));
            await store.SetAsync(1, 0); // removing nothing is a no-op
            Assert.Equal(2516u, await store.GetAsync(2));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletionCleanup_RemovesOnlyTheDeletedCharactersRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SetAsync(1, 2512);
            await store.SetAsync(2, 2516);
        });

        ICharacterDataCleanup cleanup = Assert.Single(CharacterDataCleanups.All.OfType<CharacterAmmoDataModule>());
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await cleanup.DeleteCharacterDataAsync(db, 1, CancellationToken.None);
            await cleanup.DeleteCharacterDataAsync(db, 42, CancellationToken.None); // nothing to delete
        }

        await WithStore(cs, async store =>
        {
            Assert.Equal(0u, await store.GetAsync(1));
            Assert.Equal(2516u, await store.GetAsync(2));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QueuedRemoval_NeverWipesACharacterThatStillExists(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        int id;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            id = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Hunter" })).Id;
        }

        await WithStore(cs, async store =>
        {
            await store.SetAsync(id, 2512);
            await store.SetAsync(id + 100, 2516); // no character row with this id
            await store.DeleteCharacterAsync(id);
            await store.DeleteCharacterAsync(id + 100);
            Assert.Equal(2512u, await store.GetAsync(id)); // the character exists: the removal is a no-op
            Assert.Equal(0u, await store.GetAsync(id + 100));
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return cs;
    }

    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfCharacterAmmoStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfCharacterAmmoStore(db));
    }
}
