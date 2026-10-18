using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Creatures;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="SqliteRowCommand"/>: the per-row statement the SQLite batch writers (managed-bot UpdateMany, creature respawn upsert) use instead
/// of a LINQ statement or a tracked SaveChanges per row. Names come from the EF model and values bind through EF's type mappings, so a
/// row written this way reads back through EF unchanged.
/// </summary>
public sealed class SqliteRowCommandTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<DatabaseConnectionOptions> FreshAsync()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return cs;
    }

    [Fact]
    public async Task TheStatements_UseTheModelsTableAndColumnNames()
    {
        DatabaseConnectionOptions cs = await FreshAsync();
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using (SqliteRowCommand upsert = SqliteRowCommand.Upsert(db, typeof(CreatureRespawnRow),
            [nameof(CreatureRespawnRow.InstanceId), nameof(CreatureRespawnRow.SpawnGuid)],
            [nameof(CreatureRespawnRow.MapId), nameof(CreatureRespawnRow.RespawnTime)]))
        {
            Assert.Equal(
                "INSERT INTO \"creature_respawn\" (\"instance_id\", \"spawn_guid\", \"map_id\", \"respawn_time\") VALUES (@p0, @p1, @p2, @p3) "
                + "ON CONFLICT (\"instance_id\", \"spawn_guid\") DO UPDATE SET \"map_id\" = excluded.\"map_id\", \"respawn_time\" = excluded.\"respawn_time\"",
                upsert.Sql);
        }

        using SqliteRowCommand update = SqliteRowCommand.Update(db, typeof(ManagedPlayerbotRow),
            [nameof(ManagedPlayerbotRow.Revision)], [nameof(ManagedPlayerbotRow.BotId), nameof(ManagedPlayerbotRow.Revision)]);
        Assert.Equal("UPDATE \"managed_playerbot\" SET \"Revision\" = @p0 WHERE \"BotId\" = @p1 AND \"Revision\" = @p2", update.Sql);
    }

    [Fact]
    public async Task AnUpsert_InsertsThenReplaces_AndEfReadsTheRowBack()
    {
        DatabaseConnectionOptions cs = await FreshAsync();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            using SqliteRowCommand upsert = SqliteRowCommand.Upsert(db, typeof(CreatureRespawnRow),
                [nameof(CreatureRespawnRow.InstanceId), nameof(CreatureRespawnRow.SpawnGuid)],
                [nameof(CreatureRespawnRow.MapId), nameof(CreatureRespawnRow.RespawnTime)]);
            Assert.Equal(1, await upsert.ExecuteAsync([0, 4000000000u, 1u, 100L]));
            Assert.Equal(1, await upsert.ExecuteAsync([0, 4000000000u, 2u, 200L]));
            Assert.Equal(1, await upsert.ExecuteAsync([7, 4000000000u, 3u, 300L]));
            await transaction.CommitAsync();
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(cs);
        CreatureRespawnRow[] rows = await read.Set<CreatureRespawnRow>().AsNoTracking().OrderBy(r => r.InstanceId).ToArrayAsync();
        Assert.Equal([(0, 4000000000u, 2u, 200L), (7, 4000000000u, 3u, 300L)],
            rows.Select(r => (r.InstanceId, r.SpawnGuid, r.MapId, r.RespawnTime)).ToArray());
    }

    [Fact]
    public async Task OutsideATransaction_ItRefuses()
    {
        DatabaseConnectionOptions cs = await FreshAsync();
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Throws<InvalidOperationException>(() => SqliteRowCommand.Upsert(db, typeof(CreatureRespawnRow),
            [nameof(CreatureRespawnRow.InstanceId), nameof(CreatureRespawnRow.SpawnGuid)], [nameof(CreatureRespawnRow.MapId)]));
    }

    [Fact]
    public async Task AWrongValueCount_IsRefused()
    {
        DatabaseConnectionOptions cs = await FreshAsync();
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using SqliteRowCommand upsert = SqliteRowCommand.Upsert(db, typeof(CreatureRespawnRow),
            [nameof(CreatureRespawnRow.InstanceId), nameof(CreatureRespawnRow.SpawnGuid)], [nameof(CreatureRespawnRow.MapId)]);
        await Assert.ThrowsAsync<ArgumentException>(() => upsert.ExecuteAsync([0, 1u]));
    }
}
