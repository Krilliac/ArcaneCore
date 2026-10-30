using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// What the world does when its characters database writer lock is lost: the guard stops claiming it, the loss and a recovery are logged (with no secret in the text), and a lock
/// found taken over by another session ends the world in an orderly stop with a failure exit code. The handlers run everywhere; the end-to-end cases need a PostgreSQL server in
/// <c>ARCANECORE_TEST_POSTGRES</c> (hosted CI) and are reported as Skipped without one.
/// </summary>
public sealed class WorldWriterGuardLossTests
{
    private const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
                Skip = $"Set {PostgresVariable} to a PostgreSQL server connection string (no database) to run this test.";
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    // OnLockConflict records a fault in the process-wide ledger; a test that reaches it forgets that entry again when it ends.
    private sealed class LedgerCleanup : IDisposable
    {
        public void Dispose() => ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Reset();
    }

    private static (WorldWriterGuard Guard, CapturingLoggerProvider Log, FakeLifetime Lifetime, List<int> ExitCodes, ShutdownDrain Drain) Guard(TimeSpan? interval = null, DatabaseOptions? options = null)
    {
        var provider = new CapturingLoggerProvider();
        var lifetime = new FakeLifetime();
        var exitCodes = new List<int>();
        var drain = new ShutdownDrain();
        ILogger<WorldWriterGuard> logger = new LoggerFactory([provider]).CreateLogger<WorldWriterGuard>();
        var guard = new WorldWriterGuard(options is null ? null : Options.Create(options), logger, lifetime: lifetime, lockHealthInterval: interval, setExitCode: code => { lock (exitCodes) exitCodes.Add(code); }, drain: drain);
        return (guard, provider, lifetime, exitCodes, drain);
    }

    [Fact]
    public void ALostLock_IsLoggedCritical_WithoutAnySecret_AndDoesNotStopTheWorld()
    {
        (WorldWriterGuard guard, CapturingLoggerProvider log, FakeLifetime lifetime, List<int> exitCodes, ShutdownDrain drain) = Guard();
        using (guard)
        {
            guard.OnLockLost("PostgreSQL advisory lock 1: the lock connection failed (NpgsqlException: login failed, Password=hunter2secret;\nHost=db)");

            CapturedCategoryLog entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
            Assert.Contains("LOST", entry.Message);
            Assert.DoesNotContain("hunter2secret", entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
            Assert.Equal(0, lifetime.Stops); // a loss alone may heal: the next health ticks try to take the lock back
            Assert.Empty(exitCodes);
        }
    }

    [Fact]
    public void ARecoveredLock_IsLoggedAtWarning_AndAConflictStopsTheWorldWithAFailureExitCode()
    {
        using var ledger = new LedgerCleanup();
        (WorldWriterGuard guard, CapturingLoggerProvider log, FakeLifetime lifetime, List<int> exitCodes, ShutdownDrain drain) = Guard();
        using (guard)
        {
            guard.OnLockRecovered("PostgreSQL advisory lock 1");
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("RE-TAKEN", StringComparison.Ordinal));
            Assert.Equal(0, lifetime.Stops);

            guard.OnLockConflict("PostgreSQL advisory lock 1");
            guard.OnLockConflict("PostgreSQL advisory lock 1"); // reported once by the lock; a repeat must not stop twice

            Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("another session", StringComparison.Ordinal));
            Assert.Equal(1, lifetime.Stops);
            Assert.Equal([ArcaneCore.Kernel.Ops.ExitCodes.Failure], exitCodes);
            // The stop must not write the shutdown saves into the other world's database: both lanes refuse every attempt, and it is said at Critical.
            Assert.NotNull(drain.Saves.SuppressedReason);
            Assert.NotNull(drain.Features.SuppressedReason);
            Assert.True(drain.Saves.Exhausted);
            Assert.NotNull(drain.Saves.Refuse());
            Assert.NotNull(drain.Features.Refuse());
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("SKIPPED", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ABlockedRetake_IsLoggedAsAScrubbedWarning()
    {
        (WorldWriterGuard guard, CapturingLoggerProvider log, _, _, _) = Guard();
        using (guard)
        {
            guard.OnLockRetakeBlocked("lock X: the lock could not be retaken (NpgsqlException: refused, Password=hunter2secret)");

            CapturedCategoryLog entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.DoesNotContain("hunter2secret", entry.Message);
            Assert.Contains("still lost", entry.Message);
        }
    }

    private static async Task<(DatabaseOptions Options, string Scratch)> ScratchAsync()
    {
        string server = Environment.GetEnvironmentVariable(PostgresVariable)!.TrimEnd(';');
        string scratch = "arcane_lockdb_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync($"CREATE DATABASE \"{scratch}\"");
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.PostgreSql,
            ConnectionString = server + $";Database=arcane_t_{Guid.NewGuid():N}"[..40],
            WriterLockDatabase = scratch,
        };
        return (options, scratch);
    }

