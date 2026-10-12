using System.Text.RegularExpressions;
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
/// The 2026-10-11 w29 incident: "database is locked" windows nobody could attribute because the holder was an autocommit write or a transaction
/// that was never ended. Covers <see cref="SlowCommandInterceptor"/>, the commit/open split and the disposed-without-commit warning of
/// <see cref="SlowTransactionInterceptor"/>. Every connection string is <c>Pooling=False</c> on a temp file.
/// </summary>
public sealed class SlowCommandInterceptorTests : IDisposable
{
    private const string Secret = "TopSecretName";
    private const string HeavySelect = "SELECT 'hunter2', count(*) FROM (WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 3000000) SELECT x FROM c)";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-slow-command-" + Guid.NewGuid().ToString("N"));

    public SlowCommandInterceptorTests() => Directory.CreateDirectory(_directory);

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
    public async Task ASlowAutocommitSaveChanges_IsReportedByTheCommandInterceptor_AndNeverByTheTransactionInterceptorAlone()
    {
        // Same lock window twice: once with only the pre-existing transaction interceptor armed (the old behaviour), once with the command interceptor too.
        (string Connection, int Id) oldSetup = await NewDatabaseAsync();
        var oldLogs = new CaptureProvider();
        await using (ServiceProvider provider = Build(oldLogs, oldSetup.Connection, commandMs: "0", transactionMs: "200"))
        {
            int before = oldLogs.Entries.Count;
            await WriteWhileLockedAsync(provider, oldSetup.Connection, oldSetup.Id, useSaveChanges: true);
            Assert.DoesNotContain(oldLogs.Entries.Skip(before), e => e.Level == LogLevel.Warning);
        }

        (string Connection, int Id) newSetup = await NewDatabaseAsync();
        var logs = new CaptureProvider();
        await using (ServiceProvider provider = Build(logs, newSetup.Connection, commandMs: "200", transactionMs: "200"))
        {
            int before = logs.Entries.Count;
            await WriteWhileLockedAsync(provider, newSetup.Connection, newSetup.Id, useSaveChanges: true);

            (LogLevel Level, string Message) warning = Assert.Single(logs.Entries.Skip(before), e => e.Level == LogLevel.Warning);
            Assert.Contains("slow database command on CharacterDbContext", warning.Message, StringComparison.Ordinal);
            Assert.Contains("completed", warning.Message, StringComparison.Ordinal);
            Assert.Contains("autocommit, no transaction", warning.Message, StringComparison.Ordinal);
            Assert.Contains("; write;", warning.Message, StringComparison.Ordinal);
            Assert.Contains("UPDATE", warning.Message, StringComparison.Ordinal);
            Assert.True(warning.Message.Contains(nameof(SlowCommandInterceptorTests) + "." + nameof(WriteWhileLockedAsync), StringComparison.Ordinal), warning.Message);
            Assert.DoesNotContain(Secret, warning.Message, StringComparison.Ordinal);
            Match duration = Regex.Match(warning.Message, @"command on CharacterDbContext: (\d+) ms");
            Assert.True(duration.Success && long.Parse(duration.Groups[1].Value) >= 200, warning.Message);
        }
    }

