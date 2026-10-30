using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// SQLite connection setup (docs/areas/persistence.md): <c>busy_timeout</c> on every connection EF opens, the journal and synchronous
/// settings the durability story depends on, EF's write transactions taking the write lock at BEGIN, and what the pragma buys a
/// second writer: with a held write lock it waits at least the configured timeout inside SQLite before SQLITE_BUSY, and with the
/// pragma at 0 it is refused at once (measured on the raw handle, so Microsoft.Data.Sqlite's retry loop cannot mask it). MariaDB, MySQL and PostgreSQL connections are untouched.
/// </summary>
public sealed class SqliteConnectionPragmaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-pragma-" + Guid.NewGuid().ToString("N"));

    public SqliteConnectionPragmaTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // Every connection string here is Pooling=False, so no pooled handle outlives its connection; never clear the process-wide pools (tests run in parallel).
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
    public async Task BusyTimeoutIsClampedToTheGuardsQueryTimeout_OnlyWhenTheGuardIsOn()
    {
        string auth = Connection("auth.db");
        (string, string?)[] sqlite = [("Database:Provider", "Sqlite"), ("Database:ConnectionString", auth)];

        // Guard on with a 2 s deadline: a refused call must not outlive it inside SQLite's busy handler.
        await using (ServiceProvider provider = Build(true, [.. sqlite, ("Database:SqliteBusyTimeoutMs", "5000"), ("Resilience:Database:QueryTimeoutMs", "2000")]))
        {
            Assert.Equal(2000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
        }

        // A configured value below the deadline is kept.
        await using (ServiceProvider provider = Build(true, [.. sqlite, ("Database:SqliteBusyTimeoutMs", "300"), ("Resilience:Database:QueryTimeoutMs", "2000")]))
        {
            Assert.Equal(300L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
        }

        // No deadline (0) or the guard switched off: the configured value stands.
        await using (ServiceProvider provider = Build(true, [.. sqlite, ("Database:SqliteBusyTimeoutMs", "5000"), ("Resilience:Database:QueryTimeoutMs", "0")]))
        {
            Assert.Equal(5000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
        }

        await using (ServiceProvider provider = Build(true, [.. sqlite, ("Database:SqliteBusyTimeoutMs", "5000"), ("Resilience:Database:Enabled", "false"), ("Resilience:Database:QueryTimeoutMs", "2000")]))
        {
            Assert.Equal(5000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
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
        // SQLite's default FULL unless Database:SqliteSynchronous opts a database into NORMAL (which can lose the most recent commits on
        // power loss or an OS crash, and is applied only in WAL mode). FULL is the shipped default.
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
    public async Task SynchronousIsNormalOnlyWhereConfigured_AndFullByDefault()
    {
        // NORMAL is only applied to a database already in WAL mode (the schema bootstrap normally puts it there), so put the files there first.
        string auth = WalConnection("auth.db");
        string characters = WalConnection("characters.db");
        string world = WalConnection("world.db");

        // Unset: FULL (2) everywhere, today's behaviour.
        await using (ServiceProvider provider = Build(("Database:Auth:Provider", "Sqlite"), ("Database:Auth:ConnectionString", auth),
            ("Database:Characters:Provider", "Sqlite"), ("Database:Characters:ConnectionString", characters),
            ("Database:World:Provider", "Sqlite"), ("Database:World:ConnectionString", world)))
        {
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.Characters));
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.World));
        }

        // Only the characters database is NORMAL (1).
        await using (ServiceProvider provider = Build(("Database:Auth:Provider", "Sqlite"), ("Database:Auth:ConnectionString", auth),
            ("Database:Characters:Provider", "Sqlite"), ("Database:Characters:ConnectionString", characters), ("Database:Characters:SqliteSynchronous", "NORMAL"),
            ("Database:World:Provider", "Sqlite"), ("Database:World:ConnectionString", world)))
        {
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(1L, await SynchronousAsync(provider, DatabaseComponent.Characters));
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.World));
        }

        // The shared key is inherited by every component without its own; a component's own key overrides it.
        await using (ServiceProvider provider = Build(("Database:SqliteSynchronous", "Normal"),
            ("Database:Auth:Provider", "Sqlite"), ("Database:Auth:ConnectionString", auth),
            ("Database:Characters:Provider", "Sqlite"), ("Database:Characters:ConnectionString", characters), ("Database:Characters:SqliteSynchronous", "Full"),
            ("Database:World:Provider", "Sqlite"), ("Database:World:ConnectionString", world)))
        {
            Assert.Equal(1L, await SynchronousAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(2L, await SynchronousAsync(provider, DatabaseComponent.Characters));
            Assert.Equal(1L, await SynchronousAsync(provider, DatabaseComponent.World));
        }

        // Single-database layout, with the busy-timeout clamp object rebuilt by the guard: the setting survives it.
        await using (ServiceProvider provider = Build(true, [("Database:Provider", "Sqlite"), ("Database:ConnectionString", auth), ("Database:SqliteSynchronous", "NORMAL"),
            ("Database:SqliteBusyTimeoutMs", "5000"), ("Resilience:Database:QueryTimeoutMs", "2000")]))
        {
            Assert.Equal(2000L, await BusyTimeoutAsync(provider, DatabaseComponent.Auth));
            Assert.Equal(1L, await SynchronousAsync(provider, DatabaseComponent.Auth));
        }
    }

    [Fact]
    public async Task AFreshDatabaseFile_ConfiguredNormal_StartsInWalWithNormalAndNoRefusal()
    {
        // A brand-new install: characters.db does not exist yet and Database:SqliteSynchronous=NORMAL. The very first connection
        // (EF's create) used to find the new file in journal mode 'delete' and log NORMAL as refused at ERROR ("FAIL" on the console).
        // RED comes from the final log assertion ONLY: without the fix the WAL, synchronous and EffectiveFor checks pass too, because EF's create and the
        // schema bootstrap switch the file to WAL right after that first open and every later open applies NORMAL. Startup never failed; the ERROR was false.
        string path = Path.Combine(_directory, "fresh-characters.db");
        Assert.False(File.Exists(path));
        string characters = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        var errors = new CapturingLoggerProvider();
        await using ServiceProvider provider = Build(false, [("Database:Provider", "Sqlite"), ("Database:ConnectionString", characters), ("Database:SqliteSynchronous", "Normal")], errors);
        int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(), provider, new StringWriter());
        Assert.Equal(0, exit);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await db.Database.OpenConnectionAsync();
        Assert.Equal("wal", await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA journal_mode"));
        Assert.Equal(1L, await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"));
        SqliteConnectionPragmaInterceptor pragma = Assert.Single(
            db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors!.OfType<SqliteConnectionPragmaInterceptor>());
        Assert.Equal(SqliteSynchronousMode.Normal, pragma.EffectiveFor(db.Database.GetDbConnection().DataSource));
        Assert.DoesNotContain(errors.Errors, line => line.Contains("SqliteSynchronous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreshDatabaseFile_ConfiguredNormal_WithPooling_ReusedConnectionIsNormalAndNoRefusal()
    {
        // The production default: Pooling=true, so the handle EF's create opened goes back to the pool and is handed out again. As above, RED comes from the log
        // assertion only.
        string path = Path.Combine(_directory, "fresh-pooled-characters.db");
        Assert.False(File.Exists(path));
        string characters = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        var errors = new CapturingLoggerProvider();
        string? efConnectionString = null;
        try
        {
            await using ServiceProvider provider = Build(false, [("Database:Provider", "Sqlite"), ("Database:ConnectionString", characters), ("Database:SqliteSynchronous", "Normal")], errors);
            int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(), provider, new StringWriter());
            Assert.Equal(0, exit);

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            efConnectionString = db.Database.GetConnectionString();
            await db.Database.OpenConnectionAsync();
            Assert.Equal("wal", await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA journal_mode"));
            Assert.Equal(1L, await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"));
            await db.Database.CloseConnectionAsync();

            // A plain open with EF's exact connection string takes the pooled handle back WITHOUT the interceptor running: it still carries the NORMAL that was
            // applied on it (a fresh, unpooled handle would report SQLite's default FULL, 2).
            await using (var reused = new SqliteConnection(efConnectionString))
            {
                await reused.OpenAsync();
                Assert.Equal(1L, await ScalarAsync(reused, "PRAGMA synchronous"));
            }

            Assert.DoesNotContain(errors.Errors, line => line.Contains("SqliteSynchronous", StringComparison.Ordinal));
        }
        finally
        {
            // Release only this test's pool so the temp directory can be deleted; other tests' pools are untouched.
            if (efConnectionString is not null)
            {
                using var owner = new SqliteConnection(efConnectionString);
                SqliteConnection.ClearPool(owner);
            }

            using var raw = new SqliteConnection(characters);
            SqliteConnection.ClearPool(raw);
        }
    }

    [Fact]
    public async Task AFreshDatabaseFile_ConfiguredNormal_ConcurrentOpensAllGetWalAndNormal()
    {
        await ConcurrentFreshOpens("fresh-concurrent.db");
    }

    [Fact]
    public async Task AFreshDatabaseFile_ConfiguredNormal_ConcurrentOpensRepeatedly_NeverLoseWal()
    {
        // The race: a loser reads the empty file, the winner then writes page 1, the loser's page_count sees 1 and gives up. One round does not always hit it;
        // many rounds make a regression fail reliably instead of once in a full-suite run.
        for (int round = 0; round < 40; round++)
        {
            await ConcurrentFreshOpens($"fresh-concurrent-{round}.db");
        }
    }

    [Fact]
    public async Task AFreshDatabaseFile_ConfiguredNormal_ConcurrentSyncOpensRepeatedly_NeverLoseWal()
    {
        // Same race through the synchronous open (ConnectionOpened -> WalIfEmpty), which the async rounds above do not exercise.
        for (int round = 0; round < 40; round++)
        {
            await ConcurrentFreshOpens($"fresh-concurrent-sync-{round}.db", useSyncOpen: true);
        }
    }

    private async Task ConcurrentFreshOpens(string fileName, bool useSyncOpen = false)
    {
        // Many connections opening a file that does not exist yet at the same moment: each may see the empty rollback-journal file and try the WAL switch,
        // which needs an exclusive lock. busy_timeout is set before the switch, so the losers wait instead of failing: no open throws, every connection ends in
        // WAL on NORMAL, and nothing is logged as refused. A loser that read journal_mode before the winner's switch and page_count after it sees a non-empty
        // file; it must re-read the mode rather than judge a WAL file by the stale answer.
        string path = Path.Combine(_directory, fileName);
        Assert.False(File.Exists(path));
        string connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        var errors = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(logging => logging.AddProvider(errors));
        SqliteConnectionPragmaInterceptor pragma = SqliteConnectionPragmaInterceptor.For(5000, 0, SqliteSynchronousMode.Normal, factory.CreateLogger("concurrent-open"));
        using var start = new Barrier(8);

        long[] synchronous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = new DbContext(new DbContextOptionsBuilder<DbContext>().UseSqlite(connection).AddInterceptors(pragma).Options);
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)), "barrier timed out: the opens were not concurrent");
            if (useSyncOpen)
            {
                db.Database.OpenConnection();
            }
            else
            {
                await db.Database.OpenConnectionAsync();
            }

            Assert.Equal("wal", await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA journal_mode"));
            return (long)(await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"))!;
        })));

        Assert.All(synchronous, value => Assert.Equal(1L, value));
        Assert.Equal(SqliteSynchronousMode.Normal, pragma.EffectiveFor(path));
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public void EfCreate_SwitchesANewFileToWal_AfterOpening()
    {
        // Pins the third-party behaviour the interceptor's comment relies on: EF's SqliteDatabaseCreator.Create runs PRAGMA journal_mode = 'wal' after it has
        // opened the connection (so an interceptor on that open still sees the default rollback journal). No interceptor here.
        string path = Path.Combine(_directory, "ef-create.db");
        using var db = new DbContext(new DbContextOptionsBuilder<DbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
        db.GetService<IRelationalDatabaseCreator>().Create();
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        raw.Open();
        using SqliteCommand command = raw.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task AnExistingRollbackJournalDatabase_ConfiguredNormal_IsLeftAloneAndStaysOnFull()
    {
        // Fail closed is unchanged for a file that already has content: the interceptor never rewrites a populated database's journal mode
        // (the schema bootstrap does that, only once it has accepted the schema), so a bare open of a 'delete'-mode file stays on FULL.
        string connection = Connection("rollback.db");
        await using (var raw = new SqliteConnection(connection))
        {
            await raw.OpenAsync();
            await ScalarAsync(raw, "CREATE TABLE t(id INTEGER)");
            Assert.Equal("delete", await ScalarAsync(raw, "PRAGMA journal_mode"));
        }

        var builder = new DbContextOptionsBuilder<DbContext>().UseSqlite(connection).AddInterceptors(SqliteConnectionPragmaInterceptor.For(300, 0, SqliteSynchronousMode.Normal));
        await using var db = new DbContext(builder.Options);
        await db.Database.OpenConnectionAsync();
        Assert.Equal("delete", await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA journal_mode"));
        Assert.Equal(2L, await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"));
    }

    [Fact]
    public async Task TheInterceptor_AppliesTheSynchronousModeItWasBuiltWith()
    {
        foreach ((SqliteSynchronousMode mode, long expected) in new[] { (SqliteSynchronousMode.Normal, 1L), (SqliteSynchronousMode.Full, 2L) })
        {
            var builder = new DbContextOptionsBuilder<DbContext>().UseSqlite(WalConnection($"sync-{mode}.db")).AddInterceptors(SqliteConnectionPragmaInterceptor.For(300, 0, mode));
            await using var db = new DbContext(builder.Options);
            await db.Database.OpenConnectionAsync();
            Assert.Equal(expected, await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"));
        }
    }

    [Fact]
    public void AnUnknownSynchronousModeIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(5000, 0, (SqliteSynchronousMode)7));
        Assert.Same(SqliteConnectionPragmaInterceptor.For(5000, 0, SqliteSynchronousMode.Normal), SqliteConnectionPragmaInterceptor.For(5000, 0, SqliteSynchronousMode.Normal));
        Assert.NotSame(SqliteConnectionPragmaInterceptor.For(5000, 0, SqliteSynchronousMode.Normal), SqliteConnectionPragmaInterceptor.For(5000, 0, SqliteSynchronousMode.Full));
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
    public async Task ContenderBlocksForAtLeastTheConfiguredBusyTimeoutBeforeSqliteBusy()
    {
        // Lower bound only, so machine load can lengthen the wait but never fail the test. Windows sleep granularity is why the bound
        // sits below the configured 300 ms: SQLite's busy handler sleeps the whole timeout in slices.
        (int result, TimeSpan elapsed) = await ContendForTheWriteLockAsync(busyTimeoutMs: 300);
        Assert.Equal(5, result); // SQLITE_BUSY: the holder never released, so the wait ended by timing out.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(250), $"the contender returned SQLITE_BUSY after {elapsed.TotalMilliseconds:F0} ms; busy_timeout=300 must hold it for at least 250 ms");
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task ContenderGetsSqliteBusyAtOnceWhenTheBusyTimeoutIsZero()
    {
        // The control for the test above: without the pragma the same call fails immediately, so the 250 ms there is the pragma's doing.
        (int result, TimeSpan elapsed) = await ContendForTheWriteLockAsync(busyTimeoutMs: 0);
        Assert.Equal(5, result);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(250), $"with busy_timeout=0 the contender took {elapsed.TotalMilliseconds:F0} ms to get SQLITE_BUSY");
    }

    /// <summary>
    /// One connection holds <c>BEGIN IMMEDIATE</c> for the whole measurement. A second connection, opened through EF so the interceptor
    /// applies <c>busy_timeout</c>, then asks for the write lock directly on the SQLite handle. Going to the handle bypasses
    /// Microsoft.Data.Sqlite's command retry loop (which retries SQLITE_BUSY for the command timeout and would rescue the call), so
    /// the elapsed time is SQLite's own busy handler and nothing else.
    /// </summary>
    private async Task<(int Result, TimeSpan Elapsed)> ContendForTheWriteLockAsync(int busyTimeoutMs)
    {
        string connection = Connection("writers.db");
        await using ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection),
            ("Database:SqliteBusyTimeoutMs", busyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await InitializeProbeAsync(provider, connection);

        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await ScalarAsync(holder, "BEGIN IMMEDIATE");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await db.Database.OpenConnectionAsync();
        var contender = (SqliteConnection)db.Database.GetDbConnection();
        Assert.Equal((long)busyTimeoutMs, await ScalarAsync(contender, "PRAGMA busy_timeout"));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int result = SQLitePCL.raw.sqlite3_exec(contender.Handle, "BEGIN IMMEDIATE");
        clock.Stop();
        if (result == 0)
        {
            SQLitePCL.raw.sqlite3_exec(contender.Handle, "ROLLBACK");
        }

        await ScalarAsync(holder, "ROLLBACK");
        return (result, clock.Elapsed);
    }

    [Fact]
    public void MariaDbAndPostgreSqlGetNoInterceptor()
    {
        var postgres = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(postgres, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.PostgreSql, ConnectionString = $"Host=127.0.0.1;Port={ClosedLoopbackPort.Next()};Database=arcane;Username=arcane;Password=arcane;",
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
            Provider = DatabaseProvider.MariaDb, ConnectionString = $"Server=127.0.0.1;Port={ClosedLoopbackPort.Next()};Database=arcane;User=arcane;Password=arcane;Connect Timeout=1;",
        }));
        Assert.DoesNotContain(Interceptors(maria), interceptor => interceptor is SqliteConnectionPragmaInterceptor);
    }

    private async Task InitializeProbeAsync(ServiceProvider provider, string connection)
    {
        int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(), provider, new StringWriter());
        Assert.Equal(0, exit);
        await using var setup = new SqliteConnection(connection);
        await setup.OpenAsync();
        await ScalarAsync(setup, "CREATE TABLE IF NOT EXISTS pragma_probe(id INTEGER)");
    }

    [Fact]
    public async Task TheInterceptor_ReplacesAnUnlimitedDefaultTimeoutWithTheWriteBound_AndDerivesBusyTimeoutFromIt()
    {
        string connection = Connection("unlimited.db", defaultTimeout: 0);
        var builder = new DbContextOptionsBuilder<DbContext>().UseSqlite(connection).AddInterceptors(SqliteConnectionPragmaInterceptor.For(300, 1000));
        await using var db = new DbContext(builder.Options);
        await db.Database.OpenConnectionAsync();
        var sqlite = (SqliteConnection)db.Database.GetDbConnection();
        Assert.Equal(1, sqlite.DefaultTimeout);
        Assert.Equal(300L, await ScalarAsync(sqlite, "PRAGMA busy_timeout"));
    }

    [Fact]
    public async Task AddAuthDatabase_PoolsAuthDbContextInstancesAcrossScopes()
    {
        string auth = Connection("pooled-auth.db");
        await using ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", auth));

        AuthDbContext firstInstance;
        await using (AsyncServiceScope scope1 = provider.CreateAsyncScope())
        {
            firstInstance = scope1.ServiceProvider.GetRequiredService<AuthDbContext>();
            firstInstance.Accounts.Attach(new ArcaneCore.Kernel.Accounts.Account
            {
                Id = 999,
                Username = "POOLED",
                Salt = new byte[32],
                Verifier = new byte[32],
            });
            Assert.NotEmpty(firstInstance.ChangeTracker.Entries());
        }

        await using (AsyncServiceScope scope2 = provider.CreateAsyncScope())
        {
            AuthDbContext secondInstance = scope2.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.Same(firstInstance, secondInstance);
            Assert.Empty(secondInstance.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task PooledSqliteHandle_SkipsRedundantPragmaExecutionOnReopen_UntilConfigurationChanges()
    {
        string path = Path.Combine(_directory, "pooled-handle-skip.db");
        string pooledConn = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 0 }.ToString();
        try
        {
            SqliteConnectionPragmaInterceptor normalInterceptor = SqliteConnectionPragmaInterceptor.For(5000, 1000, SqliteSynchronousMode.Normal);
            DbContextOptions<DbContext> normalOptions = new DbContextOptionsBuilder<DbContext>()
                .UseSqlite(pooledConn)
                .AddInterceptors(normalInterceptor)
                .Options;

            await using (var db1 = new DbContext(normalOptions))
            {
                await db1.Database.OpenConnectionAsync();
                var sqlite1 = (SqliteConnection)db1.Database.GetDbConnection();
                Assert.Equal(1, sqlite1.DefaultTimeout);
                Assert.Equal("wal", await ScalarAsync(sqlite1, "PRAGMA journal_mode"));
                Assert.Equal(1000L, await ScalarAsync(sqlite1, "PRAGMA busy_timeout"));
                Assert.Equal(1L, await ScalarAsync(sqlite1, "PRAGMA synchronous"));

                // Mutate the live handle's busy_timeout directly; a pooled reopen with identical interceptor settings must skip re-issuing PRAGMAs.
                await ScalarAsync(sqlite1, "PRAGMA busy_timeout = 777");
                Assert.Equal(777L, await ScalarAsync(sqlite1, "PRAGMA busy_timeout"));
            }

            await using (var db2 = new DbContext(normalOptions))
            {
                await db2.Database.OpenConnectionAsync();
                var sqlite2 = (SqliteConnection)db2.Database.GetDbConnection();
                Assert.Equal(1, sqlite2.DefaultTimeout);
                Assert.Equal(777L, await ScalarAsync(sqlite2, "PRAGMA busy_timeout"));
            }

            // Reopening the same pooled handle with a different interceptor configuration must re-apply the PRAGMAs.
            SqliteConnectionPragmaInterceptor fullInterceptor = SqliteConnectionPragmaInterceptor.For(400, 1000, SqliteSynchronousMode.Full);
            DbContextOptions<DbContext> fullOptions = new DbContextOptionsBuilder<DbContext>()
                .UseSqlite(pooledConn)
                .AddInterceptors(fullInterceptor)
                .Options;

            await using (var db3 = new DbContext(fullOptions))
            {
                await db3.Database.OpenConnectionAsync();
                var sqlite3 = (SqliteConnection)db3.Database.GetDbConnection();
                Assert.Equal(400L, await ScalarAsync(sqlite3, "PRAGMA busy_timeout"));
                Assert.Equal(2L, await ScalarAsync(sqlite3, "PRAGMA synchronous"));
            }
        }
        finally
        {
            using var clear = new SqliteConnection(pooledConn);
            SqliteConnection.ClearPool(clear);
        }
    }

    /// <summary>A connection string for a database file that is already in WAL mode (the mode is persistent in the file).</summary>
    private string WalConnection(string name)
    {
        string connection = Connection(name);
        using var raw = new SqliteConnection(connection);
        raw.Open();
        using SqliteCommand command = raw.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        Assert.Equal("wal", command.ExecuteScalar());
        return connection;
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

    private static async Task<long> SynchronousAsync(ServiceProvider provider, DatabaseComponent component)
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
            return (long)(await ScalarAsync(db.Database.GetDbConnection(), "PRAGMA synchronous"))!;
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

    private static ServiceProvider Build(params (string Key, string? Value)[] values) => Build(false, values);

    private static ServiceProvider Build(bool withGuard, (string Key, string? Value)[] values, ILoggerProvider? logs = null)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });
        services.AddAuthDatabase(config);
        services.AddCharacterDatabase(config);
        services.AddWorldDatabase(config);
        if (withGuard)
        {
            services.AddDatabaseResilience(config);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Collects every Error-or-worse line (the console's "FAIL") from any category.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Errors.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
