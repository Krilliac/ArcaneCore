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

    [Fact]
    public async Task LockedSqliteFile_GuardedCallIsRefusedWithinTheQueryTimeout_AndRecoversAfterTheLockIsReleased()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        try
        {
            await using ServiceProvider provider = Build(
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
            IAccountStore accounts = blocked.ServiceProvider.GetRequiredService<IAccountStore>();
            var watch = Stopwatch.StartNew();
            ResilienceException refused = await Assert.ThrowsAnyAsync<ResilienceException>(async () =>
                await guard.ExecuteAsync(
                    DatabaseComponent.Auth, static (s, ct) => new ValueTask<Account?>(s.FindByUsernameAsync("NOBODY", ct)), accounts));
            watch.Stop();

            Assert.IsType<TimeoutRejectedException>(refused);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(QueryTimeoutMs + 2500), $"guarded call against a locked file took {watch.Elapsed}");
            Assert.Equal(1, auth.ConsecutiveFailures);

            // The lock goes away (exclusive locking mode keeps it until the next access in normal mode): the abandoned
            // call finishes on its own and the next guarded call succeeds.
            await ExecuteAsync(holder, "ROLLBACK");
            await ExecuteAsync(holder, "PRAGMA locking_mode=NORMAL");
            await ExecuteAsync(holder, "SELECT count(*) FROM sqlite_master");
            using (IServiceScope fresh = provider.CreateScope())
            {
                IAccountStore again = fresh.ServiceProvider.GetRequiredService<IAccountStore>();
                Account? none = await guard.ExecuteAsync(
                    DatabaseComponent.Auth, static (s, ct) => new ValueTask<Account?>(s.FindByUsernameAsync("NOBODY", ct)), again);
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(config);
        services.AddDatabaseResilience(config);
        return services.BuildServiceProvider();
    }
}
