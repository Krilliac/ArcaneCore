using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The five-second Scourge and War Effort state polls are pure reads. On SQLite they must not take the write lock
/// (Microsoft.Data.Sqlite opens Serializable as BEGIN IMMEDIATE), or each poll queues behind the character save queue's
/// commits: the live 2026-10-11 hangs showed "save queue depth 204, oldest pending 16727 ms" while the poll ran.
/// </summary>
public sealed class WorldStateReadSnapshotTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    [Trait("Category", "Timing")]
    [Fact]
    public async Task ScourgeAndWarEffortLoadsReadWhileAnotherConnectionHoldsTheWriteLock()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.True(await new EfScourgeInvasionStateStore(db).StartAsync());
        }

        // A writer (the save queue's commit, in production) holds RESERVED for the whole read.
        await using var writer = new SqliteConnection(connection.ConnectionString + ";Default Timeout=1");
        await writer.OpenAsync();
        await using (SqliteCommand begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE world_scourge_invasion_state SET battles_won = 7 WHERE id = 1;";
            await begin.ExecuteNonQueryAsync();
        }

        var clock = Stopwatch.StartNew();
        ScourgeInvasionSnapshot scourge;
        WarEffortSnapshot war;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            scourge = await new EfScourgeInvasionStateStore(db).LoadAsync();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            war = await new EfWarEffortStateStore(db).LoadAsync();
        clock.Stop();

        Assert.Equal(ScourgeInvasionState.Enabled, scourge.State);
        Assert.Equal(0, scourge.BattlesWon); // the last committed snapshot, not the writer's uncommitted row
        Assert.Equal(14, scourge.Zones.Sum(z => z.Remaining));
        Assert.NotNull(war);
        // A write-locking read would wait out the busy timeout (or fail with SQLITE_BUSY); a snapshot read does not wait.
        Assert.True(clock.ElapsedMilliseconds < 1_000, $"loads took {clock.ElapsedMilliseconds} ms behind a writer");

        await using (SqliteCommand rollback = writer.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
