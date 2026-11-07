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
    [Trait("Category", "Timing")] // wall-bound: the point is surviving a real SQLite lock held past 1 s against the shipped 5 s bound
    public async Task AWriteOfTheServer_WaitsOutAnotherConnectionsWriteLockHeldPastOneSecond()
    {
        using var deadline = new CancellationTokenSource();
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        // The fixture starts under its own hang bound; the scenario deadline is armed after it returns (TestDeadlines).
        deadline.CancelAfter(TimeSpan.FromSeconds(45));

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
        long writeStarted = 0;
        Task<int> write = Task.Run(() =>
        {
            Volatile.Write(ref writeStarted, System.Diagnostics.Stopwatch.GetTimestamp());
            return db.Database.ExecuteSqlRawAsync("CREATE TABLE fixture_lock_probe (id INTEGER)", token);
        }, token);

        // The lock is held until the write itself has been running for 1.5 s, measured from the write's own start so a slow pool start does not shorten the
        // proof. A one-second bound has failed the write long before that (the margin over it is 500 ms, enough for the EF pipeline and the busy-wait overshoot);
        // the shipped 5 s bound is still waiting. The release runs on a dedicated thread (LongRunning), so a starved thread pool cannot delay the COMMIT; the
        // remaining way to fail is the host stalling that thread for about 3.5 s, since the write hits its 5 s bound before the COMMIT then.
        Task<bool> release = Task.Factory.StartNew(() =>
        {
            while (Volatile.Read(ref writeStarted) == 0
                || System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref writeStarted)).TotalMilliseconds < 1500)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(10);
            }

            bool stillWaiting = !write.IsCompleted;
            using SqliteCommand commit = holder.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            return stillWaiting;
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        bool waitedOutTheHold = await release.WaitAsync(token);
        Assert.True(waitedOutTheHold, "the write finished or failed while the lock was still held");

        await write.WaitAsync(token);
    }
}