    [Fact]
    public async Task ASlowAutocommitExecuteUpdate_IsReportedAsAWrite()
    {
        (string Connection, int Id) setup = await NewDatabaseAsync();
        var logs = new CaptureProvider();
        await using ServiceProvider provider = Build(logs, setup.Connection, commandMs: "200", transactionMs: "200");
        int before = logs.Entries.Count;
        await WriteWhileLockedAsync(provider, setup.Connection, setup.Id, useSaveChanges: false);

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries.Skip(before), e => e.Level == LogLevel.Warning);
        Assert.Contains("autocommit, no transaction", warning.Message, StringComparison.Ordinal);
        Assert.Contains("; write;", warning.Message, StringComparison.Ordinal);
        Assert.Contains("UPDATE", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowReadInsideATransaction_IsReportedAsInATransaction_WithLiteralsMasked()
    {
        var logs = new CaptureProvider();
        await using ServiceProvider provider = BuildCommandOnly(logs, new SlowCommandInterceptor(50, new CaptureLogger(logs)));
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(HeavySelect);
        await transaction.CommitAsync();

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries, e => e.Message.Contains("slow database command", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("inside a transaction", warning.Message, StringComparison.Ordinal);
        Assert.Contains("; read;", warning.Message, StringComparison.Ordinal);
        Assert.Contains("SELECT '?', count(*) FROM (WITH RECURSIVE", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalSlowCommands_AreRateLimited_AndTheNextWarningCountsTheSuppressedOnes()
    {
        var logs = new CaptureProvider();
        var time = new ManualTime();
        await using ServiceProvider provider = BuildCommandOnly(logs, new SlowCommandInterceptor(1, new CaptureLogger(logs), time));
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();

        for (int i = 0; i < 3; i++)
        {
            await db.Database.ExecuteSqlRawAsync(HeavySelect);
        }

        Assert.Single(logs.Entries, e => e.Message.Contains("slow database command", StringComparison.Ordinal));
        Assert.Contains("0 identical warnings suppressed", logs.Entries.Single(e => e.Message.Contains("slow database command", StringComparison.Ordinal)).Message, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(SlowCommandInterceptor.WindowSeconds + 1));
        await db.Database.ExecuteSqlRawAsync(HeavySelect);

        List<(LogLevel Level, string Message)> all = [.. logs.Entries.Where(e => e.Message.Contains("slow database command", StringComparison.Ordinal))];
        Assert.Equal(2, all.Count);
        Assert.Contains("2 identical warnings suppressed", all[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSqlPrefix_SkipsComments_MasksLiterals_Truncates_AndClassifiesTheStatement()
    {
        Assert.Equal("UPDATE t SET a = @p0 WHERE id = @p1", SlowCommandInterceptor.SqlPrefix("-- tag\n/* x */\n  UPDATE   t\nSET a = @p0 WHERE id = @p1", out string kind));
        Assert.Equal("write", kind);
        Assert.Equal("INSERT INTO t VALUES ('?', ?, ?)", SlowCommandInterceptor.SqlPrefix("INSERT INTO t VALUES ('o''brien', 42, 3.5)", out kind));
        Assert.Equal("write", kind);
        Assert.Equal("read", SlowCommandInterceptor.SqlPrefixKind("select 1"));
        Assert.Equal("write", SlowCommandInterceptor.SqlPrefixKind("REPLACE INTO t VALUES (1)"));
        Assert.Equal("write", SlowCommandInterceptor.SqlPrefixKind("delete from t"));
        Assert.Equal("other", SlowCommandInterceptor.SqlPrefixKind("PRAGMA foreign_keys = ON"));
        Assert.Equal("other", SlowCommandInterceptor.SqlPrefixKind("SELECTED"));
        Assert.Equal("SELECT * FROM t WHERE name = '?'", SlowCommandInterceptor.SqlPrefix("SELECT * FROM t WHERE name = 'unterminated", out _));

        string prefix = SlowCommandInterceptor.SqlPrefix("SELECT " + new string('a', 200), out _);
        Assert.Equal(SlowCommandInterceptor.SqlPrefixLength + 3, prefix.Length);
        Assert.EndsWith("...", prefix, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SlowCommandInterceptor.SqlPrefix("   ", out kind));
        Assert.Equal("other", kind);
    }

    [Fact]
    public void TheSqlPrefix_MasksDoubleQuotedText_KeepsOnlyTableNames_HandlesBackslashEscapes_LeadingDotNumbers_AndDropsComments()
    {
        // An EF-generated SQLite statement stays attributable: the table survives, columns and aliases do not.
        Assert.Equal("UPDATE \"characters\" SET \"?\" = @p0 WHERE \"?\" = @p1 RETURNING ?;",
            SlowCommandInterceptor.SqlPrefix("UPDATE \"characters\" SET \"Name\" = @p0\nWHERE \"Id\" = @p1\nRETURNING 1;", out _));
        Assert.Equal("SELECT \"?\".\"?\" FROM \"characters\" AS \"?\"", SlowCommandInterceptor.SqlPrefix("SELECT \"c\".\"Id\" FROM \"characters\" AS \"c\"", out _));
        Assert.Equal("SELECT * FROM \"?\" WHERE a = \"?\"", SlowCommandInterceptor.SqlPrefix("SELECT * FROM \"two words\" WHERE a = \"hunter2\"", out _));

        Assert.Equal("WHERE n = '?' AND m = ?", SlowCommandInterceptor.SqlPrefix("WHERE n = 'it\\'s secret' AND m = 1", out _));
        Assert.Equal("WHERE n = E'?' AND m = ?", SlowCommandInterceptor.SqlPrefix("WHERE n = E'a\\'b secret' AND m = 1", out _));
        Assert.Equal("WHERE n = '?'", SlowCommandInterceptor.SqlPrefix("WHERE n = 'o''brien secret'", out _));

        Assert.Equal("WHERE x > ? AND y = ? AND z = t.c", SlowCommandInterceptor.SqlPrefix("WHERE x > .5 AND y = 1.5e3 AND z = t.c", out _));

        Assert.Equal("SELECT ? FROM t WHERE a = ?", SlowCommandInterceptor.SqlPrefix("SELECT 1 /* secret1 */ FROM t -- secret2 42\n WHERE a = 2", out _));
    }

    [Fact]
    public void AWithStatement_IsClassifiedByItsMainStatement()
    {
        Assert.Equal("write", SlowCommandInterceptor.SqlPrefixKind("WITH c AS (SELECT 1) UPDATE t SET a = 1"));
        Assert.Equal("write", SlowCommandInterceptor.SqlPrefixKind("WITH r AS (SELECT 1), s AS (SELECT 2) INSERT INTO t SELECT * FROM r"));
        Assert.Equal("write", SlowCommandInterceptor.SqlPrefixKind("-- tag\nWITH r AS (SELECT 'delete') DELETE FROM t"));
        Assert.Equal("read", SlowCommandInterceptor.SqlPrefixKind("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 3) SELECT x FROM c"));
        Assert.Equal("read", SlowCommandInterceptor.SqlPrefixKind("WITH update_x AS (SELECT 1) SELECT 1 FROM update_x"));
        Assert.Equal("other", SlowCommandInterceptor.SqlPrefixKind("WITH c AS (SELECT 1)"));
    }

    [Fact]
    public async Task AFastCommand_AndAZeroThreshold_AreNotLogged()
    {
        var fast = new CaptureProvider();
        await using (ServiceProvider provider = BuildCommandOnly(fast, new SlowCommandInterceptor(60000, new CaptureLogger(fast))))
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CharacterDbContext>().Database.ExecuteSqlRawAsync(HeavySelect);
        }

        var disabled = new CaptureProvider();
        await using (ServiceProvider provider = BuildCommandOnly(disabled, new SlowCommandInterceptor(0, new CaptureLogger(disabled))))
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CharacterDbContext>().Database.ExecuteSqlRawAsync(HeavySelect);
        }

        Assert.Empty(fast.Entries);
        Assert.Empty(disabled.Entries);
    }

    [Fact]
    public async Task AWriteThatFailsAsLocked_IsReportedAsFailed()
    {
        (string Connection, int Id) setup = await NewDatabaseAsync();
        var logs = new CaptureProvider();
        await using ServiceProvider provider = Build(logs, new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = setup.Connection,
            ["Database:SlowCommandWarnMs"] = "200",
            ["Database:SlowTransactionWarnMs"] = "0",
            ["Database:SqliteBusyTimeoutMs"] = "300",
            ["Database:SqliteWriteTimeoutMs"] = "1000",
        });

        await using var holder = new SqliteConnection(setup.Connection);
        await holder.OpenAsync();
        await using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        try
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            // Open first: a command that EF creates before it opens the connection keeps the 30 s Default Timeout (see the report), which is not what this test measures.
            await db.Database.OpenConnectionAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Exception? thrown = await Record.ExceptionAsync(
                () => db.Characters.Where(c => c.Id == setup.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, Secret)));
            watch.Stop();
            Assert.NotNull(thrown);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the locked write took {watch.Elapsed}");
            Assert.True(DatabaseTransience.IsBusy(thrown), "expected SQLite busy/locked, got " + thrown);
        }
        finally
        {
            await using SqliteCommand rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries, e => e.Message.Contains("slow database command", StringComparison.Ordinal));
        Assert.Contains("failed", warning.Message, StringComparison.Ordinal);
        Assert.Contains("autocommit, no transaction", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransactionLogsItsHeldTimeAndItsCommitTimeSeparately()
    {
        var logs = new CaptureProvider();
        (string Connection, int Id) setup = await NewDatabaseAsync();
        await using ServiceProvider provider = Build(logs, setup.Connection, commandMs: "0", transactionMs: "50");

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Task.Delay(300);
            await transaction.CommitAsync();
        }

        (LogLevel Level, string Message) warning = Assert.Single(logs.Entries, e => e.Message.Contains(nameof(SlowCommandInterceptorTests), StringComparison.Ordinal));
        Assert.Contains("committed", warning.Message, StringComparison.Ordinal);
        Match split = Regex.Match(warning.Message, @"was open (\d+) ms .*?: (\d+) ms held by the application before the end call and (\d+) ms inside the end call");
        Assert.True(split.Success, warning.Message);
        long open = long.Parse(split.Groups[1].Value);
        long held = long.Parse(split.Groups[2].Value);
        long inside = long.Parse(split.Groups[3].Value);
        Assert.True(held >= 250, warning.Message);
        Assert.True(inside >= 0 && held + inside == open, warning.Message);
    }

    [Fact]
    public async Task ATransactionDisposedWithoutCommit_IsReported_Sync_AndAsync_AndOnlyOnce()
    {
        var logs = new CaptureProvider();
        (string Connection, int Id) setup = await NewDatabaseAsync();
        await using ServiceProvider provider = Build(logs, setup.Connection, commandMs: "0", transactionMs: "50");

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Task.Delay(150);
            }
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Task.Delay(150);
            }
        }

        List<(LogLevel Level, string Message)> abandoned = [.. logs.Entries.Where(e => e.Message.Contains("disposed without a commit or rollback", StringComparison.Ordinal))];
        Assert.Equal(2, abandoned.Count);
        Assert.All(abandoned, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Contains("CharacterDbContext", e.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ATransactionDisposedWithoutCommit_IsReported_Sync_AndAsync_AndOnlyOnce), e.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ACommittedOrRolledBackTransaction_IsNeverReportedAsAbandoned()
    {
        var logs = new CaptureProvider();
        (string Connection, int Id) setup = await NewDatabaseAsync();
        await using ServiceProvider provider = Build(logs, setup.Connection, commandMs: "0", transactionMs: "50");

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using (var committed = await db.Database.BeginTransactionAsync())
            {
                await Task.Delay(100);
                await committed.CommitAsync();
            }

            await using (var rolledBack = await db.Database.BeginTransactionAsync())
            {
                await Task.Delay(100);
                await rolledBack.RollbackAsync();
            }
        }

        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("disposed without", StringComparison.Ordinal));
        Assert.Equal(2, logs.Entries.Count(e => e.Message.Contains(nameof(SlowCommandInterceptorTests), StringComparison.Ordinal)));
    }

