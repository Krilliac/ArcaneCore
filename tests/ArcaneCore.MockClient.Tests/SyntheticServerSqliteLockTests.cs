using ArcaneCore.Data.Characters;
using ArcaneCore.MockClient.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The synthetic server's SQLite files wait for another connection's write lock as long as a shipped server does: the characters database's
/// write bound (<c>Database:SqliteWriteTimeoutMs</c>, 5 s), not a one-second Default Timeout of the fixture's own. With one second, a save
/// that met another writer's commit on a loaded CI runner failed as "database is locked" (QuestRewardAsyncRecoveryTests, the quest store's
/// SaveQuestsAsync behind the character save queue), a failure the shipped configuration does not have.
/// </summary>
public sealed class SyntheticServerSqliteLockTests
{
    [Fact]
    public async Task AWriteOfTheServer_WaitsOutAnotherConnectionsWriteLockHeldPastOneSecond()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);

        await using var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = server.CharactersDatabasePath, Pooling = false,
        }.ToString());
        await holder.OpenAsync(token);
        await using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(token);
        }

        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        // Microsoft.Data.Sqlite runs a command synchronously (its busy wait included), so the write gets a thread of its own: the lock can then be released while it waits.
        Task<int> write = Task.Run(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE fixture_lock_probe (id INTEGER)", token), token);

        // Held for 1.5 s: a one-second bound has already failed the write by now; the shipped 5 s bound is still waiting.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
        Assert.False(write.IsCompleted, write.Exception?.GetBaseException().Message ?? $"the write finished while the lock was held ({write.Status})");

        await using (SqliteCommand commit = holder.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync(token);
        }

        await write.WaitAsync(token);
    }
}
