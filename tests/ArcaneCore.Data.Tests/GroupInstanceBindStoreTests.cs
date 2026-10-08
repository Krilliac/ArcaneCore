using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The stored group binds (<see cref="GroupInstanceBindDataModule"/>, vmangos <c>group_instance</c>) on every engine: they round trip
/// and update in place, go with their instance (MapPersistentStateMgr.cpp:756) and with their leader (CharacterDatabaseCleaner), and
/// the startup load drops rows of a missing instance or a deleted leader (MapPersistentStateManager::CleanupInstances). A database
/// from before the step gains the table and keeps its rows.
/// </summary>
public sealed class GroupInstanceBindStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GroupBinds_RoundTrip_UpdateInPlace_AndGoWithTheirInstance(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, int bob) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 409, 0));
            await store.SaveInstanceAsync(new InstanceRecord(102, 469, 0));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 101, true));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 102, false));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(bob, 102, true));
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal(
                [new GroupInstanceBindRecord(alice, 101, true), new GroupInstanceBindRecord(alice, 102, false), new GroupInstanceBindRecord(bob, 102, true)],
                snapshot.GroupBinds.OrderBy(b => b.LeaderCharacterId).ThenBy(b => b.InstanceId));

            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 102, true)); // upsert: becomes permanent
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 102, true));
            await store.DeleteGroupBindAsync(bob, 102);
            await store.DeleteGroupBindAsync(bob, 999); // missing: no-op
            await store.DeleteInstanceAsync(101);
        });

        await WithStore(cs, async store =>
        {
            InstanceStoreSnapshot snapshot = await store.LoadAsync();
            Assert.Equal([new GroupInstanceBindRecord(alice, 102, true)], snapshot.GroupBinds);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheLeadersDeletion_RemovesItsRows_AndLoadDropsOrphans(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, int bob) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 409, 0));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 101, true));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(bob, 101, true));
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 555, true)); // instance row missing
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(424242, 101, true)); // leader never existed
        });

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(bob, 77)); // GroupInstanceBindDataModule's cleanup runs inside
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.Equal(0, await db.Set<GroupInstanceRow>().CountAsync(r => r.LeaderCharacterId == bob));
        }

        await WithStore(cs, async store =>
        {
            Assert.Equal([new GroupInstanceBindRecord(alice, 101, true)], (await store.LoadAsync()).GroupBinds);

            const int gone = 434343; // the queued removal after a deletion: no characters row, its rows go
            await store.SaveGroupBindAsync(new GroupInstanceBindRecord(gone, 101, true));
            await store.DeleteCharacterAsync(gone);
            await store.DeleteCharacterAsync(alice); // a live character keeps its rows
        });

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await check.Set<GroupInstanceRow>().CountAsync(r => r.LeaderCharacterId == 434343));
        Assert.Equal(1, await check.Set<GroupInstanceRow>().CountAsync(r => r.LeaderCharacterId == alice));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADatabaseFromBeforeTheGroupBinds_GainsTheTable_KeepingItsInstances(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice, _) = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveInstanceAsync(new InstanceRecord(101, 409, 0));
            await store.SaveBindAsync(new CharacterInstanceBindRecord(alice, 101, true));
        });

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            // A database from before the step: no group_instance table, version below it. Fixed identifiers only.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string drop = $"DROP TABLE {sql.DelimitIdentifier(GroupInstanceBindDataModule.Table)}";
            await db.Database.ExecuteSqlRawAsync(drop);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, GroupInstanceBindDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
            {
                await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
                Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            }

            await WithStore(cs, async store =>
            {
                InstanceStoreSnapshot snapshot = await store.LoadAsync();
                Assert.Equal([new InstanceRecord(101, 409, 0)], snapshot.Instances);
                Assert.Equal([new CharacterInstanceBindRecord(alice, 101, true)], snapshot.Binds);
                Assert.Empty(snapshot.GroupBinds);
            });
        }

        await WithStore(cs, store => store.SaveGroupBindAsync(new GroupInstanceBindRecord(alice, 101, true)));
        await WithStore(cs, async store => Assert.Single((await store.LoadAsync()).GroupBinds));
    }

    [Fact]
    public void TheModule_IsCharactersVersion35_WithOneNewTable_AndItsOwnCleanup()
    {
        var module = new GroupInstanceBindDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(35, module.SchemaVersion);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= GroupInstanceBindDataModule.Version);
        Assert.Equal(new CreateTableChange("group_instance"), Assert.Single(module.SchemaChanges));
        Assert.Contains(CharacterDataCleanups.All, c => c is GroupInstanceBindDataModule);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions Cs, int Alice, int Bob)> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord alice = await characters.CreateAsync(new CharacterRecord { AccountId = 77, Name = "Alice", Race = 1, Class = 1, Level = 60 });
        CharacterRecord bob = await characters.CreateAsync(new CharacterRecord { AccountId = 77, Name = "Bob", Race = 1, Class = 1, Level = 60 });
        return (cs, alice.Id, bob.Id);
    }

    private static async Task WithStore(DatabaseConnectionOptions cs, Func<IInstanceStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfInstanceStore(db));
    }
}
