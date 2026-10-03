using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Creatures;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The characters-database <c>creature_respawn</c> table and its store (<see cref="CreatureRespawnDataModule"/>, <see cref="EfCreatureRespawnStore"/>):
/// vmangos keeps <c>guid, respawn_time, instance, map</c> in the characters database and drops expired rows when it loads them
/// (sql/characters.sql:472-480, Maps/MapPersistentStateMgr.cpp:80-101, :1070-1100). The store is written to by one queue at a time; replacing a
/// key is an EF update-or-insert, never <c>REPLACE INTO</c> (PostgreSQL has none). Every theory runs on each provider the machine has: SQLite
/// locally, MariaDB and PostgreSQL on hosted CI.
/// </summary>
public sealed class CreatureRespawnStoreTests : IAsyncLifetime
{
    private const long Now = 1_700_000_000;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<DatabaseConnectionOptions> FreshAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return cs;
    }

    /// <summary>Dungeon instance rows (a respawn time of an instance that has no row is dropped at load).</summary>
    private static async Task AddInstancesAsync(DatabaseConnectionOptions cs, params int[] ids)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        db.Set<InstanceRow>().AddRange(ids.Select(id => new InstanceRow { Id = id, MapId = 409, ResetTime = Now + 100_000 }));
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(DatabaseConnectionOptions cs, long now = Now)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfCreatureRespawnStore(db).LoadAsync(now);
    }

    private static async Task SaveAsync(DatabaseConnectionOptions cs, CreatureRespawnRecord[] upserts, params CreatureRespawnKey[] deletes)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await new EfCreatureRespawnStore(db).SaveAsync(upserts, deletes);
    }

    [Fact]
    public void CharactersStep_IsTheRespawnTable_AndItHasADeletionCleanup()
    {
        SchemaStep step = Assert.Single(CharacterDbContext.Schema.Steps, s => s.Version == CreatureRespawnDataModule.Version);
        Assert.Equal(["creature_respawn"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CreatureRespawnDataModule.Version);
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is CreatureRespawnDataModule);

        // The rows belong to spawns, not characters: nothing to delete with a character, and the guard test must find the cleanup.
        Assert.Contains(CharacterDataCleanups.All, c => ReferenceEquals(c, module));
        Assert.DoesNotContain(module, CharacterDataCleanups.Missing);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARecordRoundTrips_AndTheSameGuidInTwoInstancesKeepsTwoRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await AddInstancesAsync(cs, 7, 8);

        await SaveAsync(cs,
        [
            new CreatureRespawnRecord(0, 0, 4711, Now + 3600),
            new CreatureRespawnRecord(409, 7, 4711, Now + 7200), // the same guid in dungeon instance 7
            new CreatureRespawnRecord(409, 8, 4711, Now + 9000),
        ]);

        IReadOnlyList<CreatureRespawnRecord> rows = await LoadAsync(cs);
        Assert.Equal(
            [(0u, 0u, 4711u, Now + 3600), (409u, 7u, 4711u, Now + 7200), (409u, 8u, 4711u, Now + 9000)],
            rows.OrderBy(r => r.InstanceId).Select(r => (r.MapId, r.InstanceId, r.SpawnGuid, r.RespawnTime)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SavingAKeyAgain_ReplacesItsTime_NeverAddsARow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 5, Now + 100)]);

        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 5, Now + 999)]);
        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 5, Now + 5000), new CreatureRespawnRecord(0, 0, 5, Now + 6000)]); // later in one batch wins

        CreatureRespawnRecord only = Assert.Single(await LoadAsync(cs));
        Assert.Equal(Now + 6000, only.RespawnTime);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADelete_RemovesOnlyItsKey_AndAKeyInBothListsIsDeleted(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await AddInstancesAsync(cs, 3);
        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 1, Now + 100), new CreatureRespawnRecord(0, 0, 2, Now + 100), new CreatureRespawnRecord(409, 3, 1, Now + 100)]);

        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 2, Now + 200)], new CreatureRespawnKey(0, 1), new CreatureRespawnKey(0, 2));

        CreatureRespawnRecord left = Assert.Single(await LoadAsync(cs));
        Assert.Equal((409u, 3u, 1u), (left.MapId, left.InstanceId, left.SpawnGuid)); // (0,1) deleted, (0,2) deleted although also upserted, (3,1) untouched
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_DropsExpiredRows_ForGood(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 1, Now - 1), new CreatureRespawnRecord(0, 0, 2, Now), new CreatureRespawnRecord(0, 0, 3, Now + 1)]);

        IReadOnlyList<CreatureRespawnRecord> loaded = await LoadAsync(cs);

        Assert.Equal([3u], loaded.Select(r => r.SpawnGuid)); // a time equal to now has passed (vmangos keeps only t > GetGameTime)
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await db.Set<CreatureRespawnRow>().CountAsync()); // the expired rows are gone from the table, not just filtered
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_DropsTheRowsOfAnInstanceThatNoLongerExists(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            db.Set<InstanceRow>().Add(new InstanceRow { Id = 7, MapId = 409, ResetTime = Now + 100_000 });
            await db.SaveChangesAsync();
        }

        await SaveAsync(cs,
        [
            new CreatureRespawnRecord(0, 0, 1, Now + 100),     // the shared world needs no instance row
            new CreatureRespawnRecord(409, 7, 2, Now + 100),   // a live instance
            new CreatureRespawnRecord(409, 9, 3, Now + 100),   // instance 9 was deleted: a reused id must not inherit the timer
        ]);

        IReadOnlyList<CreatureRespawnRecord> loaded = await LoadAsync(cs);

        Assert.Equal([1u, 2u], loaded.Select(r => r.SpawnGuid).Order());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingAnInstance_RemovesItsRespawnTimes_AndOnlyThose(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            db.Set<InstanceRow>().AddRange(new InstanceRow { Id = 7, MapId = 409, ResetTime = Now + 100_000 }, new InstanceRow { Id = 8, MapId = 409, ResetTime = Now + 100_000 });
            await db.SaveChangesAsync();
        }

        await SaveAsync(cs, [new CreatureRespawnRecord(0, 0, 1, Now + 100), new CreatureRespawnRecord(409, 7, 2, Now + 100), new CreatureRespawnRecord(409, 8, 3, Now + 100)]);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfInstanceStore(db).DeleteInstanceAsync(7); // the instance store's own delete takes the respawn times with it
        }

        Assert.Equal([1u, 3u], (await LoadAsync(cs)).Select(r => r.SpawnGuid).Order());
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfCreatureRespawnStore(db).DeleteInstanceAsync(8);
        }

        Assert.Equal([1u], (await LoadAsync(cs)).Select(r => r.SpawnGuid));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_AddsTheTable_KeepsTheRows_AndRerunsCleanly(DatabaseProvider provider)
    {
        // MariaDB commits DDL implicitly, PostgreSQL DDL is transactional: either way a second EnsureAsync over the finished schema changes nothing.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int previous = CreatureRespawnDataModule.Version - 1;
        SchemaDefinition prefix = new()
        {
            Component = CharacterDbContext.Schema.Component,
            CurrentVersion = previous,
            Version1Tables = CharacterDbContext.Schema.Version1Tables,
            Steps = [.. CharacterDbContext.Schema.Steps.Where(s => s.Version <= previous)],
        };

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, prefix);
            db.Set<InstanceRow>().Add(new InstanceRow { Id = 3, MapId = 409, ResetTime = Now + 100_000 });
            await db.SaveChangesAsync();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(3, (await db.Set<InstanceRow>().SingleAsync()).Id);
            await new EfCreatureRespawnStore(db).SaveAsync([new CreatureRespawnRecord(409, 3, 10 + (uint)pass, Now + 100)], []);
        }

        Assert.Equal([10u, 11u], (await LoadAsync(cs)).Select(r => r.SpawnGuid).Order());
    }

    [Fact]
    public async Task ABatchWithANullRecord_IsRefusedBeforeAnythingIsWritten()
    {
        DatabaseConnectionOptions cs = await FreshAsync(DatabaseProvider.Sqlite);

        await Assert.ThrowsAnyAsync<Exception>(() => SaveAsync(cs,
        [
            new CreatureRespawnRecord(0, 0, 1, Now + 100),
            null!, // refused up front: the rows before it must not be written
        ]));

        Assert.Empty(await LoadAsync(cs));
    }
}
