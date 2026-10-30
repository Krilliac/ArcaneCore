using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// The background WAL checkpointer (<c>Database:Sqlite:Checkpoint</c>): with it on, no commit on an application connection runs a checkpoint (the fsync of the main database file
/// inside a game save that crossed SQLite's 1000-page auto-checkpoint threshold was the multi-second SaveStateAsync stall seen on the live server); the service runs PASSIVE checkpoints only during
/// operation (a TRUNCATE holds the writer lock through the copy and the database fsync) and one TRUNCATE at host stop, loses no committed write, and leaves SQLite's defaults alone when disabled or on
/// another engine. Tests drive <see cref="SqliteWalCheckpointer.RunOnce"/> directly or a hand-driven clock; the one that depends on real timing (a writer's worst commit latency, an absolute bound) is why
/// the class runs serially in <see cref="SqliteCheckpointCollection"/>. All connections are Pooling=False temp files.
/// </summary>
[Collection(SqliteCheckpointCollection.Name)]
public sealed class SqliteCheckpointTests : IDisposable
{
    private const int FrameBytes = 4096;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-checkpoint-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _open = [];

    public SqliteCheckpointTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (IDisposable item in _open)
        {
            item.Dispose();
        }

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
    public void WithTheCheckpointerOn_AnAppConnectionNeverAutoCheckpoints_SoTheWalGrowsPastTheDefaultThreshold()
    {
        string path = NewWalDb("a.db");
        SqliteConnection app = OpenApp(path, new SqliteCheckpointOptions());
        Assert.Equal(0L, Scalar(app, "PRAGMA wal_autocheckpoint"));
        Assert.Equal(SqliteCheckpointOptions.DefaultJournalSizeLimitBytes, Scalar(app, "PRAGMA journal_size_limit"));

        Insert(app, 1500);

        // SQLite's default (1000 pages) would have checkpointed and restarted the WAL inside one of these commits, capping the file near 1000 frames.
        Assert.True(WalBytes(path) > 1100L * FrameBytes, $"WAL is only {WalBytes(path)} bytes: a commit checkpointed");
    }

    [Fact]
    public void TheBackgroundCheckpoint_CopiesTheWalIntoTheDatabase_AndTheNextCommitShrinksTheFileToTheJournalSizeLimit()
    {
        string path = NewWalDb("b.db");
        var options = new SqliteCheckpointOptions { JournalSizeLimitBytes = 64 * 1024 };
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 600);
        long before = WalBytes(path);
        Assert.True(before > 500L * FrameBytes);

        var checkpointer = new SqliteWalCheckpointer(Target(path), options, new ListLogger(), TimeProvider.System);
        SqliteCheckpointResult result = checkpointer.RunOnce();

        Assert.True(result.Ran);
        Assert.False(result.Truncate);
        Assert.False(result.Busy);
        Assert.True(result.LogFrames > 500, $"log frames {result.LogFrames}");
        Assert.Equal(result.LogFrames, result.CheckpointedFrames);

