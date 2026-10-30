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

    // ---- 2. A lost lock is re-taken, or reported as taken over

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLock_IsRetaken_WhenNobodyElseHoldsIt(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int lostReports = 0, recoveredReports = 0, conflicts = 0;
        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, name,
            reason => { Interlocked.Increment(ref lostReports); lost.TrySetResult(reason); },
            TimeSpan.FromMilliseconds(300),
            onRecovered: reason => { Interlocked.Increment(ref recoveredReports); recovered.TrySetResult(reason); },
            onConflict: _ => Interlocked.Increment(ref conflicts));

        await CharacterDatabaseWriterLockTests.KillLockSessionAsync(options, name);

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));
        string recoveredReason = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(held.Description, recoveredReason);
        Assert.True(held.IsHeld());
        Assert.False(held.IsLost);
        // The re-taken lock excludes a second world again, and a later loss is reported afresh.
        Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => ServerAdvisoryWriterLock.TryAcquire(options, name));
        Assert.Equal((1, 1, 0), (Volatile.Read(ref lostReports), Volatile.Read(ref recoveredReports), Volatile.Read(ref conflicts)));
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLock_TakenByAnotherSession_IsReportedAsAConflict_AndNeverRecovered(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conflict = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int recoveredReports = 0, conflicts = 0;
        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, name,
            reason => lost.TrySetResult(reason),
            TimeSpan.FromSeconds(1),
            onRecovered: _ => Interlocked.Increment(ref recoveredReports),
            onConflict: reason => { Interlocked.Increment(ref conflicts); conflict.TrySetResult(reason); });

        await CharacterDatabaseWriterLockTests.KillLockSessionAsync(options, name);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Another world takes the freed lock before this one's next health tick.
        ServerAdvisoryWriterLock? rival = null;
        for (int attempt = 0; attempt < 100 && rival is null; attempt++)
        {
            try { rival = ServerAdvisoryWriterLock.TryAcquire(options, name); }
            catch (CharacterDatabaseAlreadyOwnedException) { await Task.Delay(10); }
        }

        Assert.NotNull(rival);
        using (rival)
        {
            string reason = await conflict.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains(held.Description, reason);
            await Task.Delay(2500); // more ticks: the conflict is reported once, and the lock is not recovered
            Assert.Equal((0, 1), (Volatile.Read(ref recoveredReports), Volatile.Read(ref conflicts)));
            Assert.True(held.IsLost);
            Assert.False(held.IsHeld());
        }
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
