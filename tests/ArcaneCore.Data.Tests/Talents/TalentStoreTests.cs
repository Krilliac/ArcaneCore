using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Data.Schema;
using Xunit;

namespace ArcaneCore.Data.Tests.Talents;

/// <summary>
/// <c>character_talent</c> and <c>character_spell_disabled</c> (<see cref="CharacterTalentDataModule"/>) on every
/// engine the test matrix provides (SQLite always; MariaDB/PostgreSQL when their connection strings are set, i.e. in
/// hosted CI): the respec economy state and the disabled-spell set round-trip, are idempotent, are isolated per
/// character and are removed by character deletion.
/// </summary>
public sealed class TalentStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Module_IsOneCharactersStep_WithBothTables_AndACleanup()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is CharacterTalentDataModule);
        Assert.Equal(CharacterTalentDataModule.Version, module.SchemaVersion);
        Assert.Equal(
            [CharacterTalentDataModule.TalentTable, CharacterTalentDataModule.DisabledSpellTable],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == module.SchemaVersion && s.Changes.SequenceEqual(module.SchemaChanges));
        Assert.DoesNotContain(CharacterDataCleanups.Missing, m => m is CharacterTalentDataModule);
        Assert.Single(CharacterDataCleanups.All.OfType<CharacterTalentDataModule>());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RespecState_RoundTrips_AndUpserts(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            Assert.Null(await store.GetAsync(1));
            await store.SaveAsync(1, new CharacterTalentState(3, 1_800_000_123));
            await store.SaveAsync(2, new CharacterTalentState(10, long.MaxValue));
        });

        await WithStore(cs, async store =>
        {
            Assert.Equal(new CharacterTalentState(3, 1_800_000_123), await store.GetAsync(1));
            Assert.Equal(new CharacterTalentState(10, long.MaxValue), await store.GetAsync(2));
            await store.SaveAsync(1, new CharacterTalentState(4, 1_800_000_999));   // upsert replaces
        });

        await WithStore(cs, async store =>
        {
            Assert.Equal(new CharacterTalentState(4, 1_800_000_999), await store.GetAsync(1));
            Assert.Equal(new CharacterTalentState(10, long.MaxValue), await store.GetAsync(2));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DisabledSpells_AddRemoveAreIdempotent_AndIsolated(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            Assert.Empty(await store.GetDisabledAsync(1));
            await store.AddDisabledAsync(1, [30, 10, 20, 10]);
            await store.AddDisabledAsync(1, [20, 40]);          // re-adding is ignored
            await store.AddDisabledAsync(2, [10]);
            await store.AddDisabledAsync(1, []);
            Assert.Equal<uint>([10, 20, 30, 40], await store.GetDisabledAsync(1));
            Assert.Equal<uint>([10], await store.GetDisabledAsync(2));

            await store.RemoveDisabledAsync(1, [20, 999]);     // unknown spells are ignored
            await store.RemoveDisabledAsync(1, [20]);
            Assert.Equal<uint>([10, 30, 40], await store.GetDisabledAsync(1));
            Assert.Equal<uint>([10], await store.GetDisabledAsync(2));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletionCleanup_RemovesBothTables_ForOnlyTheDeletedCharacter(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveAsync(1, new CharacterTalentState(1, 5));
            await store.SaveAsync(2, new CharacterTalentState(2, 6));
            await store.AddDisabledAsync(1, [7, 8]);
            await store.AddDisabledAsync(2, [9]);
        });

        ICharacterDataCleanup cleanup = Assert.Single(CharacterDataCleanups.All.OfType<CharacterTalentDataModule>());
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await cleanup.DeleteCharacterDataAsync(db, 1, CancellationToken.None);
            await cleanup.DeleteCharacterDataAsync(db, 42, CancellationToken.None);   // nothing to delete
        }

        await WithStore(cs, async store =>
        {
            Assert.Null(await store.GetAsync(1));
            Assert.Empty(await store.GetDisabledAsync(1));
            Assert.Equal(new CharacterTalentState(2, 6), await store.GetAsync(2));
            Assert.Equal<uint>([9], await store.GetDisabledAsync(2));
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QueuedDeleteCharacter_NeverWipesARecreatedCharacter(DatabaseProvider provider)
    {
        // The conditional delete (docs/integration/character-delete.md) only removes rows whose character row is gone.
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveAsync(5, new CharacterTalentState(1, 5));
            await store.AddDisabledAsync(5, [7]);
            await store.DeleteCharacterAsync(5);           // no characters row exists for id 5: its leftovers go
            Assert.Null(await store.GetAsync(5));
            Assert.Empty(await store.GetDisabledAsync(5));
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

    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfCharacterTalentStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfCharacterTalentStore(db));
    }
}