        // The log is fully in the database file now; the next write restarts it and SQLite truncates the file to journal_size_limit.
        Insert(app, 5, start: 10_000);
        long after = WalBytes(path);
        Assert.True(after < before / 4, $"WAL stayed at {after} bytes (was {before})");
    }

    [Fact]
    public void EveryCheckpointRun_AddsOneSampleToItsDatabasesLatencyHistogram()
    {
        string name = "latency-" + Guid.NewGuid().ToString("N");
        string path = NewWalDb(name + ".db");
        var options = new SqliteCheckpointOptions();
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 20);
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, new ListLogger(), TimeProvider.System);
        LatencyHistogram histogram = LatencyRegistry.Shared.Get(LatencyNames.CheckpointPrefix + name);
        Assert.Equal(0, histogram.Count);

        checkpointer.RunOnce();
        Assert.Equal(1, histogram.Count);
        checkpointer.RunOnce();
        Assert.Equal(2, histogram.Count);
    }

    [Fact]
    public void DuringOperation_TheCheckpointIsPassiveOnlyEvenWithAHugeWal_AndOnlyTheShutdownRunTruncates()
    {
        string path = NewWalDb("c.db");
        var options = new SqliteCheckpointOptions();
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 600);
        long before = WalBytes(path);
        Assert.True(before > 2L * 1024 * 1024, $"WAL is {before} bytes");
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, new ListLogger(), TimeProvider.System);

        // TRUNCATE would take the writer lock and hold it through the copy and the database fsync; during operation only PASSIVE runs, and the file keeps its size (journal_size_limit shrinks it later).
        SqliteCheckpointResult operating = checkpointer.RunOnce();
        Assert.True(operating.Ran);
        Assert.False(operating.Truncate);
        Assert.Equal(operating.LogFrames, operating.CheckpointedFrames);
        Assert.Equal(before, WalBytes(path));

        // At host stop, once the world has drained, the shutdown run truncates.
        SqliteCheckpointResult final = checkpointer.RunOnce(final: true);
        Assert.True(final.Truncate);
        Assert.False(final.Busy);
        Assert.Equal(0L, WalBytes(path));
        Assert.Equal(600L, Scalar(app, "SELECT count(*) FROM t"));
    }

    // Models an application crash only (the files as they are on disk while the process is alive): this holds with or without the checkpointer, because WAL replay is SQLite's own.
    // What an OS crash or power loss loses is decided by Database:SqliteSynchronous and the fsync, not by who checkpoints, so it is not modelled here.
    [Fact]
    public void CommittedWritesNotYetCheckpointed_ReplayFromTheWalAfterAnApplicationCrash_ModelsAnAppCrashOnly()
    {
        string path = NewWalDb("d.db");
        SqliteConnection app = OpenApp(path, new SqliteCheckpointOptions());
        Insert(app, 300);
        Assert.True(WalBytes(path) > 0);

        // A crash leaves the database file and the -wal file exactly as they are on disk while the process is running (nothing was checkpointed): copy them aside while
        // the connection is still open, without the -shm index, and open the copy. SQLite must rebuild the index and replay the log.
        string crashed = Path.Combine(_directory, "crashed.db");
        File.Copy(path, crashed);
        File.Copy(path + "-wal", crashed + "-wal");
        using (var recovered = new SqliteConnection(Connection(crashed)))
        {
            recovered.Open();
            Assert.Equal(300L, Scalar(recovered, "SELECT count(*) FROM t"));
        }

        // And an orderly close and reopen keeps them too.
        app.Close();
        using var reopened = new SqliteConnection(Connection(path));
        reopened.Open();
        Assert.Equal(300L, Scalar(reopened, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void WithTheCheckpointerDisabled_AnAppConnectionKeepsSqlitesDefaults()
    {
        string path = NewWalDb("e.db");
        var options = new SqliteCheckpointOptions { Enabled = false };
        SqliteConnection app = OpenApp(path, options);

        Assert.Equal(1000L, Scalar(app, "PRAGMA wal_autocheckpoint"));
        Assert.Equal(-1L, Scalar(app, "PRAGMA journal_size_limit"));
        Assert.Null(SqliteCheckpointTarget.For(Options(path), options));
        Assert.Empty(ServiceFor(path, ("Database:Sqlite:Checkpoint:Enabled", "false")).Targets);

        // The shared interceptor for "no checkpointer" carries no limit.
        Assert.Equal(-1L, SqliteConnectionPragmaInterceptor.For(5000).WalJournalSizeLimit);
    }

    [Fact]
    public void MariaDbAndPostgreSql_GetNoCheckpointer()
    {
        var enabled = new SqliteCheckpointOptions();
        foreach (DatabaseProvider provider in new[] { DatabaseProvider.MariaDb, DatabaseProvider.MySql, DatabaseProvider.PostgreSql })
        {
            Assert.Null(SqliteCheckpointTarget.For(new DatabaseConnectionOptions { Provider = provider, ConnectionString = "Data Source=" + Path.Combine(_directory, "x.db") }, enabled));
            SqliteCheckpointService service = ServiceFor(Path.Combine(_directory, "x.db"), ("Database:Provider", provider.ToString()));
            Assert.Empty(service.Targets);
        }

        // Control: the same settings on SQLite do give a target; in-memory, read-only and URI databases do not.
        Assert.NotNull(SqliteCheckpointTarget.For(Options(Path.Combine(_directory, "x.db")), enabled));
        Assert.Null(SqliteCheckpointTarget.For(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" }, enabled));
        Assert.Null(SqliteCheckpointTarget.For(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=x.db;Mode=ReadOnly" }, enabled));
        Assert.Null(SqliteCheckpointTarget.For(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=file:x.db?mode=memory" }, enabled));
    }

    [Fact]
    public void OneFileUsedByEveryComponent_IsCheckpointedOnce()
    {
        string path = NewWalDb("shared.db");
        SqliteCheckpointService service = ServiceFor(path);
        SqliteCheckpointTarget target = Assert.Single(service.Targets);
        Assert.Equal(Path.GetFullPath(path), target.DatabasePath);
    }

    [Fact]
    public async Task StoppingTheService_RunsAFinalTruncateCheckpoint()
    {
        string path = NewWalDb("stop.db");
        SqliteConnection app = OpenApp(path, new SqliteCheckpointOptions());
        Insert(app, 200);
        Assert.True(WalBytes(path) > 0);

        SqliteCheckpointService service = ServiceFor(path);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0L, WalBytes(path));
        Assert.Equal(200L, Scalar(app, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void ACheckpointThatKeepsFailingToCatchUp_WarnsAfterThirtySeconds_AndOnlyOncePerMinute()
    {
        string path = NewWalDb("stall.db");
        var options = new SqliteCheckpointOptions();
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 20);

        // A reader parked on an old snapshot stops the checkpoint at the frames it can see; later frames stay in the log.
        using var reader = new SqliteConnection(Connection(path));
        reader.Open();
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        Assert.Equal(20L, Scalar(reader, "SELECT count(*) FROM t", snapshot));
        Insert(app, 20, start: 1000);

        var clock = new HandDrivenClock();
        var logger = new ListLogger();
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, logger, clock);

        SqliteCheckpointResult first = checkpointer.RunOnce();
        Assert.True(first.Ran);
        Assert.True(first.CheckpointedFrames < first.LogFrames, "the parked reader did not hold the checkpoint back");
        Assert.Empty(logger.Warnings);

        clock.Advance(TimeSpan.FromSeconds(29));
        checkpointer.RunOnce();
        Assert.Empty(logger.Warnings);

        clock.Advance(TimeSpan.FromSeconds(2));
        checkpointer.RunOnce();
        Assert.Single(logger.Warnings);

        clock.Advance(TimeSpan.FromSeconds(30));
        checkpointer.RunOnce();
        Assert.Single(logger.Warnings); // rate limited

        clock.Advance(TimeSpan.FromSeconds(31));
        checkpointer.RunOnce();
        Assert.Equal(2, logger.Warnings.Count);

        // Once the reader is gone the checkpoint catches up and the next stall starts counting afresh.
        snapshot.Commit();
        SqliteCheckpointResult caughtUp = checkpointer.RunOnce();
        Assert.Equal(caughtUp.LogFrames, caughtUp.CheckpointedFrames);
        clock.Advance(TimeSpan.FromMinutes(5));
        checkpointer.RunOnce();
        Assert.Equal(2, logger.Warnings.Count);
    }

    [Fact]
    public void AWalFileThatStaysAboveFourTimesTheJournalSizeLimitWhileCommitsKeepArriving_WarnsAfterThirtySeconds_AndOnlyOncePerMinute()
    {
        string path = NewWalDb("growth.db");
        var options = new SqliteCheckpointOptions { JournalSizeLimitBytes = 0 }; // threshold is four times the 1 MiB floor: 4 MiB
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 1500);
        Assert.True(WalBytes(path) > 4L * 1024 * 1024, $"WAL is only {WalBytes(path)} bytes");

        // A reader parked on a snapshot keeps the log from restarting, so the file stays large while commits keep arriving.
        using var reader = new SqliteConnection(Connection(path));
        reader.Open();
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        Assert.Equal(1500L, Scalar(reader, "SELECT count(*) FROM t", snapshot));

        var clock = new HandDrivenClock();
        var logger = new ListLogger();
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, logger, clock);
        int next = 10_000;
        int Growth() => logger.Warnings.Count(w => w.Contains("not restarting", StringComparison.Ordinal));
        void Step(TimeSpan advance)
        {
            clock.Advance(advance);
            Insert(app, 1, start: next++); // a commit since the previous run: not idle
            checkpointer.RunOnce();
        }

        Step(TimeSpan.Zero);
        Assert.Equal(0, Growth());

        Step(TimeSpan.FromSeconds(29));
        Assert.Equal(0, Growth());

        Step(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Growth());

        Step(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Growth()); // rate limited

        Step(TimeSpan.FromSeconds(31));
        Assert.Equal(2, Growth());

        // Once the reader is gone the shutdown TRUNCATE empties the file and the condition clears.
        snapshot.Commit();
        checkpointer.RunOnce(final: true);
        Assert.Equal(0L, WalBytes(path));
        clock.Advance(TimeSpan.FromMinutes(5));
        checkpointer.RunOnce();
        Assert.Equal(2, Growth());
    }

    [Fact]
    public void ALargeWalThatIsFullyCopiedAndIdle_NeverWarns()
    {
        string path = NewWalDb("idle.db");
        var options = new SqliteCheckpointOptions { JournalSizeLimitBytes = 0 };
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 1500);
        Assert.True(WalBytes(path) > 4L * 1024 * 1024, $"WAL is only {WalBytes(path)} bytes");

        var clock = new HandDrivenClock();
        var logger = new ListLogger();
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, logger, clock);

        // No writes and no readers: the log is copied, nothing will restart it, and the file keeps its size until the next write or the shutdown TRUNCATE. That is not a fault.
        foreach (int seconds in new[] { 0, 29, 2, 30, 31, 120, 600 })
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            SqliteCheckpointResult result = checkpointer.RunOnce();
            Assert.Equal(result.LogFrames, result.CheckpointedFrames);
        }

        Assert.True(WalBytes(path) > 4L * 1024 * 1024);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void TheSettingsBindFromConfiguration_AndAreValidated()
    {
        string path = NewWalDb("cfg.db");
        SqliteCheckpointService service = ServiceFor(path, ("Database:Sqlite:Checkpoint:IntervalMs", "500"));
        Assert.Single(service.Targets);

        foreach ((string key, string value) in new[]
        {
            ("IntervalMs", "99"), ("IntervalMs", "3600001"),
            ("JournalSizeLimitBytes", "-1"), ("JournalSizeLimitBytes", "1099511627777"),
        })
        {
            Assert.Throws<InvalidOperationException>(() => ServiceFor(path, ($"Database:Sqlite:Checkpoint:{key}", value)));
        }

        // A bad value for a checkpointer that is turned off is not an error: nothing reads it.
        Assert.Empty(ServiceFor(path, ("Database:Sqlite:Checkpoint:Enabled", "false"), ("Database:Sqlite:Checkpoint:IntervalMs", "1")).Targets);
    }

    [Fact]
    public void CheckpointFailure_OnCorruptDatabaseFile_RecordsFaultAndDatabaseAnomaly()
    {
        string path = Path.Combine(_directory, "corrupt.db");
        File.WriteAllText(path, "not a sqlite database file");
        File.WriteAllBytes(path + "-wal", new byte[128]);
        var checkpointer = new SqliteWalCheckpointer(Target(path), new SqliteCheckpointOptions(), new ListLogger(), TimeProvider.System);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "database/checkpoint-failed");
        SqliteCheckpointResult result = checkpointer.RunOnce();

        Assert.False(result.Ran);
        Assert.Contains(capture.Notices, n => n.Subject == Path.GetFullPath(path));
        Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == "data.sqlite-checkpoint");
    }

    private static readonly TimeSpan LockEpisodeBound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pins the operational checkpoint mode by behaviour: with a reader parked on an old snapshot and a commit after it, PASSIVE copies what it can and
    /// reports not busy, while FULL, RESTART and TRUNCATE (busy timeout 0) cannot finish and report busy (and take the writer lock to try).
    /// </summary>
    [Fact]
    public void PassiveCheckpoint_WithAReaderParkedOnAnOldSnapshot_ReportsNotBusy()
    {
        string path = NewWalDb("passive-pin.db");
        var options = new SqliteCheckpointOptions();
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 20);
        using var reader = new SqliteConnection(Connection(path));
        reader.Open();
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        Assert.Equal(20L, Scalar(reader, "SELECT count(*) FROM t", snapshot));
        Insert(app, 20, start: 1000);

        SqliteCheckpointResult result = new SqliteWalCheckpointer(Target(path), options, new ListLogger(), TimeProvider.System).RunOnce();

        Assert.True(result.Ran);
        Assert.False(result.Truncate);
        Assert.False(result.Busy);
        Assert.True(result.CheckpointedFrames < result.LogFrames, $"the parked reader did not hold the copy back ({result.CheckpointedFrames} of {result.LogFrames})");
    }

    [Fact]
    public async Task AWriterCommittingInATightLoop_IsNeverHeldOffByTheCheckpointsRunningAlongsideIt()
    {
        string path = NewWalDb("writer.db");
        var options = new SqliteCheckpointOptions();
        SqliteConnection app = OpenApp(path, options);
        Insert(app, 1200);
        SqliteConnection writer = OpenApp(path, options);
        // The writer commits through SQLite directly with no busy wait, so a commit that meets a lock another connection holds returns
        // SQLITE_BUSY at once (Microsoft.Data.Sqlite would retry it until its command timeout). The contract is that a checkpoint never holds
        // a lock a writer must wait out (the TRUNCATE hold through the copy and fsync), not that a writer can never see SQLITE_BUSY: SQLite's
        // walIndexReadHdr takes WAL_WRITE_LOCK for the instant it takes a reader (every fresh checkpoint connection) to re-read a wal-index
        // header it caught mid-update, and a commit that starts in that instant returns SQLITE_BUSY (CI saw 1 and 2 of these). So a BUSY
        // commit is retried at once, as a busy handler would, and what is measured is how many separate lock episodes there were and how
        // long the longest one lasted. A wall-clock bound on whole commits measured the disk instead: a commit's fsync on a runner whose
        // disk other jobs are loading can take seconds with no lock involved at all.
        SQLitePCL.raw.sqlite3_busy_timeout(writer.Handle, 0);
        int lockEpisodes = 0;
        long longestEpisodeMs = 0;

        using var started = new ManualResetEventSlim();
        int stop = 0;
        Task writes = Task.Run(() =>
        {
            long loopStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // The writer never pauses and auto-checkpoint is off, so only the PASSIVE checkpoints under test can let SQLite restart the WAL from the top. It rewrites a bounded set of rows (REPLACE, not INSERT) so the database
            // and the log stay small even when the runner's disk is slow and the checkpoints are starved; the wall-clock deadline is a backstop that ends the loop if the checkpoint side never signals stop.
            for (int i = 0; (Volatile.Read(ref stop) == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(loopStart) < TimeSpan.FromSeconds(30)) || i < 5; i++)
            {
                string insert = $"INSERT OR REPLACE INTO t VALUES ({100_000 + (i % 256)}, zeroblob(3000))";
                int rc = SQLitePCL.raw.sqlite3_exec(writer.Handle, insert);
                if (rc is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
                {
                    lockEpisodes++;
                    long episodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    do
                    {
                        Thread.Yield();
                        rc = SQLitePCL.raw.sqlite3_exec(writer.Handle, insert);
                    }
                    while (rc is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED && System.Diagnostics.Stopwatch.GetElapsedTime(episodeStart) < LockEpisodeBound);
                    longestEpisodeMs = Math.Max(longestEpisodeMs, (long)System.Diagnostics.Stopwatch.GetElapsedTime(episodeStart).TotalMilliseconds);
                    if (rc is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED) return; // still held off at the bound: give up so the assertion below reports it instead of the test hanging
                }

                if (rc != SQLitePCL.raw.SQLITE_OK) throw new InvalidOperationException($"insert {i} failed: {SQLitePCL.raw.sqlite3_errmsg(writer.Handle).utf8_to_string()}");

                if (i == 3)
                {
                    started.Set();
                }
            }
        });

        // Bounded, and watching the writer: a writer that faulted or gave up before its fourth commit never sets `started`, and an
        // unbounded Wait would hang the run with no message. Awaiting the finished task rethrows its fault.
        if (!started.Wait(TimeSpan.FromSeconds(60)) || writes.IsCompleted)
        {
            Volatile.Write(ref stop, 1);
            await writes.WaitAsync(TimeSpan.FromSeconds(30)); // rethrows the writer's exception
            Assert.True(started.IsSet, "the writer ended before its fourth commit without failing");
        }
        var checkpointer = new SqliteWalCheckpointer(Target(path), options, new ListLogger(), TimeProvider.System);
        for (int run = 0; run < 30; run++)
        {
            SqliteCheckpointResult result = checkpointer.RunOnce();
            Assert.False(result.Truncate, "a TRUNCATE ran during operation: it holds the writer lock through the copy and the database fsync");
        }

        Volatile.Write(ref stop, 1);
        await writes;

        // PASSIVE takes no writer lock and waits on no lock, so a writer is only turned away by the instant a fresh checkpoint connection holds
        // WAL_WRITE_LOCK to re-read a torn header, which is over almost at once. A lock the checkpoint held through its copy and the database fsync
        // (the old TRUNCATE escalation: a 5078 ms commit, the busy timeout expiring, was seen once) outlasts the bound. The number of episodes
        // is not asserted: FULL/RESTART/TRUNCATE take the lock once per run, so a count cannot tell them from the torn-header race. That the
        // checkpoint is PASSIVE is pinned by PassiveCheckpoint_WithAReaderParkedOnAnOldSnapshot_ReportsNotBusy.
        Assert.True(longestEpisodeMs < LockEpisodeBound.TotalMilliseconds, $"the writer was held off for {longestEpisodeMs} ms by a lock while the checkpoints ran ({lockEpisodes} episodes)");
    }

    [Fact]
    public async Task EveryDatabaseRegistrationPassesTheCheckpointSettingsToItsConnections()
    {
        foreach ((bool enabled, long expected) in new[] { (true, 0L), (false, 1000L) })
        {
            var values = new Dictionary<string, string?> { ["Database:Provider"] = "Sqlite", ["Database:Sqlite:Checkpoint:Enabled"] = enabled.ToString() };
            foreach (string component in new[] { "Auth", "Characters", "World" })
            {
                values[$"Database:{component}:Provider"] = "Sqlite";
                values[$"Database:{component}:ConnectionString"] = Connection(NewWalDb($"di-{enabled}-{component}.db"));
            }

            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthDatabase(config).AddCharacterDatabase(config).AddWorldDatabase(config);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await using AuthDbContext auth = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using CharacterDbContext characters = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using WorldDbContext world = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorldDbContext>>().CreateDbContextAsync();
            foreach (DbContext db in new DbContext[] { auth, characters, world })
            {
                await db.Database.OpenConnectionAsync();
                Assert.Equal(expected, Scalar((SqliteConnection)db.Database.GetDbConnection(), "PRAGMA wal_autocheckpoint"));
            }
        }
    }

    [Fact]
    public async Task OnHostStop_TheFinalCheckpointRunsAfterAServiceRegisteredLaterHasDrainedItsWrites()
    {
        string path = NewWalDb("order-stop.db");
        using var drain = new DrainingService(path);
        using IHost host = new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = Connection(path),
            }))
            .ConfigureServices((context, services) =>
            {
                services.AddCharacterDatabase(context.Configuration); // registers the checkpointer first, like the daemon (AddWorldProductionServices) ...
                services.AddSingleton<IHostedService>(drain); // ... and the save-queue owner (WorldHost) after it, so the host stops that one first
            })
            .Build();
        await host.StartAsync();

        await host.StopAsync();

        // The drain's writes happened during the stop; the checkpointer's final TRUNCATE must have come after them (the drain still holds its connection, so the log was not closed away).
        Assert.Equal(50, drain.StopWrites);
        Assert.Equal(0L, WalBytes(path));
        Assert.Equal(50L, Scalar(drain.Connection, "SELECT count(*) FROM t"));
    }

    private sealed class DrainingService(string path) : IHostedService, IDisposable
    {
        public SqliteConnection Connection { get; } = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());

        public int StopWrites { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Connection.Open();
            Exec(Connection, "PRAGMA wal_autocheckpoint = 0");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Insert(Connection, 50);
            StopWrites = 50;
            return Task.CompletedTask;
        }

        public void Dispose() => Connection.Dispose();
    }

    private SqliteCheckpointService ServiceFor(string path, params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = Connection(path),
        };
        foreach ((string key, string? value) in extra)
        {
            values[key] = value;
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(config).AddCharacterDatabase(config).AddWorldDatabase(config);
        ServiceProvider provider = services.BuildServiceProvider();
        _open.Add(provider);
        return provider.GetServices<IHostedService>().OfType<SqliteCheckpointService>().Single();
    }

    private SqliteConnection OpenApp(string path, SqliteCheckpointOptions checkpoint)
    {
        var builder = new DbContextOptionsBuilder<DbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, Options(path), checkpoint: checkpoint);
        var db = new DbContext(builder.Options);
        _open.Add(db);
        db.Database.OpenConnection();
        return (SqliteConnection)db.Database.GetDbConnection();
    }

    private DatabaseConnectionOptions Options(string path)
        => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = Connection(path), SqliteSynchronous = SqliteSynchronousMode.Normal };

    private SqliteCheckpointTarget Target(string path)
        => SqliteCheckpointTarget.For(Options(path), new SqliteCheckpointOptions()) ?? throw new InvalidOperationException("no target");

    private static string Connection(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();

    /// <summary>Creates the database file in WAL mode (the mode is stored in the file) and returns its path.</summary>
    private string NewWalDb(string name)
    {
        string path = Path.Combine(_directory, name);
        using var raw = new SqliteConnection(Connection(path));
        raw.Open();
        Exec(raw, "PRAGMA journal_mode=WAL");
        return path;
    }

    private static long WalBytes(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;

    private static void Insert(SqliteConnection connection, int rows, int start = 0)
    {
        Exec(connection, "CREATE TABLE IF NOT EXISTS t(id INTEGER PRIMARY KEY, payload BLOB)");
        for (int i = 0; i < rows; i++)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO t VALUES ($id, zeroblob(3000))";
            command.Parameters.AddWithValue("$id", start + i);
            command.ExecuteNonQuery(); // autocommit: one WAL commit per row
        }
    }

    private static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? -1L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>
/// Runs <see cref="SqliteCheckpointTests"/> alone: it holds a writer's worst commit latency to an absolute bound, and other test classes hammering the same disk with SQLite
/// fsyncs at the same moment (a 3.4 s commit was seen with no TRUNCATE anywhere, during a wide <c>Sqlite</c> filter) would make that a measure of the box, not of the checkpointer.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCheckpointCollection
{
    public const string Name = "SqliteCheckpoint (serial: timing bound)";
}
