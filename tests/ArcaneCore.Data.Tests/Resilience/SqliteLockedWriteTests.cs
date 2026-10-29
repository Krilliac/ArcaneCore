using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// A second connection holding the SQLite write lock (the 2026-10-11 incident): the busy classifier, the bounded character save, and the
/// slow-transaction warning that names the next holder. Every connection string is <c>Pooling=False</c>.
/// </summary>
public sealed class SqliteLockedWriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-locked-write-" + Guid.NewGuid().ToString("N"));

    public SqliteLockedWriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
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
    public void IsBusy_RecognisesSqliteBusyAndLocked_DirectlyWrappedAndAggregated_AndNothingElse()
    {
        Assert.True(DatabaseTransience.IsBusy(new SqliteException("busy", 5)));
        Assert.True(DatabaseTransience.IsBusy(new SqliteException("locked", 6)));
        Assert.True(DatabaseTransience.IsBusy(new DbUpdateException("save", new SqliteException("busy", 5))));
        Assert.True(DatabaseTransience.IsBusy(new InvalidOperationException("outer", new DbUpdateException("save", new SqliteException("locked", 6)))));
        Assert.True(DatabaseTransience.IsBusy(new AggregateException(new IOException("x"), new SqliteException("busy", 5))));

        Assert.False(DatabaseTransience.IsBusy(new SqliteException("cannot open", 14)));
        Assert.False(DatabaseTransience.IsBusy(new SqliteException("constraint", 19)));
        Assert.False(DatabaseTransience.IsBusy(new DbUpdateException("save", new IOException("disk"))));
        Assert.False(DatabaseTransience.IsBusy(new IOException("injected")));
    }

    [Fact]
    public async Task ACharacterSaveAgainstALockedFile_FailsBusyWithinTheWriteBound_AndSucceedsOnceTheLockIsReleased()
    {
        string file = Path.Combine(_directory, "characters.db");
        string connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 30 }.ToString();
        await using ServiceProvider provider = Build(
            ("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection),
            ("Database:SqliteBusyTimeoutMs", "300"), ("Database:SqliteWriteTimeoutMs", "1000"));
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        int id = await CreateCharacterAsync(provider);

        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await ScalarAsync(holder, "BEGIN IMMEDIATE");
        try
        {
            var watch = Stopwatch.StartNew();
            Exception? thrown;
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
                thrown = await Record.ExceptionAsync(() => store.SaveStateAsync(State(id, 777)));
            }

            watch.Stop();
            Assert.NotNull(thrown);
            Assert.True(DatabaseTransience.IsBusy(thrown), "expected SQLite busy/locked, got " + thrown);
            // The connection's Default Timeout is 30 s: without the bound the save would sit here that long. 1 s bound + 300 ms busy slices + slack.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the locked save took {watch.Elapsed}");
        }
        finally
        {
            await ScalarAsync(holder, "ROLLBACK");
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterStore>().SaveStateAsync(State(id, 777));
        }

        await using AsyncServiceScope read = provider.CreateAsyncScope();
        Assert.Equal(777u, (await read.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(id))!.Money);
    }

    [Fact]
    public async Task TheStartupSchemaLock_WaitsItsOwnTimeout_NotTheCharactersWriteBound()
    {
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "schema.db"), Pooling = false, DefaultTimeout = 30 }.ToString();
        await using ServiceProvider provider = Build(
            ("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection),
            ("Database:SqliteBusyTimeoutMs", "300"), ("Database:SqliteWriteTimeoutMs", "1000"));
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await ScalarAsync(holder, "BEGIN IMMEDIATE");
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await db.Database.OpenConnectionAsync();
        var sqlite = (SqliteConnection)db.Database.GetDbConnection();
        Assert.Equal(1, sqlite.DefaultTimeout); // the characters write bound

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ArcaneCore.Data.Schema.SchemaMismatchException>(
            () => ArcaneCore.Data.Schema.SchemaLock.AcquireAsync(db, "characters", TimeSpan.FromSeconds(3), CancellationToken.None));
        watch.Stop();
        // The lock asked for 3 s: it must wait its own timeout, not give up at the 1 s write bound.
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15));
        Assert.Equal(1, sqlite.DefaultTimeout); // restored

        await ScalarAsync(holder, "ROLLBACK");
        await using (ArcaneCore.Data.Schema.SchemaLock held = await ArcaneCore.Data.Schema.SchemaLock.AcquireAsync(db, "characters", TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            await held.CompleteAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ASlowTransaction_IsLoggedAtWarn_NamingTheContextAndTheCallingCode()
    {
        var logs = new CaptureProvider();
        await using ServiceProvider provider = Build(logs, "50");
        await InitializeAsync(provider);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Task.Delay(150);
            await transaction.CommitAsync();
        }

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(SqliteLockedWriteTests), StringComparison.Ordinal)); // the schema bootstrap's own transaction may warn too at a 50 ms threshold
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("CharacterDbContext", warning.Message, StringComparison.Ordinal);
        Assert.Contains("committed", warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SqliteLockedWriteTests), warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ASlowTransaction_IsLoggedAtWarn_NamingTheContextAndTheCallingCode), warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FULL", "synchronous=FULL")]
    [InlineData("NORMAL", "no WAL fsync under synchronous=NORMAL")]
    public async Task ASlowTransaction_NamesTheSynchronousSettingInEffect(string synchronous, string expected)
    {
        var logs = new CaptureProvider();
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        await using ServiceProvider provider = Build(logs, ("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection),
            ("Database:SlowTransactionWarnMs", "50"), ("Database:SqliteSynchronous", synchronous));
        await InitializeAsync(provider);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Task.Delay(150);
            await transaction.CommitAsync();
        }

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(SqliteLockedWriteTests), StringComparison.Ordinal));
        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalIsRefusedOnADatabaseNotInWalMode_FallsBackToFull_AndLogsAnError()
    {
        // A file left in rollback-journal mode (the WAL switch failed or never ran): NORMAL there can corrupt the database on power loss, so it must not apply.
        // It holds a table: an EMPTY file is a fresh install, which the interceptor puts into WAL itself (SqliteConnectionPragmaTests.AFreshDatabaseFile_...).
        var logs = new CaptureProvider();
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
        string connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using (var raw = new SqliteConnection(connection))
        {
            await raw.OpenAsync();
            Assert.Equal("delete", await ScalarAsync(raw, "PRAGMA journal_mode=DELETE"));
            await ScalarAsync(raw, "CREATE TABLE populated(id INTEGER)");
        }

        await using ServiceProvider provider = Build(logs, ("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection), ("Database:SqliteSynchronous", "NORMAL"));
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await db.Database.OpenConnectionAsync();
            var sqlite = (SqliteConnection)db.Database.GetDbConnection();
            Assert.Equal("delete", await ScalarAsync(sqlite, "PRAGMA journal_mode"));
            Assert.Equal(2L, await ScalarAsync(sqlite, "PRAGMA synchronous")); // FULL, not NORMAL (1)
        }

        (LogLevel Level, string Message) error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("not WAL", error.Message, StringComparison.Ordinal);
        Assert.Contains("delete", error.Message, StringComparison.Ordinal);

        // The same file once it is in WAL mode gets NORMAL (1).
        await using (var raw = new SqliteConnection(connection))
        {
            await raw.OpenAsync();
            Assert.Equal("wal", await ScalarAsync(raw, "PRAGMA journal_mode=WAL"));
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await db.Database.OpenConnectionAsync();
            Assert.Equal(1L, await ScalarAsync((SqliteConnection)db.Database.GetDbConnection(), "PRAGMA synchronous"));
        }
    }

    [Fact]
    public async Task AFastTransaction_IsNotLogged_AndZeroDisablesTheWarning()
    {
        var logs = new CaptureProvider();
        await using (ServiceProvider provider = Build(logs, "60000"))
        {
            await InitializeAsync(provider);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await transaction.CommitAsync();
        }

        var disabled = new CaptureProvider();
        await using (ServiceProvider provider = Build(disabled, "0"))
        {
            await InitializeAsync(provider);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Task.Delay(50);
            await transaction.CommitAsync();
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("database transaction", StringComparison.Ordinal));
        Assert.DoesNotContain(disabled.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("database transaction", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSlowTransactionInterceptorIsAttachedOnEveryProvider()
    {
        var interceptor = new SlowTransactionInterceptor(2000, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var postgres = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(postgres, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.PostgreSql, ConnectionString = $"Host=127.0.0.1;Port={ClosedLoopbackPort.Next()};Database=arcane;Username=arcane;Password=arcane;",
        }, interceptor);
        Assert.Contains(interceptor, Interceptors(postgres));

        var sqlite = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(sqlite, new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:",
        }, interceptor);
        Assert.Contains(interceptor, Interceptors(sqlite));
        Assert.Single(Interceptors(sqlite), i => i is SqliteConnectionPragmaInterceptor);
    }

    private static IEnumerable<object> Interceptors(DbContextOptionsBuilder builder)
        => builder.Options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()?.Interceptors ?? [];

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    private static async Task<int> CreateCharacterAsync(ServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CharacterRecord created = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
            .CreateAsync(new CharacterRecord { AccountId = 1, Name = "Lockwood", Race = 1, Class = 1, Level = 1 });
        return created.Id;
    }

    private static async Task InitializeAsync(ServiceProvider provider)
        => await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private ServiceProvider Build(CaptureProvider logs, string slowTransactionWarnMs)
    {
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        return Build(logs, ("Database:Provider", "Sqlite"), ("Database:ConnectionString", connection), ("Database:SlowTransactionWarnMs", slowTransactionWarnMs));
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] values) => Build(null, values);

    private static ServiceProvider Build(CaptureProvider? logs, params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        services.AddCharacterDatabase(config);
        return services.BuildServiceProvider();
    }

    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public void Dispose()
        {
        }
    }
}
