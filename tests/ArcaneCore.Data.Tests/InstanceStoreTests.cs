using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The instance tables (<see cref="InstanceDataModule"/>) on every engine: instances, binds,
/// raid reset times and last instances round trip, update in place, and the startup load drops
/// binds of deleted characters and of missing instances (vmangos CleanupInstances).
/// </summary>
public sealed class InstanceStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InstancesAndBinds_RoundTrip_AndUpdateInPlace(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, int bob) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 36, 1_700_007_200));
            await store.SaveInstanceAsync(new InstanceRecord(102, 409, 1_700_600_000));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 101, false));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 102, false));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(bob, 102, true));
            await store.SaveResetTimeAsync(new InstanceResetRecord(409, 1_700_600_000));
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(alice, 36, 101));
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal([new InstanceRecord(101, 36, 1_700_007_200), new InstanceRecord(102, 409, 1_700_600_000)], snapshot.Instances);
            Assert.Equal(
                [new CharacterInstanceBindRecord(alice, 101, false), new CharacterInstanceBindRecord(alice, 102, false), new CharacterInstanceBindRecord(bob, 102, true)],
                snapshot.Binds.OrderBy(b => b.CharacterId).ThenBy(b => b.InstanceId));
            Assert.Equal([new InstanceResetRecord(409, 1_700_600_000)], snapshot.ResetTimes);
            Assert.Equal([new CharacterLastInstanceRecord(alice, 36, 101)], snapshot.LastInstances);

            // Updates in place (idempotent upserts): the bind becomes permanent, times move.
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 102, true));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 102, true));
            await store.SaveInstanceAsync(new InstanceRecord(101, 36, 1_700_010_000));
            await store.SaveResetTimeAsync(new InstanceResetRecord(409, 1_701_200_000));
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(alice, 409, 102));
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal(1_700_010_000, snapshot.Instances.Single(i => i.Id == 101).ResetTime);
            Assert.True(snapshot.Binds.Single(b => b.CharacterId == alice && b.InstanceId == 102).Permanent);
            Assert.Equal(3, snapshot.Binds.Count);
            Assert.Equal([new InstanceResetRecord(409, 1_701_200_000)], snapshot.ResetTimes);
            Assert.Equal([new CharacterLastInstanceRecord(alice, 409, 102)], snapshot.LastInstances);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Deletes_RemoveBindsAndInstances_AndMissingRowsAreNoOps(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, int bob) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 36, 1));
            await store.SaveInstanceAsync(new InstanceRecord(102, 36, 2));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 101, false));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(bob, 101, false));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(bob, 102, false));
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(alice, 36, 101));
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(bob, 36, 102));
        });

        await WithStore(cs, async store =>
        {
            await store.DeleteInstanceAsync(101); // with its binds and last-instance rows
            await store.DeleteBindAsync(bob, 102);
            await store.DeleteBindAsync(bob, 999); // missing: no-op
            await store.DeleteInstanceAsync(999); // missing: no-op
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal([new InstanceRecord(102, 36, 2)], snapshot.Instances);
            Assert.Empty(snapshot.Binds);
            Assert.Equal([new CharacterLastInstanceRecord(bob, 36, 102)], snapshot.LastInstances);

            await store.SaveBindAsync(new CharacterInstanceBindRecord(bob, 102, true));
            await store.DeleteCharacterAsync(bob);
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Empty(snapshot.Binds);
            Assert.Empty(snapshot.LastInstances);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_DropsRowsOfDeletedCharacters_AndBindsOfMissingInstances(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, int bob) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 36, 1));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 101, false));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(bob, 101, true));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 555, false)); // instance row missing
            await store.SaveBindAsync(new CharacterInstanceBindRecord(424242, 101, false)); // character never existed
            await store.SaveLastInstanceAsync(new CharacterLastInstanceRecord(bob, 36, 101));
        });

        // Bob is deleted by the character flow, which knows nothing about instances.
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(bob, 77));
        }

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal([new CharacterInstanceBindRecord(alice, 101, false)], snapshot.Binds);
            Assert.Empty(snapshot.LastInstances);
            Assert.Equal([new InstanceRecord(101, 36, 1)], snapshot.Instances); // the manager drops unbound instances
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions Cs, int Alice, int Bob)> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord alice = await characters.CreateAsync(new CharacterRecord { AccountId = 77, Name = "Alice", Race = 1, Class = 1, Level = 20 });
        CharacterRecord bob = await characters.CreateAsync(new CharacterRecord { AccountId = 77, Name = "Bob", Race = 1, Class = 1, Level = 20 });
        return (cs, alice.Id, bob.Id);
    }

    /// <summary>Run against a fresh context so every read comes from the database, not the change tracker.</summary>
    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfInstanceStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfInstanceStore(db));
    }
}