    private static async Task ExecuteAsync(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresVariable)!.TrimEnd(';')) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Ends every session that holds an advisory lock in the scratch lock database (only this test's locks live there).
    private static Task KillLockSessionsAsync(string scratch)
        => ExecuteAsync($"SELECT pg_terminate_backend(l.pid) FROM pg_locks l WHERE l.locktype = 'advisory' AND l.database = (SELECT oid FROM pg_database WHERE datname = '{scratch}')");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [PostgresFact]
    [Trait("Category", "Timing")]
    public async Task WhileTheLockIsLost_LockPathIsNull_AndOnceRetaken_TheGuardClaimsItAgain()
    {
        (DatabaseOptions options, string scratch) = await ScratchAsync();
        try
        {
            (WorldWriterGuard guard, CapturingLoggerProvider log, FakeLifetime lifetime, List<int> exitCodes, ShutdownDrain drain) = Guard(TimeSpan.FromMilliseconds(300), options);
            using (guard)
            {
                Assert.True(guard.Acquire());
                Assert.NotNull(guard.LockPath);

                await KillLockSessionsAsync(scratch);

                await WaitUntilAsync(() => guard.LockPath is null, TimeSpan.FromSeconds(30), "the guard to stop claiming the lost lock");
                await WaitUntilAsync(() => guard.LockPath is not null, TimeSpan.FromSeconds(30), "the guard to re-take the lock");
                Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("LOST", StringComparison.Ordinal));
                Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("RE-TAKEN", StringComparison.Ordinal));
                Assert.Equal(0, lifetime.Stops);
                Assert.Empty(exitCodes);
                Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(options.Resolve(DatabaseComponent.Characters)));
            }
        }
        finally { await ExecuteAsync($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)"); }
    }

    [PostgresFact]
    [Trait("Category", "Timing")]
    public async Task ALockTakenOverByAnotherSession_StopsTheWorld_AtTheFirstRetake_WithoutTheShutdownSaves()
    {
        using var ledger = new LedgerCleanup();
        (DatabaseOptions options, string scratch) = await ScratchAsync();
        try
        {
            (WorldWriterGuard guard, CapturingLoggerProvider log, FakeLifetime lifetime, List<int> exitCodes, ShutdownDrain drain) = Guard(TimeSpan.FromSeconds(4), options); // the 4 s between the tick that notices the loss and the tick that retakes is the rival's window
            using (guard)
            {
                Assert.True(guard.Acquire());
                await KillLockSessionsAsync(scratch);
                await WaitUntilAsync(() => guard.LockPath is null, TimeSpan.FromSeconds(30), "the loss to be noticed");

                CharacterDatabaseWriterLock? rival = null;
                for (int attempt = 0; attempt < 100 && rival is null; attempt++)
                {
                    try { rival = CharacterDatabaseWriterLock.TryAcquire(options.Resolve(DatabaseComponent.Characters)); }
                    catch (CharacterDatabaseAlreadyOwnedException) { await Task.Delay(10); }
                }

                Assert.NotNull(rival);
                using (rival)
                {
                    await WaitUntilAsync(() => lifetime.Stops > 0, TimeSpan.FromSeconds(30), "the world to be asked to stop");
                    Assert.Null(guard.LockPath);
                    Assert.NotNull(drain.Saves.SuppressedReason);
                    Assert.NotNull(drain.Features.SuppressedReason);
                    Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("another session", StringComparison.Ordinal));
                    Assert.Equal([ArcaneCore.Kernel.Ops.ExitCodes.Failure], exitCodes);
                }
            }
        }
        finally { await ExecuteAsync($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)"); }
    }
}
