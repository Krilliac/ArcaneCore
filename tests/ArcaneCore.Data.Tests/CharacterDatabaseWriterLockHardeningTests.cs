using System.Globalization;
using ArcaneCore.Data.Characters.Ownership;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The hardening of the server-provider writer lock: it is checked cluster-wide on PostgreSQL, a lost lock is re-taken or reported as taken over, stale sessions of a dead
/// host are bounded, and every command is time-limited. PostgreSQL cases run where <c>ARCANECORE_TEST_POSTGRES</c> is set, MariaDB cases where <c>ARCANECORE_TEST_MARIADB</c> is set
/// (hosted CI); without either, every case here reports Skipped with the variable to set.
/// </summary>
public sealed class CharacterDatabaseWriterLockHardeningTests
{
    /// <summary>A fact that is reported as skipped (never silently passed) unless <c>ARCANECORE_TEST_POSTGRES</c> is set.</summary>
    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabases.PostgresVariable)))
                Skip = $"Set {TestDatabases.PostgresVariable} to a PostgreSQL server connection string (no database) to run this test.";
        }
    }

    public static IEnumerable<object[]> ServerProviders() => CharacterDatabaseWriterLockTests.ServerProviders();

    private static NpgsqlConnectionStringBuilder AdminBuilder()
        => new(Environment.GetEnvironmentVariable(TestDatabases.PostgresVariable)!.TrimEnd(';')) { Database = "postgres", Pooling = false };

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminBuilder().ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // ---- 1. PostgreSQL: the lock is cluster-wide, not per lock database

    [PostgresFact]
    public async Task TwoWorldsWithDifferentLockDatabases_AreStillMutuallyExcluded_OnPostgreSql()
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions viaDefault = await databases.CreateAsync(DatabaseProvider.PostgreSql);
        string scratch = "arcane_lockdb_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAdminAsync($"CREATE DATABASE \"{scratch}\"");
        try
        {
            var viaScratch = new DatabaseConnectionOptions { Provider = DatabaseProvider.PostgreSql, ConnectionString = viaDefault.ConnectionString, WriterLockDatabase = scratch };

            CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquire(viaDefault)!;
            try
            {
                Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(viaScratch));
            }
            finally { first.Dispose(); }

            // The refused attempt gave back what it took: the lock is free again, in either lock database.
            CharacterDatabaseWriterLock second = CharacterDatabaseWriterLock.TryAcquire(viaScratch)!;
            try
            {
                Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(viaDefault));

                // A different characters database does not collide, in whatever lock database.
                DatabaseConnectionOptions elsewhere = await databases.CreateAsync(DatabaseProvider.PostgreSql);
                using CharacterDatabaseWriterLock unrelated = CharacterDatabaseWriterLock.TryAcquire(elsewhere)!;
                Assert.NotNull(unrelated);
            }
            finally { second.Dispose(); }
        }
        finally { await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)"); }
    }

    // ---- 2. A lost lock is re-taken, or reported as taken over. The state machine is stepped by hand (CheckHealth with a one-hour interval), so no timer races the test.

    private sealed class Reports
    {
        public int Lost, Recovered, Conflicts, Blocked;
        public string? ConflictText;
    }

    private static ServerAdvisoryWriterLock Acquire(DatabaseConnectionOptions options, Reports reports)
        => ServerAdvisoryWriterLock.TryAcquire(options, ServerAdvisoryWriterLock.DatabaseNameOf(options)!,
            _ => Interlocked.Increment(ref reports.Lost),
            TimeSpan.FromHours(1),
            onRecovered: _ => Interlocked.Increment(ref reports.Recovered),
            onConflict: text => { reports.ConflictText = text; Interlocked.Increment(ref reports.Conflicts); },
            onRetakeBlocked: _ => Interlocked.Increment(ref reports.Blocked));

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLock_IsRetaken_WhenNobodyElseHoldsIt(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        var reports = new Reports();
        using ServerAdvisoryWriterLock held = Acquire(options, reports);
        long firstSession = held.SessionId;

        await CharacterDatabaseWriterLockTests.KillLockSessionAsync(options, ServerAdvisoryWriterLock.DatabaseNameOf(options)!);
        held.CheckHealth(); // notices the loss
        Assert.True(held.IsLost);
        Assert.Equal(1, reports.Lost);

        for (int attempt = 0; attempt < 100 && held.IsLost; attempt++)
        {
            held.CheckHealth(); // retakes (the killed session may need a moment to leave the server)
            if (held.IsLost) await Task.Delay(50);
        }

        Assert.False(held.IsLost);
        Assert.True(held.IsHeld());
        Assert.NotEqual(firstSession, held.SessionId);
        Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => ServerAdvisoryWriterLock.TryAcquire(options, ServerAdvisoryWriterLock.DatabaseNameOf(options)!));
        Assert.Equal((1, 1, 0), (reports.Lost, reports.Recovered, reports.Conflicts));
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLock_TakenByAnotherSession_IsAConflictAtTheFirstRetake_AndNeverRecovered(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        var reports = new Reports();
        using ServerAdvisoryWriterLock held = Acquire(options, reports);
        long firstSession = held.SessionId;

        await CharacterDatabaseWriterLockTests.KillLockSessionAsync(options, name);
        held.CheckHealth(); // notices the loss
        Assert.Equal(1, reports.Lost);

        // Another world takes the freed lock.
        ServerAdvisoryWriterLock? rival = null;
        for (int attempt = 0; attempt < 200 && rival is null; attempt++)
        {
            try { rival = ServerAdvisoryWriterLock.TryAcquire(options, name); }
            catch (CharacterDatabaseAlreadyOwnedException) { await Task.Delay(25); }
        }

        Assert.NotNull(rival);
        using (rival)
        {
            held.CheckHealth(); // the first retake: refused, and the holder is not this process's earlier session
            Assert.Equal(1, reports.Conflicts); // at once, with no stale-session wait
            Assert.Contains(held.Description, reports.ConflictText);
            Assert.Contains($"server session {rival.SessionId}", reports.ConflictText);
            Assert.NotEqual(firstSession, rival.SessionId);
            held.CheckHealth();
            held.CheckHealth();
            Assert.Equal((1, 0, 1, 0), (reports.Lost, reports.Recovered, reports.Conflicts, reports.Blocked)); // reported once, never recovered, not mistaken for a blocked retake
            Assert.True(held.IsLost);
            Assert.False(held.IsHeld());
        }
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLock_StillHeldByThisProcessesOwnDeadSession_WaitsAndThenRetakes_WithoutAConflict(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        var reports = new Reports();
        using ServerAdvisoryWriterLock held = Acquire(options, reports);
        System.Data.Common.DbConnection? zombie = null;
        held.CloseDeadConnection = connection => zombie = connection; // the server has not noticed that this session is dead: it stays open and keeps the lock
        held.MarkLostForTest();

        held.CheckHealth();
        held.CheckHealth();
        held.CheckHealth();

        Assert.NotNull(zombie);
        Assert.True(held.IsLost);
        Assert.Equal((0, 0, 1), (reports.Recovered, reports.Conflicts, reports.Blocked)); // its own session: not another world; reported once (throttled), retried each tick

        await zombie.DisposeAsync(); // the server ends the stale session
        for (int attempt = 0; attempt < 100 && held.IsLost; attempt++)
        {
            held.CheckHealth();
            if (held.IsLost) await Task.Delay(50);
        }

        Assert.False(held.IsLost);
        Assert.True(held.IsHeld());
        Assert.Equal((1, 0), (reports.Recovered, reports.Conflicts));
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _milliseconds = 1000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _milliseconds, (long)by.TotalMilliseconds);
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ARetakeWhoseStepAfterTheTakeFails_LeavesASessionThatCountsAsOwn_NotAConflict(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        var reports = new Reports();
        using ServerAdvisoryWriterLock held = Acquire(options, reports);
        long firstSession = held.SessionId;
        long failedSession = 0;
        int calls = 0;
        held.AfterTakeHook = connection =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            // The server granted the lock to this session; the step after the take then fails, and the connection is disposed with the lock possibly still held server-side.
            using System.Data.Common.DbCommand command = ServerAdvisoryWriterLock.CreateCommand(connection, provider == DatabaseProvider.PostgreSql ? "SELECT pg_backend_pid()" : "SELECT CONNECTION_ID()");
            failedSession = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            throw new InvalidOperationException("the step after the take failed");
        };

        await CharacterDatabaseWriterLockTests.KillLockSessionAsync(options, ServerAdvisoryWriterLock.DatabaseNameOf(options)!);
        held.CheckHealth(); // notices the loss
        Assert.True(held.IsLost);

        held.CheckHealth(); // the retake: takes the lock, then fails
        Assert.True(calls >= 1, "the retake never reached the post-take step");
        Assert.NotEqual(0, failedSession);
        Assert.Contains(failedSession, held.OwnSessionsForTest()); // named and kept as own although the take did not complete
        Assert.Contains(firstSession, held.OwnSessionsForTest());
        Assert.True(held.IsLost);

        for (int attempt = 0; attempt < 100 && held.IsLost; attempt++)
        {
            held.CheckHealth(); // the failed session may still hold the lock for a moment: it is our own, so this waits and retakes
            if (held.IsLost) await Task.Delay(50);
        }

        Assert.False(held.IsLost);
        Assert.True(held.IsHeld());
        Assert.Equal(0, reports.Conflicts);
        Assert.Equal(1, reports.Recovered);
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task AnOwnHolderThatOutlivesTheStaleTimeoutPlusOneInterval_IsDeclaredAConflict(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        var reports = new Reports();
        using ServerAdvisoryWriterLock held = Acquire(options, reports); // interval 1 h: stale timeout 4 h, so the own-session wait is bounded at 5 h
        var time = new ManualTime();
        held.Time = time;
        System.Data.Common.DbConnection? zombie = null;
        held.CloseDeadConnection = connection => zombie = connection; // a session whose id looks like ours and that the server never ends
        held.MarkLostForTest();

        held.CheckHealth();
        held.CheckHealth();
        Assert.NotNull(zombie);
        Assert.Equal((0, 0, 1), (reports.Recovered, reports.Conflicts, reports.Blocked)); // inside the wait: our own session, not a conflict

        time.Advance(TimeSpan.FromHours(4.5));
        held.CheckHealth();
        Assert.Equal(0, reports.Conflicts); // past the stale timeout but within one more interval

        time.Advance(TimeSpan.FromHours(1));
        held.CheckHealth();
        Assert.Equal(1, reports.Conflicts);
        Assert.Contains("treated as another session", reports.ConflictText);
        held.CheckHealth();
        Assert.Equal(1, reports.Conflicts); // once per loss
        Assert.True(held.IsLost);

        await zombie.DisposeAsync();
    }

    // ---- The pg_locks decomposition of a negative key whose halves are both >= 2^31

    private const string NegativeKeyDatabase = "wl_neg_5"; // key -8484032823849904770: classid 2319624472, objid 3618379134

    [Fact]
    public void TheFixedNegativeKeyDatabase_HasBothHalvesAboveTwoToThe31()
    {
        long key = ServerAdvisoryWriterLock.PostgresLockKey(NegativeKeyDatabase);

        Assert.Equal(-8484032823849904770L, key);
        Assert.Equal(2319624472u, (uint)(key >> 32));
        Assert.Equal(3618379134u, (uint)key);
    }

    [PostgresFact]
    public async Task ANegativeKey_IsListedInPgLocksAsItsTwoUnsignedHalves_AndTheHealthCheckAndTheClusterCheckSeeIt()
    {
        var options = new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.PostgreSql,
            ConnectionString = Environment.GetEnvironmentVariable(TestDatabases.PostgresVariable)!.TrimEnd(';') + ";Database=" + NegativeKeyDatabase,
        };
        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, NegativeKeyDatabase);

        await using var connection = new NpgsqlConnection(AdminBuilder().ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid::bigint = 2319624472 AND objid::bigint = 3618379134";
        object? pid = await command.ExecuteScalarAsync();

        Assert.Equal(held.SessionId, Convert.ToInt64(pid, CultureInfo.InvariantCulture));
        Assert.True(held.IsHeld()); // the health check's own decomposition found it
        Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => ServerAdvisoryWriterLock.TryAcquire(options, NegativeKeyDatabase));
    }

    // ---- 3. Stale sessions of a host that died without a TCP reset are bounded

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task TheLockSession_CarriesAnIdleTimeout_OfAboutFourHealthIntervals(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;

        Assert.Equal(TimeSpan.FromSeconds(120), ServerAdvisoryWriterLock.StaleSessionTimeoutFor(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(10), ServerAdvisoryWriterLock.StaleSessionTimeoutFor(TimeSpan.FromMilliseconds(200))); // floor

        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, name, healthInterval: TimeSpan.FromSeconds(30));
        if (provider == DatabaseProvider.PostgreSql)
        {
            long serverVersion = Convert.ToInt64(held.ScalarForTest("SELECT current_setting('server_version_num')::bigint"), CultureInfo.InvariantCulture);
            if (serverVersion >= 140000)
                Assert.Equal(120_000L, Convert.ToInt64(held.ScalarForTest("SELECT setting::bigint FROM pg_settings WHERE name = 'idle_session_timeout'"), CultureInfo.InvariantCulture));
            Assert.True(Convert.ToInt64(held.ScalarForTest("SELECT setting::bigint FROM pg_settings WHERE name = 'tcp_keepalives_idle'"), CultureInfo.InvariantCulture) > 0);
            Assert.True(Convert.ToInt64(held.ScalarForTest("SELECT setting::bigint FROM pg_settings WHERE name = 'tcp_keepalives_interval'"), CultureInfo.InvariantCulture) > 0);
        }
        else
        {
            Assert.Equal(120L, Convert.ToInt64(held.ScalarForTest("SELECT @@session.wait_timeout"), CultureInfo.InvariantCulture));
        }
    }

    [ServerProviderTheory]
    [Trait("Category", "Timing")] // waits 9 s of wall time against the server's idle limit
    [MemberData(nameof(ServerProviders))]
    public async Task TheIdleTimeout_KeepsAHealthyLockSession_AndWouldEndAnUncheckedOne(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        DatabaseConnectionOptions unchecked_ = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        string uncheckedName = ServerAdvisoryWriterLock.DatabaseNameOf(unchecked_)!;
        if (provider == DatabaseProvider.PostgreSql)
        {
            await using var probe = new NpgsqlConnection(AdminBuilder().ConnectionString);
            await probe.OpenAsync();
            await using var version = probe.CreateCommand();
            version.CommandText = "SELECT current_setting('server_version_num')::bigint";
            if (Convert.ToInt64(await version.ExecuteScalarAsync(), CultureInfo.InvariantCulture) < 140000) return; // idle_session_timeout needs PostgreSQL 14
        }

        // A health check every 300 ms against a 3 s idle limit: the session is never idle for long, so it outlives the limit several times over.
        using ServerAdvisoryWriterLock healthy = ServerAdvisoryWriterLock.TryAcquire(options, name, healthInterval: TimeSpan.FromMilliseconds(300), staleSessionTimeout: TimeSpan.FromSeconds(3));
        // The same limit with no health check (interval one hour): the server ends the idle session, which is exactly what the limit is for.
        using ServerAdvisoryWriterLock idle = ServerAdvisoryWriterLock.TryAcquire(unchecked_, uncheckedName, healthInterval: TimeSpan.FromHours(1), staleSessionTimeout: TimeSpan.FromSeconds(3));

        await Task.Delay(TimeSpan.FromSeconds(9));

        Assert.True(healthy.IsHeld());
        Assert.False(healthy.IsLost);
        Assert.False(idle.IsHeld());
    }

    // ---- 4. Every command is time-limited, and failure text carries no secret

    [Fact]
    public void EveryLockCommand_HasAFiveSecondTimeout()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using System.Data.Common.DbCommand command = ServerAdvisoryWriterLock.CreateCommand(connection, "SELECT 1");
        Assert.Equal(5, command.CommandTimeout);
        Assert.Equal("SELECT 1", command.CommandText);
    }

    [Fact]
    public void AFailureDescription_NeverCarriesTheConfiguredPassword()
    {
        var failure = new InvalidOperationException("login failed for 'Host=db;Username=u;Password=hunter2secret' (Password=hunter2secret)");

        string described = ServerAdvisoryWriterLock.DescribeFailure(failure, "Host=db;Username=u;Password=hunter2secret;Database=chars");

        Assert.DoesNotContain("hunter2secret", described);
        Assert.Contains(nameof(InvalidOperationException), described);
        Assert.DoesNotContain('\n', described);
    }
}

/// <summary>A theory over the server providers that is reported as Skipped (never silently empty or passed) unless a server is configured in <c>ARCANECORE_TEST_MARIADB</c> or <c>ARCANECORE_TEST_POSTGRES</c>.</summary>
internal sealed class ServerProviderTheoryAttribute : TheoryAttribute
{
    public ServerProviderTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabases.MariaDbVariable))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabases.PostgresVariable)))
            Skip = $"Set {TestDatabases.PostgresVariable} and/or {TestDatabases.MariaDbVariable} to a server connection string (no database) to run this test.";
    }
}
