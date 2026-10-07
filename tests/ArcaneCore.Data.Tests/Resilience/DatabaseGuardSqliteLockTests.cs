using System.Diagnostics;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// The query timeout against a provider that ignores its cancellation token. Microsoft.Data.Sqlite's async methods
/// run synchronously and sit in SQLite's busy handler for the connection's <c>Default Timeout</c> (30 s) when another
/// connection holds the file, so the guard must stop waiting on its own (<see cref="TimeoutStrategy.Pessimistic"/>)
/// for the Sqlite provider; the network providers honour the token and keep the cooperative strategy.
/// </summary>
public sealed class DatabaseGuardSqliteLockTests
{
    private const int QueryTimeoutMs = 500;

    [Fact]
    public void TimeoutStrategy_FollowsTheProviderOfEachComponent()
    {
        using ServiceProvider provider = Build(
            ("Database:Provider", "MariaDb"),
            ("Database:ConnectionString", "Server=127.0.0.1;Port=1;Database=arcane;User=arcane;Password=arcane;"),
            ("Database:Characters:Provider", "Sqlite"),
            ("Database:Characters:ConnectionString", "Data Source=:memory:"),
            ("Database:World:Provider", "PostgreSql"),
            ("Database:World:ConnectionString", "Host=127.0.0.1;Port=1;Database=arcane;Username=arcane;Password=arcane;"));
        DatabaseGuard guard = provider.GetRequiredService<DatabaseGuard>();

        Assert.Equal(TimeoutStrategy.Cooperative, guard.PipelineFor(DatabaseComponent.Auth).Timeout!.Strategy);
        Assert.Equal(TimeoutStrategy.Pessimistic, guard.PipelineFor(DatabaseComponent.Characters).Timeout!.Strategy);
        Assert.Equal(TimeoutStrategy.Cooperative, guard.PipelineFor(DatabaseComponent.World).Timeout!.Strategy);
    }

    /// <remarks>
    /// The deadline runs on a hand-driven clock and the abandoned call reports when it really ends. Both were wall-clock
    /// races before (a flake under a loaded test run, 2026-10-07): the recovery call shared the production 500 ms budget
    /// with the still-running abandoned call, which sits in Microsoft.Data.Sqlite's busy retry and contends with the
    /// recovery read for the WAL recovery the exclusive holder leaves behind, and with the scheduler of a saturated machine
    /// (a measured recovery call cut off at 1042 ms against a 500 ms deadline); and the scope disposal and the file
    /// deletion could run before the abandoned call had let go of its connection (an IOException on <c>auth.db</c>).
    /// </remarks>
    [Fact]
    public async Task LockedSqliteFile_GuardedCallIsRefusedWithinTheQueryTimeout_AndRecoversAfterTheLockIsReleased()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        var clock = new HandDrivenClock();
        try
        {
            await using ServiceProvider provider = Build(
                clock,
                ("Database:Provider", "Sqlite"),
                ("Database:ConnectionString", $"Data Source={file}"),
                ("Resilience:Database:QueryTimeoutMs", QueryTimeoutMs.ToString()));
            int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(), provider, new StringWriter());
            Assert.Equal(DbUpgradeExitCodes.Ok, exit);

            DatabaseGuard guard = provider.GetRequiredService<DatabaseGuard>();
            CircuitBreaker auth = guard.Circuits.For(DatabaseComponent.Auth);
            Assert.Equal(TimeSpan.FromMilliseconds(QueryTimeoutMs), guard.QueryTimeout);

            // Another connection holds the file. The bootstrap leaves it in WAL mode (PRAGMA journal_mode answers "wal"),
            // where a plain BEGIN EXCLUSIVE does not stop readers; exclusive locking mode plus one statement takes the file
            // for this connection alone, and every other reader or writer sits in SQLite's busy handler until it lets go.
            await using var holder = new SqliteConnection($"Data Source={file}");
            await holder.OpenAsync();
            await ExecuteAsync(holder, "PRAGMA locking_mode=EXCLUSIVE");
            await ExecuteAsync(holder, "BEGIN EXCLUSIVE");
            await ExecuteAsync(holder, "SELECT count(*) FROM sqlite_master");

            IServiceScope blocked = provider.CreateScope();
            var call = new ObservedCall(blocked.ServiceProvider.GetRequiredService<IAccountStore>());
            Task<Account?> refused = guard.ExecuteAsync(DatabaseComponent.Auth, static (c, ct) => c.FindAsync(ct), call).AsTask();

            // The call is blocked in SQLite (which ignores its token) once the deadline is armed; nothing happens until the
            // deadline passes on the clock.
            await WaitUntilAsync(() => clock.PendingTimers > 0, "the guard to arm the query deadline");
            Assert.False(refused.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(QueryTimeoutMs));

            // Once it has passed, the guard answers at once, in real time, while the store call is still stuck.
            Exception? thrown = await Record.ExceptionAsync(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<TimeoutRejectedException>(thrown);
            Assert.False(call.Ended.IsCompleted, "the store call was expected to still be blocked on the locked file");
            Assert.Equal(1, auth.ConsecutiveFailures);

            // The lock goes away (exclusive locking mode keeps it until the next access in normal mode): the abandoned call
            // finishes on its own, and then the next guarded call succeeds. The clock does not move, so the deadline of the
            // recovery call cannot fire whatever the machine is doing; the real-time bounds only catch a hang.
            await ExecuteAsync(holder, "ROLLBACK");
            await ExecuteAsync(holder, "PRAGMA locking_mode=NORMAL");
            await ExecuteAsync(holder, "SELECT count(*) FROM sqlite_master");
            await call.Ended.WaitAsync(TimeSpan.FromSeconds(30));
            using (IServiceScope fresh = provider.CreateScope())
            {
                IAccountStore again = fresh.ServiceProvider.GetRequiredService<IAccountStore>();
                Account? none = await guard.ExecuteAsync(
                    DatabaseComponent.Auth, static (s, ct) => new ValueTask<Account?>(s.FindByUsernameAsync("NOBODY", ct)), again)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Null(none);
            }

            Assert.Equal(0, auth.ConsecutiveFailures);
            Assert.Equal(CircuitState.Closed, auth.State);

            // Disposing the scope whose context the abandoned call used must not hang once the lock is gone.
            var disposal = Stopwatch.StartNew();
            blocked.Dispose();
            Assert.True(disposal.Elapsed < TimeSpan.FromSeconds(5), $"disposing the abandoned call's scope took {disposal.Elapsed}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 50)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>A store call that reports when it really ends, abandoned or not.</summary>
    private sealed class ObservedCall(IAccountStore store)
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ended => _ended.Task;

        public async ValueTask<Account?> FindAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await store.FindByUsernameAsync("NOBODY", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ended.TrySetResult();
            }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] values) => Build(null, values);

    private static ServiceProvider Build(TimeProvider? clock, params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock); // AddDatabaseResilience adds the system clock only when none is registered
        }

        services.AddAuthDatabase(config);
        services.AddDatabaseResilience(config);
        return services.BuildServiceProvider();
    }
}
