using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Resilience;

/// <summary>Marks a database component as registered in this host, so <see cref="SqliteCheckpointService"/> knows which databases to look after.</summary>
public sealed record SqliteCheckpointComponent(DatabaseComponent Component);

/// <summary>One file-backed SQLite database the checkpointer looks after.</summary>
/// <param name="DatabasePath">The full path of the main database file (the WAL is <c>DatabasePath + "-wal"</c>).</param>
/// <param name="ConnectionString">The configured connection string, from which the service builds its own.</param>
internal sealed record SqliteCheckpointTarget(string DatabasePath, string ConnectionString)
{
    // The few connection strings of a process, parsed once: this runs on every DbContext build.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Paths = new();

    /// <summary>
    /// The target for a resolved component when the checkpointer should own it: SQLite, <paramref name="checkpoint"/> enabled and a plain file-backed read-write database
    /// (not in-memory, not read-only, not a <c>file:</c> URI). Null for every other provider or database, so MariaDB, MySQL and PostgreSQL never get a checkpointer.
    /// </summary>
    public static SqliteCheckpointTarget? For(DatabaseConnectionOptions options, SqliteCheckpointOptions checkpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!checkpoint.Enabled || options.Provider != DatabaseProvider.Sqlite || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return null;
        }

        string? path = Paths.GetOrAdd(options.ConnectionString, static connectionString =>
        {
            SqliteConnectionStringBuilder builder;
            try
            {
                builder = new SqliteConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                return null;
            }

            string source = builder.DataSource;
            return string.IsNullOrWhiteSpace(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly
                ? null
                : Path.GetFullPath(source);
        });
        return path is null ? null : new SqliteCheckpointTarget(path, options.ConnectionString);
    }
}

/// <summary>What one <see cref="SqliteWalCheckpointer.RunOnce"/> did.</summary>
/// <param name="Ran">False when there was nothing to do (no WAL file) or the attempt failed.</param>
/// <param name="Truncate">True when it ran <c>wal_checkpoint(TRUNCATE)</c> rather than PASSIVE (only the shutdown run does).</param>
/// <param name="Busy">The first result column: a TRUNCATE could not finish because a writer or reader was in the way.</param>
/// <param name="LogFrames">The second result column: frames in the WAL (-1 when the database is not in WAL mode).</param>
/// <param name="CheckpointedFrames">The third result column: frames copied into the database file.</param>
/// <param name="WalBytesBefore">The -wal file size before the checkpoint.</param>
/// <param name="WalBytesAfter">The -wal file size after the checkpoint.</param>
internal readonly record struct SqliteCheckpointResult(bool Ran, bool Truncate, bool Busy, long LogFrames, long CheckpointedFrames, long WalBytesBefore, long WalBytesAfter);

/// <summary>
/// Checkpoints one SQLite database's write-ahead log from its own short-lived connection (opened per run with pooling off, so no handle outlives a run and the file can
/// always be deleted or replaced between runs). <see cref="RunOnce"/> is the unit of work the service schedules and the tests drive directly.
/// <para>
/// During operation every run is <c>PASSIVE</c> only: it takes no writer lock and waits on no lock (its I/O, the WAL fsync, page writes and database fsync, still competes with commits for the same disk). There is deliberately no TRUNCATE during operation: TRUNCATE takes the exclusive
/// writer lock and holds it through the copy of the frames written since the last PASSIVE, the WAL fsync and the database fsync. A PASSIVE that reports a full backfill does not mean the database file was synced
/// (SQLite syncs it only when the frames copied reach the live end of the log, so a commit that lands during the copy skips the sync), and on Windows that later sync flushes every page earlier runs wrote,
/// all under the writer lock. The WAL file is trimmed by <c>journal_size_limit</c> instead, when the log restarts (continuous overlapping readers can postpone that), and a warning is logged when it stays above 4 x max(limit, 1 MiB) for 30 s while commits keep arriving or the copy is incomplete.
/// The final run at host stop (<c>final: true</c>, after the world has drained its saves) is a TRUNCATE: it waits at most <see cref="FinalBusyTimeoutMs"/> for readers and writers, and holds the writer lock through its copy and syncs.
/// A run that does not finish for <see cref="StallWarnAfter"/> logs one warning, then at most one per <see cref="StallWarnEvery"/>.
/// </para>
/// Thread affinity: one instance is used by one loop at a time (it is not thread-safe); never call it from the world thread. It does blocking SQLite I/O.
/// </summary>
internal sealed class SqliteWalCheckpointer
{
    /// <summary>The busy timeout of the shutdown TRUNCATE: how long it waits for readers and writers before it reports busy. It bounds the wait, not how long the writer lock is held once taken.</summary>
    public const int FinalBusyTimeoutMs = 250;

    /// <summary>How long checkpoints must keep failing to progress before the first warning.</summary>
    public static readonly TimeSpan StallWarnAfter = TimeSpan.FromSeconds(30);