    private async Task<(string Connection, int Id)> NewDatabaseAsync()
    {
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        await using ServiceProvider provider = Build(null, connection, commandMs: "0", transactionMs: "0");
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CharacterRecord created = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
            .CreateAsync(new CharacterRecord { AccountId = 1, Name = "Lockwood", Race = 1, Class = 1, Level = 1 });
        return (connection, created.Id);
    }

    /// <summary>One single-command autocommit write while a second connection holds the write lock for about 1.5 s.</summary>
    private static async Task WriteWhileLockedAsync(ServiceProvider provider, string connection, int id, bool useSaveChanges)
    {
        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        Task release = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await using SqliteCommand rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        });

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            if (useSaveChanges)
            {
                // Tracked before the lock window matters: the read is not blocked by a WAL writer.
                CharacterRecord record = await db.Characters.SingleAsync(c => c.Id == id);
                record.Name = Secret;
                await db.SaveChangesAsync();
            }
            else
            {
                await db.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, Secret));
            }
        }

        await release;
    }

    private ServiceProvider Build(CaptureProvider? logs, string connection, string commandMs, string transactionMs)
        => Build(logs, new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = connection,
            ["Database:SlowCommandWarnMs"] = commandMs,
            ["Database:SlowTransactionWarnMs"] = transactionMs,
        });

    private static ServiceProvider Build(CaptureProvider? logs, Dictionary<string, string?> values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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

    /// <summary>A characters context on a fresh temp file with only the given command interceptor (so a test controls its logger and clock).</summary>
    private ServiceProvider BuildCommandOnly(CaptureProvider logs, SlowCommandInterceptor interceptor)
    {
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        var services = new ServiceCollection();
        services.AddDbContext<CharacterDbContext>(builder => DataServiceCollectionExtensions.ConfigureProvider(
            builder, new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = connection }, slowCommands: interceptor));
        return services.BuildServiceProvider();
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _now = 1_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public override long TimestampFrequency => 1000;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, (long)(by.TotalSeconds * TimestampFrequency));
    }

    private sealed class CaptureLogger(CaptureProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => provider.Log(logLevel, eventId, state, exception, formatter);
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
