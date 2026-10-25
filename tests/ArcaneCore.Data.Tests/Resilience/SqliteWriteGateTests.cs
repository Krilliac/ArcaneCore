using ArcaneCore.Data.Resilience;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// The in-process SQLite single writer (<c>Database:Sqlite:SingleWriter</c>): many concurrent EF writers on one file queue on the gate instead of failing
/// as locked; reads and deferred snapshots never take it; a waiter that times out reports SQLite busy and names the holder; a leaked hold is released. Every
/// connection string is <c>Pooling=False</c> so no pooled handle outlives the temp directory.
/// </summary>
public sealed class SqliteWriteGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-write-gate-" + Guid.NewGuid().ToString("N"));

    public SqliteWriteGateTests() => Directory.CreateDirectory(_directory);

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

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", true)]
    [InlineData("  \r\n update t set a = 1", true)]
    [InlineData("-- comment\nDELETE FROM t", true)]
    [InlineData("/* x */ REPLACE INTO t VALUES (1)", true)]
    [InlineData("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c", true)]
    [InlineData("CREATE TABLE x (a)", true)]
    [InlineData("SELECT * FROM t", false)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", false)]
    [InlineData("PRAGMA busy_timeout = 5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWrite_ClassifiesByTheFirstKeyword(string? sql, bool expected)
        => Assert.Equal(expected, SqliteWriteGateInterceptor.IsWrite(sql));

    [Fact]
    public async Task ManyConcurrentWriteTransactions_AllCommit_WithoutABusyFailure()
    {
        string connection = ConnectionString("concurrent.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, StatsIntervalMs = 0, WarnWaitMs = 0 });
        await using (GateDb db = Create(connection, gate)) await db.Database.EnsureCreatedAsync();

        const int writers = 16, perWriter = 25;
        var failures = new List<Exception>();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            for (int seq = 0; seq < perWriter; seq++)
            {
                try
                {
                    // A generous write bound: the gate's wait is bounded by it, and a loaded test host (parallel test classes starving the thread pool, Windows' 15.6 ms timer)
                    // must not turn a scheduling delay into a busy failure. A gate that really loses writers still fails (count and first exception are asserted).
                    await using GateDb db = Create(connection, gate, writeTimeoutMs: 30000);
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    db.Rows.Add(new GateRow { Writer = writer, Seq = seq });
                    await db.SaveChangesAsync();
                    // The application holds the write lock a little (the live slow-transaction lines): with 16 writers the queue is ~16 x 8 ms deep.
                    await Task.Delay(8);
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    lock (failures) failures.Add(ex);
                }
            }
        })));

        Assert.True(failures.Count == 0, $"{failures.Count} of {writers * perWriter} failed; first: {failures.FirstOrDefault()}");
        await using (GateDb db = Create(connection, gate))
            Assert.Equal(writers * perWriter, await db.Rows.CountAsync());
        Assert.NotNull(SqliteWriteGate.For(connection));
        Assert.Equal(0, SqliteWriteGate.For(connection)!.Waiting);
        Assert.Null(SqliteWriteGate.For(connection)!.Current);
    }

    [Fact]
    public async Task AnAutocommitSave_TakesAndReturnsTheGate()
    {
        string connection = ConnectionString("autocommit.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, StatsIntervalMs = 0 });
        await using (GateDb db = Create(connection, gate)) await db.Database.EnsureCreatedAsync();
        SqliteWriteGate fileGate = SqliteWriteGate.For(connection)!;
        long before = fileGate.AcquiredTotal;

        await using (GateDb db = Create(connection, gate))
        {
            db.Rows.Add(new GateRow { Writer = 1, Seq = 1 });
            await db.SaveChangesAsync(); // one INSERT: EF runs it without a transaction
            await db.Rows.Where(r => r.Writer == 1).ExecuteUpdateAsync(s => s.SetProperty(r => r.Seq, 2));
            _ = await db.Rows.CountAsync(); // a read: no gate
        }

        Assert.True(fileGate.AcquiredTotal >= before + 2, $"acquired {fileGate.AcquiredTotal - before}");
        Assert.Null(fileGate.Current);
    }

    [Fact]
    public async Task AWriterQueuedBehindAHeldTransaction_TimesOutAsBusy_NamingTheHolder_AndReadsStillRun()
    {
        string connection = ConnectionString("held.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, WaitMs = 300, StatsIntervalMs = 0, WarnWaitMs = 0 });
        await using (GateDb db = Create(connection, gate)) await db.Database.EnsureCreatedAsync();

        await using GateDb holder = Create(connection, gate);
        await using var held = await holder.Database.BeginTransactionAsync();
        holder.Rows.Add(new GateRow { Writer = 9, Seq = 9 });
        await holder.SaveChangesAsync();

        await using (GateDb reader = Create(connection, gate))
            Assert.Equal(0, await reader.Rows.CountAsync()); // WAL reader: not gated, does not see the uncommitted row

        Exception? thrown;
        await using (GateDb writer = Create(connection, gate))
            thrown = await Record.ExceptionAsync(() => writer.Database.BeginTransactionAsync());
        SqliteException busy = Assert.IsType<SqliteException>(thrown);
        Assert.Equal(5, busy.SqliteErrorCode);
        Assert.True(DatabaseTransience.IsBusy(busy));
        Assert.Contains("in-process write gate", busy.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(GateDb), busy.Message, StringComparison.Ordinal);

        await held.CommitAsync();
        await using (GateDb writer = Create(connection, gate))
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task ATransactionDisposedWithoutCommit_ReturnsTheGate()
    {
        string connection = ConnectionString("disposed.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, WaitMs = 500, StatsIntervalMs = 0 });
        await using (GateDb db = Create(connection, gate)) await db.Database.EnsureCreatedAsync();

        await using GateDb same = Create(connection, gate);
        await using (var transaction = await same.Database.BeginTransactionAsync())
        {
            same.Rows.Add(new GateRow { Writer = 1, Seq = 1 });
            await same.SaveChangesAsync();
        } // disposed: rolled back, no commit or rollback call

        Assert.Null(SqliteWriteGate.For(connection)!.Current);
        await using GateDb other = Create(connection, gate);
        await using var next = await other.Database.BeginTransactionAsync();
        await next.CommitAsync();
    }

    [Fact]
    public async Task ATransactionRolledBackBehindEfsBack_IsReleasedByTheNextWaiter_WithinAboutASecond()
    {
        string connection = ConnectionString("behind.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, WaitMs = 4000, StatsIntervalMs = 0, WarnWaitMs = 0 });
        await using (GateDb db = Create(connection, gate)) await db.Database.EnsureCreatedAsync();

        await using GateDb rogue = Create(connection, gate);
        await using var transaction = await rogue.Database.BeginTransactionAsync();
        // The write fence refuses a stale owner exactly like this: straight on the ADO.NET transaction, no EF event.
        await Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(transaction).RollbackAsync();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using (GateDb next = Create(connection, gate))
        {
            await using var second = await next.Database.BeginTransactionAsync();
            await second.CommitAsync();
        }

        Assert.True(watch.ElapsedMilliseconds < 2500, $"the next writer waited {watch.ElapsedMilliseconds} ms");
        Assert.True(SqliteWriteGate.For(connection)!.DeadTotal >= 1);
    }

    [Fact]
    public async Task AHoldOlderThanMaxHold_IsReleasedByTheNextWaiter()
    {
        string connection = ConnectionString("stuck.db");
        SqliteSingleWriterOptions options = new() { Enabled = true, WaitMs = 30000, MaxHoldMs = 200, StatsIntervalMs = 0 };
        SqliteWriteGate gate = SqliteWriteGate.For(connection)!;
        SqliteWriteGate.Hold leaked = gate.Enter(100, "leaked", options.MaxHoldMs)!;
        Assert.NotNull(leaked);

        // The waiter wakes once a second to release the stuck hold. The wait bound is generous because a full parallel suite can starve the thread pool for
        // seconds and a tight bound would turn that scheduling delay into a false failure; a release that never happens still fails at the bound.
        SqliteWriteGate.Hold? next = await gate.EnterAsync(options.WaitMs, "next", options.MaxHoldMs, CancellationToken.None);
        Assert.True(next is not null, $"waiter gave up: timeouts={gate.TimeoutsTotal}, forced={gate.ForcedTotal}, current={gate.Current?.Owner}, leakedHeldMs={leaked.HeldMs:0}");
        Assert.True(leaked.Forced);
        leaked.Release(); // the late release of the leaked holder must not free the gate under "next"
        Assert.Same(next, gate.Current);
        next!.Release();
        Assert.Null(gate.Current);
        Assert.True(gate.ForcedTotal >= 1);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeSettings()
    {
        Assert.Throws<InvalidOperationException>(() => new SqliteSingleWriterOptions { WaitMs = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SqliteSingleWriterOptions { MaxHoldMs = SqliteSingleWriterOptions.MaxMs + 1 }.Validate());
        new SqliteSingleWriterOptions().Validate();
        Assert.True(new SqliteSingleWriterOptions().Enabled); // a fix: always on (2026-10-17), not a setting
    }

    [Fact]
    public void MemoryAndReadOnlyDatabases_HaveNoGate()
    {
        Assert.Null(SqliteWriteGate.For("Data Source=:memory:"));
        Assert.Null(SqliteWriteGate.For("Data Source=x.db;Mode=ReadOnly"));
        Assert.Null(SqliteWriteGate.For("Data Source=file:x.db?mode=memory"));
        Assert.Same(SqliteWriteGate.For(ConnectionString("same.db")), SqliteWriteGate.For(ConnectionString("same.db") + ";Cache=Shared"));
    }

    [Fact]
    public async Task DefaultConfigurationWithWriteGate_PreservesFiveSecondBusyTimeout_AndManagedPlayerbotUpdateUsesOneGatedStatement()
    {
        string connection = ConnectionString("playerbot-gate.db");
        SqliteWriteGateInterceptor gate = Gate(new SqliteSingleWriterOptions { Enabled = true, StatsIntervalMs = 0 });
        var builder = new DbContextOptionsBuilder<ArcaneCore.Data.Characters.CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder,
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = connection },
            sqliteWriteTimeoutMs: SqliteConnectionPragmaInterceptor.DefaultWriteTimeoutMs, writeGate: gate);

        await using var db = new ArcaneCore.Data.Characters.CharacterDbContext(builder.Options);
        await ArcaneCore.Data.Schema.SchemaBootstrapper.EnsureAsync(db, ArcaneCore.Data.Characters.CharacterDbContext.Schema);
        await db.Database.OpenConnectionAsync();
        await using (var pragmaCmd = db.Database.GetDbConnection().CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA busy_timeout;";
            long busyTimeout = Convert.ToInt64(await pragmaCmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(busyTimeout >= 5000, $"expected busy_timeout >= 5000, got {busyTimeout}");
        }

        var character = await new ArcaneCore.Data.Stores.EfCharacterStore(db).CreateAsync(
            new ArcaneCore.Kernel.Characters.CharacterRecord { AccountId = 7, Name = "Gatebot", Race = 1, Class = 1, Level = 1 });
        var store = new ArcaneCore.Data.Characters.Playerbots.EfManagedPlayerbotStore(db);
        var bot = new ArcaneCore.Kernel.Characters.ManagedPlayerbot(Guid.NewGuid(), character.AccountId, character.Id, "account7", false,
            ArcaneCore.Kernel.Characters.ManagedPlayerbotState.Stopped, ArcaneCore.Kernel.Characters.PlayerbotGoalKind.Explore, 0, 0, 0, 100, 100);
        await store.CreateAsync(bot);

        var counter = new CommandCounterInterceptor();
        var countedBuilder = new DbContextOptionsBuilder<ArcaneCore.Data.Characters.CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(countedBuilder,
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = connection },
            sqliteWriteTimeoutMs: SqliteConnectionPragmaInterceptor.DefaultWriteTimeoutMs, writeGate: gate);
        countedBuilder.AddInterceptors(counter);

        await using var countedDb = new ArcaneCore.Data.Characters.CharacterDbContext(countedBuilder.Options);
        var countedStore = new ArcaneCore.Data.Characters.Playerbots.EfManagedPlayerbotStore(countedDb);
        Assert.True(await countedStore.UpdateAsync(bot with { State = ArcaneCore.Kernel.Characters.ManagedPlayerbotState.Running, Revision = 1, UpdatedUnix = 101 }, expectedRevision: 0));
        Assert.Equal(0, counter.SelectCommands);
        Assert.Equal(1, counter.UpdateCommands);
    }

    private sealed class CommandCounterInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int SelectCommands;
        public int UpdateCommands;

        public override System.Data.Common.DbDataReader ReaderExecuted(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result)
        {
            Record(command.CommandText);
            return result;
        }

        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            Record(command.CommandText);
            return new ValueTask<System.Data.Common.DbDataReader>(result);
        }

        public override int NonQueryExecuted(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, int result)
        {
            Record(command.CommandText);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Record(command.CommandText);
            return new ValueTask<int>(result);
        }

        private void Record(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            string trimmed = sql.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref SelectCommands);
            else if (trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref UpdateCommands);
        }
    }

    private string ConnectionString(string file)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, file), Pooling = false }.ToString();

    private static SqliteWriteGateInterceptor Gate(SqliteSingleWriterOptions options) => SqliteWriteGateInterceptor.For(options, logger: null);

    private static GateDb Create(string connection, SqliteWriteGateInterceptor gate, int writeTimeoutMs = 1000)
    {
        var builder = new DbContextOptionsBuilder<GateDb>();
        DataServiceCollectionExtensions.ConfigureProvider(builder,
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = connection, SqliteBusyTimeoutMs = 300, SqliteSynchronous = SqliteSynchronousMode.Normal },
            sqliteWriteTimeoutMs: writeTimeoutMs, writeGate: gate);
        return new GateDb(builder.Options);
    }

    internal sealed class GateDb(DbContextOptions<GateDb> options) : DbContext(options)
    {
        public DbSet<GateRow> Rows => Set<GateRow>();
    }

    internal sealed class GateRow
    {
        public int Id { get; set; }

        public int Writer { get; set; }

        public int Seq { get; set; }
    }
}
