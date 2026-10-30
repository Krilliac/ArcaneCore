using ArcaneCore.Data.Characters;
using ArcaneCore.Data.GameObjects;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The characters-database <c>gameobject_respawn</c> table and its store (<see cref="GameObjectRespawnDataModule"/>, <see cref="EfGameObjectRespawnStore"/>):
/// vmangos keeps <c>guid, respawn_time, instance, map</c> in the characters database and drops expired rows when it loads them
/// (sql/characters.sql:472-480, Maps/MapPersistentStateMgr.cpp:103-123, :1149-1156). The store is written to by one queue at a time; replacing a
/// key is an EF update-or-insert, or on SQLite one <c>INSERT ... ON CONFLICT DO UPDATE</c> per row, never <c>REPLACE INTO</c> (PostgreSQL has none). Every theory runs on each provider the machine has: SQLite
/// locally, MariaDB and PostgreSQL on hosted CI.
/// </summary>
public sealed class GameObjectRespawnStoreTests : IAsyncLifetime
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

    private static async Task<IReadOnlyList<GameObjectRespawnRecord>> LoadAsync(DatabaseConnectionOptions cs, long now = Now)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfGameObjectRespawnStore(db).LoadAsync(now);
    }

    private static async Task SaveAsync(DatabaseConnectionOptions cs, GameObjectRespawnRecord[] upserts, params GameObjectRespawnKey[] deletes)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await new EfGameObjectRespawnStore(db).SaveAsync(upserts, deletes);
    }

    [Fact]
    public void CharactersStep_IsTheRespawnTable_AndItHasADeletionCleanup()
    {
        SchemaStep step = Assert.Single(CharacterDbContext.Schema.Steps, s => s.Version == GameObjectRespawnDataModule.Version);
        Assert.Equal(["gameobject_respawn"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= GameObjectRespawnDataModule.Version);
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is GameObjectRespawnDataModule);

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
            new GameObjectRespawnRecord(0, 0, 4711, Now + 3600),
            new GameObjectRespawnRecord(409, 7, 4711, Now + 7200), // the same guid in dungeon instance 7
            new GameObjectRespawnRecord(409, 8, 4711, Now + 9000),
        ]);

        IReadOnlyList<GameObjectRespawnRecord> rows = await LoadAsync(cs);
        Assert.Equal(
            [(0u, 0u, 4711u, Now + 3600), (409u, 7u, 4711u, Now + 7200), (409u, 8u, 4711u, Now + 9000)],
            rows.OrderBy(r => r.InstanceId).Select(r => (r.MapId, r.InstanceId, r.SpawnGuid, r.RespawnTime)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SavingAKeyAgain_ReplacesItsTime_NeverAddsARow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 5, Now + 100)]);

        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 5, Now + 999)]);
        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 5, Now + 5000), new GameObjectRespawnRecord(0, 0, 5, Now + 6000)]); // later in one batch wins

        GameObjectRespawnRecord only = Assert.Single(await LoadAsync(cs));
        Assert.Equal(Now + 6000, only.RespawnTime);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADelete_RemovesOnlyItsKey_AndAKeyInBothListsIsDeleted(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await AddInstancesAsync(cs, 3);
        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 1, Now + 100), new GameObjectRespawnRecord(0, 0, 2, Now + 100), new GameObjectRespawnRecord(409, 3, 1, Now + 100)]);

        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 2, Now + 200)], new GameObjectRespawnKey(0, 1), new GameObjectRespawnKey(0, 2));

        GameObjectRespawnRecord left = Assert.Single(await LoadAsync(cs));
        Assert.Equal((409u, 3u, 1u), (left.MapId, left.InstanceId, left.SpawnGuid)); // (0,1) deleted, (0,2) deleted although also upserted, (3,1) untouched
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_DropsExpiredRows_ForGood(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 1, Now - 1), new GameObjectRespawnRecord(0, 0, 2, Now), new GameObjectRespawnRecord(0, 0, 3, Now + 1)]);

        IReadOnlyList<GameObjectRespawnRecord> loaded = await LoadAsync(cs);

        Assert.Equal([3u], loaded.Select(r => r.SpawnGuid)); // a time equal to now has passed (vmangos keeps only t > GetGameTime)
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await db.Set<GameObjectRespawnRow>().CountAsync()); // the expired rows are gone from the table, not just filtered
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
            new GameObjectRespawnRecord(0, 0, 1, Now + 100),     // the shared world needs no instance row
            new GameObjectRespawnRecord(409, 7, 2, Now + 100),   // a live instance
            new GameObjectRespawnRecord(409, 9, 3, Now + 100),   // instance 9 was deleted: a reused id must not inherit the timer
        ]);

        IReadOnlyList<GameObjectRespawnRecord> loaded = await LoadAsync(cs);

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

        await SaveAsync(cs, [new GameObjectRespawnRecord(0, 0, 1, Now + 100), new GameObjectRespawnRecord(409, 7, 2, Now + 100), new GameObjectRespawnRecord(409, 8, 3, Now + 100)]);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfInstanceStore(db).DeleteInstanceAsync(7); // the instance store's own delete takes the respawn times with it
        }

        Assert.Equal([1u, 3u], (await LoadAsync(cs)).Select(r => r.SpawnGuid).Order());
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await new EfGameObjectRespawnStore(db).DeleteInstanceAsync(8);
        }

        Assert.Equal([1u], (await LoadAsync(cs)).Select(r => r.SpawnGuid));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_AddsTheTable_KeepsTheRows_AndRerunsCleanly(DatabaseProvider provider)
    {
        // MariaDB commits DDL implicitly, PostgreSQL DDL is transactional: either way a second EnsureAsync over the finished schema changes nothing.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int previous = GameObjectRespawnDataModule.Version - 1;
        SchemaDefinition prefix = SchemaProbe.ThroughVersion(CharacterDbContext.Schema, previous);

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
            await new EfGameObjectRespawnStore(db).SaveAsync([new GameObjectRespawnRecord(409, 3, 10 + (uint)pass, Now + 100)], []);
        }

        Assert.Equal([10u, 11u], (await LoadAsync(cs)).Select(r => r.SpawnGuid).Order());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AMixedBatch_MatchesAKeyByKeyModel_AcrossInstancesDuplicatesAndDeletes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await AddInstancesAsync(cs, 1, 2, 3);
        GameObjectRespawnRecord[] seed = [.. Enumerable.Range(1, 1300).SelectMany(g => new[] { 0u, 1u, 2u }.Select(i => new GameObjectRespawnRecord(i == 0 ? 0u : 409u, i, (uint)g, Now + 100 + g)))];
        await SaveAsync(cs, seed);

        var upserts = new List<GameObjectRespawnRecord>();
        for (uint g = 1; g <= 1300; g += 2) // existing rows (updates) in instances 0 and 1, plus a duplicate whose later record must win
        {
            upserts.Add(new GameObjectRespawnRecord(0, 0, g, Now + 5000));
            upserts.Add(new GameObjectRespawnRecord(409, 1, g, Now + 6000));
            upserts.Add(new GameObjectRespawnRecord(409, 1, g, Now + 7000));
        }

        for (uint g = 2000; g < 2700; g++) // new rows, in an instance that had none and in one that had some
        {
            upserts.Add(new GameObjectRespawnRecord(409, 3, g, Now + 8000));
            upserts.Add(new GameObjectRespawnRecord(409, 2, g, Now + 9000));
        }

        GameObjectRespawnKey[] deletes =
        [
            .. Enumerable.Range(1, 1300).Where(g => g % 3 == 0).Select(g => new GameObjectRespawnKey(2, (uint)g)),
            .. Enumerable.Range(1, 600).Where(g => g % 2 == 1).Select(g => new GameObjectRespawnKey(1, (uint)g)), // also upserted: delete wins
            new GameObjectRespawnKey(3, 99_999), // never existed
            new GameObjectRespawnKey(2, 3), // twice
        ];

        var model = seed.ToDictionary(r => (r.InstanceId, r.SpawnGuid));
        foreach (GameObjectRespawnRecord r in upserts)
        {
            model[(r.InstanceId, r.SpawnGuid)] = r;
        }

        foreach (GameObjectRespawnKey k in deletes)
        {
            model.Remove((k.InstanceId, k.SpawnGuid));
        }

        await SaveAsync(cs, [.. upserts], deletes);

        IReadOnlyList<GameObjectRespawnRecord> rows = await LoadAsync(cs);
        Assert.Equal(model.Count, rows.Count);
        Assert.Equal(
            model.Values.OrderBy(r => r.InstanceId).ThenBy(r => r.SpawnGuid),
            rows.OrderBy(r => r.InstanceId).ThenBy(r => r.SpawnGuid));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ALargeBatch_RunsOnAChunkedNumberOfStatements_NotOnePerKey(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await FreshAsync(provider);
        await SaveAsync(cs, [.. Enumerable.Range(1, 2500).Select(g => new GameObjectRespawnRecord(0, 0, (uint)g, Now + 100))]); // half the upserts update, 2,000 deletes hit

        GameObjectRespawnRecord[] upserts = [.. Enumerable.Range(2001, 5000).Select(g => new GameObjectRespawnRecord(0, 0, (uint)g, Now + 500))];
        GameObjectRespawnKey[] deletes = [.. Enumerable.Range(1, 2000).Select(g => new GameObjectRespawnKey(0, (uint)g))];
        var tap = new CommandTap();
        await using (CharacterDbContext db = SchemaProbe.CreateWith<CharacterDbContext>(cs, tap))
        {
            await new EfGameObjectRespawnStore(db).SaveAsync(upserts, deletes);
        }

        // Reads and deletes are set-based: 4 delete chunks + 10 load chunks (one per key before). The row writes themselves stay one per row on
        // SQLite (EF does not batch there; MariaDB and PostgreSQL batch them), so the total is bounded by the 5,000 changed rows plus the chunks.
        IReadOnlyList<string> commands = tap.Commands;
        int setBased = commands.Count(c => c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || c.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
        Assert.True(setBased <= 30, $"{setBased} selects and deletes for 5,000 upserts and 2,000 deletes");
        Assert.True(commands.Count <= 5000 + 50, $"{commands.Count} statements for 5,000 upserts and 2,000 deletes");
        Assert.Equal(5000, (await LoadAsync(cs)).Count);
    }

    [Fact]
    public async Task ABatchWithANullRecord_IsRefusedBeforeAnythingIsWritten()
    {
        DatabaseConnectionOptions cs = await FreshAsync(DatabaseProvider.Sqlite);

        await Assert.ThrowsAnyAsync<Exception>(() => SaveAsync(cs,
        [
            new GameObjectRespawnRecord(0, 0, 1, Now + 100),
            null!, // refused up front: the rows before it must not be written
        ]));

        Assert.Empty(await LoadAsync(cs));
    }
}