    /// <summary>The WAL file size, as a multiple of the journal size limit, above which the growth warning counts.</summary>
    public const int WalGrowthFactor = 4;

    /// <summary>The smallest limit the growth threshold is computed from (a limit of 0 would make every WAL "large").</summary>
    public const long MinGrowthWarnBytes = 1024L * 1024;

    /// <summary>The least time between two stall warnings.</summary>
    public static readonly TimeSpan StallWarnEvery = TimeSpan.FromMinutes(1);

    private readonly SqliteCheckpointTarget _target;
    private readonly string _connectionString;
    private readonly string _walPath;
    private readonly SqliteCheckpointOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly string _latencyName;

    private long? _stalledSince;
    private long? _lastLogFrames;
    private long? _bigSince;
    private long? _lastGrowthWarn;
    private long? _lastWarn;
    private bool _warned;

    public SqliteWalCheckpointer(SqliteCheckpointTarget target, SqliteCheckpointOptions options, ILogger logger, TimeProvider clock)
    {
        _target = target;
        _options = options;
        _logger = logger;
        _clock = clock;
        _walPath = target.DatabasePath + "-wal";
        _latencyName = LatencyNames.CheckpointPrefix + Path.GetFileNameWithoutExtension(target.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder(target.ConnectionString)
        {
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary>The full path of the database this instance looks after.</summary>
    public string DatabasePath => _target.DatabasePath;

    /// <summary>
    /// Runs one checkpoint. Returns <c>Ran = false</c> when there is no WAL to checkpoint or the attempt failed (the failure counts towards the stall warning); never throws for a SQLite or I/O error.
    /// </summary>
    /// <param name="final">The shutdown checkpoint: a TRUNCATE with a <see cref="FinalBusyTimeoutMs"/> busy timeout. Otherwise PASSIVE.</param>
    public SqliteCheckpointResult RunOnce(bool final = false)
    {
        long before = WalBytes();
        string mode = final ? "TRUNCATE" : "PASSIVE";
        if (before == 0)
        {
            // No WAL file (or an empty one): nothing to copy. Opening a connection would only create and delete -wal/-shm.
            NoteHealthy();
            NoteWalSize(0, idle: false);
            _lastLogFrames = null;
            return new SqliteCheckpointResult(false, false, false, 0, 0, 0, 0);
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand setup = connection.CreateCommand();
            setup.CommandText = FormattableString.Invariant(
                $"PRAGMA busy_timeout = {(final ? FinalBusyTimeoutMs : 0)}; PRAGMA wal_autocheckpoint = 0; PRAGMA journal_size_limit = {_options.JournalSizeLimitBytes};");
            setup.ExecuteNonQuery();

            long started = Stopwatch.GetTimestamp();
            (long busy, long log, long checkpointed) = Checkpoint(connection, mode);
            LatencyRegistry.Shared.RecordSince(_latencyName, started);

            long after = WalBytes();
            bool complete = busy == 0 && (log < 0 || checkpointed >= log);
            bool idle = complete && _lastLogFrames == log; // fully copied and no commit since the previous run: nothing is being written, so there is no restart to wait for
            _lastLogFrames = log;
            NoteWalSize(after, idle);
            if (busy == 0 && (log < 0 || checkpointed >= log))
            {
                NoteHealthy();
            }
            else
            {
                NoteStalled($"busy={busy}, WAL frames={log}, checkpointed={checkpointed}, WAL file {after} bytes");
            }

            return new SqliteCheckpointResult(true, mode == "TRUNCATE", busy != 0, log, checkpointed, before, after);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FaultLedger.Shared.Record("data.sqlite-checkpoint", ex);
            AnomalyLedger.Shared.Report("database", "checkpoint-failed", _target.DatabasePath, ex.Message, AnomalySeverity.Warning);
            NoteStalled($"checkpoint failed: {ex.Message}");
            return new SqliteCheckpointResult(false, final, false, 0, 0, before, WalBytes());
        }
    }

    private static (long Busy, long Log, long Checkpointed) Checkpoint(SqliteConnection connection, string mode)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(" + mode + ");";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("PRAGMA wal_checkpoint returned no row.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private long WalBytes()
    {
        try
        {
            var info = new FileInfo(_walPath);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Warns (rate limited) when the -wal file stays above <see cref="WalGrowthFactor"/> times the journal size limit (at least <see cref="MinGrowthWarnBytes"/>) for <see cref="StallWarnAfter"/> while commits keep arriving or the copy is incomplete: the log is not restarting, so journal_size_limit is not trimming it. A run that found everything copied and the same frame count as the previous run (<paramref name="idle"/>, no writes) is quiet and resets the timer: a write-idle server has no restart to wait for, and the shutdown TRUNCATE empties the file.
    /// </summary>
    private void NoteWalSize(long walBytes, bool idle)
    {
        long threshold = WalGrowthFactor * Math.Max(_options.JournalSizeLimitBytes, MinGrowthWarnBytes);
        long now = _clock.GetTimestamp();
        if (walBytes <= threshold || idle)
        {
            _bigSince = null;
            _lastGrowthWarn = null;
            return;
        }

        _bigSince ??= now;
        TimeSpan big = _clock.GetElapsedTime(_bigSince.Value, now);
        if (big < StallWarnAfter || (_lastGrowthWarn is { } last && _clock.GetElapsedTime(last, now) < StallWarnEvery))
        {
            return;
        }

        _lastGrowthWarn = now;
        _logger.LogWarning(
            "SQLite WAL file for {Database} has been {Bytes} bytes (more than {Factor} times Database:Sqlite:Checkpoint:JournalSizeLimitBytes) for {Seconds} s; the log is not restarting while commits keep arriving, so journal_size_limit cannot trim it (an old reader snapshot, or continuous overlapping readers, keeps the log from restarting)",
            _target.DatabasePath, walBytes, WalGrowthFactor, big.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture));
    }

    private void NoteHealthy()
    {
        if (_warned)
        {
            _logger.LogInformation("SQLite WAL checkpointing for {Database} is making progress again", _target.DatabasePath);
        }

        _stalledSince = null;
        _lastWarn = null;
        _warned = false;
    }

    private void NoteStalled(string detail)
    {
        long now = _clock.GetTimestamp();
        _stalledSince ??= now;
        TimeSpan stalled = _clock.GetElapsedTime(_stalledSince.Value, now);
        if (stalled < StallWarnAfter || (_lastWarn is { } last && _clock.GetElapsedTime(last, now) < StallWarnEvery))
        {
            return;
        }

        _lastWarn = now;
        _warned = true;
        _logger.LogWarning(
            "SQLite WAL checkpoints for {Database} have not caught up for {Seconds} s ({Detail}); a long-running reader or writer is holding the log open, and the WAL file will keep growing until it ends",
            _target.DatabasePath, stalled.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture), detail);
    }
}

/// <summary>
/// The background WAL checkpointer (<c>Database:Sqlite:Checkpoint</c>): for every distinct file-backed SQLite database among the components this host registered, a loop that runs
/// <see cref="SqliteWalCheckpointer.RunOnce"/> every <see cref="SqliteCheckpointOptions.IntervalMs"/> on a thread-pool thread at normal priority (never the world thread), and a final
/// TRUNCATE checkpoint when the host stops. The connections EF opens have auto-checkpoint switched off (<see cref="SqliteConnectionPragmaInterceptor.WalJournalSizeLimit"/>),
/// so this is the only thing that checkpoints; with <c>Enabled=false</c> or a non-SQLite provider there are no targets and the service does nothing.
/// Thread-safety: the targets are immutable after construction; each loop owns its checkpointer.
/// </summary>
public sealed class SqliteCheckpointService : BackgroundService
{
    private readonly SqliteCheckpointOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    public SqliteCheckpointService(IOptions<DatabaseOptions> options, IEnumerable<SqliteCheckpointComponent> components, ILogger<SqliteCheckpointService>? logger = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(components);
        DatabaseOptions database = options.Value;
        _options = database.Sqlite.Checkpoint;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _clock = clock ?? TimeProvider.System;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var targets = new List<SqliteCheckpointTarget>();
        foreach (DatabaseComponent component in components.Select(c => c.Component).Distinct())
        {
            SqliteCheckpointTarget? target = SqliteCheckpointTarget.For(database.Resolve(component), _options);
            if (target is not null && seen.Add(target.DatabasePath))
            {
                targets.Add(target);
            }
        }

        if (targets.Count > 0)
        {
            _options.Validate(); // only a daemon that will use the settings can be stopped by a bad one
        }

        Targets = targets;
    }

    /// <summary>The databases this service checkpoints (empty when disabled or when no registered component is a file-backed SQLite database).</summary>
    internal IReadOnlyList<SqliteCheckpointTarget> Targets { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Targets.Count == 0)
        {
            return;
        }

        await Task.Yield(); // leave the caller's thread: StartAsync must not run SQLite I/O inline
        await Task.WhenAll(Targets.Select(target => LoopAsync(new SqliteWalCheckpointer(target, _options, _logger, _clock), stoppingToken))).ConfigureAwait(false);
    }

    private async Task LoopAsync(SqliteWalCheckpointer checkpointer, CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQLite WAL checkpointer started for {Database} (PASSIVE every {IntervalMs} ms, TRUNCATE at host stop)",
            checkpointer.DatabasePath, _options.IntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.IntervalMs), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Run(checkpointer, final: false);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping
        }

        Run(checkpointer, final: true);
    }

    private void Run(SqliteWalCheckpointer checkpointer, bool final)
    {
        try
        {
            checkpointer.RunOnce(final);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // RunOnce reports SQLite and I/O failures itself; anything else must not end the loop.
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("sqlite-checkpoint", ex);
            _logger.LogError(ex, "SQLite WAL checkpoint for {Database} failed unexpectedly", checkpointer.DatabasePath);
        }
    }
}
