using System.Diagnostics;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// SQLite connection setup (docs/areas/persistence.md): <c>busy_timeout</c> on every connection EF opens, the journal and synchronous
/// settings the durability story depends on, EF's write transactions taking the write lock at BEGIN, and what the pragma buys two
/// writers on one file. MariaDB, MySQL and PostgreSQL connections are untouched.
/// </summary>
public sealed class SqliteConnectionPragmaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-pragma-" + Guid.NewGuid().ToString("N"));

    public SqliteConnectionPragmaTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public async Task EveryOpenedSqliteConnectionHasTheConfiguredBusyTimeout()
    {
        string auth = Connection("auth.db");
        string characters = Connection("characters.db");
        string world = Connection("world.db");

        // Unset: the shipped 5000 ms on all three contexts.
        await using (ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:Auth:Provider", "Sqlite"), ("Database:Auth:ConnectionString", auth),
            ("Database:Characters:Provider", "Sqlite"), ("Database:Characters:ConnectionString", characters),
            ("Database:World:Provider", "Sqlite"), ("Database:World:ConnectionString", world)))
        {
            Assert.Equal(5000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(5000L, await BusyTimeoutAsync(provider, DatabaseComponent.Characters));
            Assert.Equal(5000L, await BusyTimeoutAsync(provider, DatabaseComponent.World));
        }

        // The top-level key is inherited by every component; a component's own key overrides it.
        await using (ServiceProvider provider = Build(("Database:SqliteBusyTimeoutMs", "1234"),
            ("Database:Auth:Provider", "Sqlite"), ("Database:Auth:ConnectionString", auth),
            ("Database:Characters:Provider", "Sqlite"), ("Database:Characters:ConnectionString", characters), ("Database:Characters:SqliteBusyTimeoutMs", "777"),
            ("Database:World:Provider", "Sqlite"), ("Database:World:ConnectionString", world)))
        {
            Assert.Equal(1234L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(777L, await BusyTimeoutAsync(provider, DatabaseComponent.Characters));
            Assert.Equal(1234L, await BusyTimeoutAsync(provider, DatabaseComponent.World));
        }

        // Never longer than the connection's own Default Timeout: that outer loop is the one DatabaseGuard's deadline relies on.
        await using (ServiceProvider provider = Build(("Database:Provider", "Sqlite"),
            ("Database:ConnectionString", new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "short.db"), Pooling = false, DefaultTimeout = 1 }.ToString())))
        {
            Assert.Equal(1000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
        }

        // 0 disables the pragma: SQLite's own default (no waiting) stays.
        await using (ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", auth), ("Database:SqliteBusyTimeoutMs", "0")))
        {
            Assert.Equal(0L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
        }
    }

    [Fact]
    public void AnOutOfRangeBusyTimeoutIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(-1));
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(60001));
        Assert.Same(SqliteConnectionPragmaInterceptor.For(5000), SqliteConnectionPragmaInterceptor.For(5000));
    }

    [Fact]
    public async Task WalAndSynchronousFullArePinned()
    {
        // Durability note (docs/areas/persistence.md): journal_mode=WAL is applied by the schema bootstrap, and synchronous stays at
        // SQLite's default FULL. NORMAL in WAL mode can lose the most recent commits on power loss, which the save queue's
        // "never lose a save" rule does not allow, and the fsync is not where the time goes.
        string characters = Connection("characters.db");
        await using ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", characters));
        int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(), provider, new StringWriter());
        Assert.Equal(0, exit);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await db.Database.OpenConnectionAsync();
        Assert.Equal("wal", await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA journal_mode"));
        Assert.Equal(2L, await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"));
    }

    [Fact]
    public async Task EfTransactionsOnSqliteBeginImmediate()
    {
        // EF's BeginTransaction on Microsoft.Data.Sqlite takes the write lock at BEGIN, before any statement runs: a writer therefore
        // queues at the lock instead of failing later when a read transaction tries to upgrade. Pinned, not changed.
        string characters = Connection("characters.db", defaultTimeout: 1);
        await using ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", characters), ("Database:SqliteBusyTimeoutMs", "0"));
        await InitializeProbeAsync(provider, characters);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await using (await db.Database.BeginTransactionAsync())
        {
            // No statement has run on the EF connection. Another connection still cannot write.
            await using var other = new SqliteConnection(characters);
            await other.OpenAsync();
            SqliteException busy = await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(other, "INSERT INTO pragma_probe VALUES (1)"));
            Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY
        }

        await using var after = new SqliteConnection(characters);
        await after.OpenAsync();
        await ScalarAsync(after, "INSERT INTO pragma_probe VALUES (1)");
    }

    [Fact]
    public async Task TwoConcurrentWritersBothCommitQuicklyWithoutSqliteBusy()
    {
        // Writer A holds the write lock for 40 ms, writer B (an EF transaction, as the save queue's stores use) arrives while it does.
        // With busy_timeout SQLite's own handler retries within milliseconds of the release. Without it, Microsoft.Data.Sqlite's retry loop
        // sleeps 150 ms between attempts, so B commits up to 150 ms late (the RED half of this test).
        double withPragma = await WriterLatencyAfterReleaseAsync("5000");
        double withoutPragma = await WriterLatencyAfterReleaseAsync("0");
        Assert.True(withPragma < 100, $"with busy_timeout the second writer committed {withPragma:F0} ms after the first released the lock");
        Assert.True(withoutPragma > withPragma, $"without busy_timeout {withoutPragma:F0} ms was not slower than with it ({withPragma:F0} ms)");
        Assert.True(withoutPragma >= 80, $"without busy_timeout the second writer committed {withoutPragma:F0} ms after the release: the 150 ms retry quantisation is not what this test measures");
    }

    [Fact]
    public void MariaDbAndPostgreSqlGetNoInterceptor()
    {
        var postgres = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(postgres, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.PostgreSql, ConnectionString = "Host=127.0.0.1;Port=1;Database=arcane;Username=arcane;Password=arcane;",
        });
        Assert.DoesNotContain(Interceptors(postgres), interceptor => interceptor is SqliteConnectionPragmaInterceptor);

        var sqlite = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(sqlite, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:",
        });
        Assert.Single(Interceptors(sqlite), interceptor => interceptor is SqliteConnectionPragmaInterceptor);

        // MariaDB/MySQL resolve their server version over the network at configuration time, so no unit test can reach past that
        // line; the interceptor is added in the Sqlite switch arm only (DataServiceCollectionExtensions.ConfigureProvider).
        var maria = new DbContextOptionsBuilder<CharacterDbContext>();
        Assert.ThrowsAny<Exception>(() => DataServiceCollectionExtensions.ConfigureProvider(maria, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=127.0.0.1;Port=1;Database=arcane;User=arcane;Password=arcane;Connect Timeout=1;",
        }));
        Assert.DoesNotContain(Interceptors(maria), interceptor => interceptor is SqliteConnectionPragmaInterceptor);
    }

    private async Task<double> WriterLatencyAfterReleaseAsync(string busyTimeoutMs)
    {
        string file = Path.Combine(_directory, "latency-" + busyTimeoutMs + ".db");
        string connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 20 }.ToString();
        await using ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection), ("Database:SqliteBusyTimeoutMs", busyTimeoutMs));
        await InitializeProbeAsync(provider, connection);

        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await ScalarAsync(holder, "PRAGMA busy_timeout = 0");
        await ScalarAsync(holder, "BEGIN IMMEDIATE");
        long released = 0;
        Task<long> writer = Task.Run(async () =>
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("INSERT INTO pragma_probe VALUES (2)");
            await transaction.CommitAsync();
            return Stopwatch.GetTimestamp();
        });

        await Task.Delay(40);
        await ScalarAsync(holder, "INSERT INTO pragma_probe VALUES (1)");
        await ScalarAsync(holder, "COMMIT");
        released = Stopwatch.GetTimestamp();
        long committed = await writer.WaitAsync(TimeSpan.FromSeconds(20));

        await using var check = new SqliteConnection(connection);
        await check.OpenAsync();
        Assert.Equal(2L, await ScalarAsync(check, "SELECT count(*) FROM pragma_probe"));
        return Stopwatch.GetElapsedTime(released, committed).TotalMilliseconds;
    }

    private async Task InitializeProbeAsync(ServiceProvider provider, string connection)
    {
        int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(), provider, new StringWriter());
        Assert.Equal(0, exit);
        await using var setup = new SqliteConnection(connection);
        await setup.OpenAsync();
        await ScalarAsync(setup, "CREATE TABLE IF NOT EXISTS pragma_probe(id INTEGER)");
    }

    private string Connection(string name, int defaultTimeout = 30)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, name), Pooling = false, DefaultTimeout = defaultTimeout }.ToString();

    private static async Task<long> BusyTimeoutAsync(ServiceProvider provider, DatabaseComponent component)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        DbContext db = component switch
        {
            DatabaseComponent.Auth => scope.ServiceProvider.GetRequiredService<AuthDbContext>(),
            DatabaseComponent.Characters => scope.ServiceProvider.GetRequiredService<CharacterDbContext>(),
            _ => await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorldDbContext>>().CreateDbContextAsync(),
        };
        await using (db)
        {
            await db.Database.OpenConnectionAsync();
            return (long)(await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA busy_timeout"))!;
        }
    }

    private static IEnumerable<object> Interceptors(DbContextOptionsBuilder builder)
        => builder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? [];

    private static async Task<object?> ScalarAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(config);
        services.AddCharacterDatabase(config);
        services.AddWorldDatabase(config);
        return services.BuildServiceProvider();
    }
}
