using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Sets SQLite's <c>busy_timeout</c> and <c>synchronous</c> on every connection EF opens (SQLite only; <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>
/// adds it to the Sqlite case alone). Without it SQLite reports "busy" at once and Microsoft.Data.Sqlite's own retry loop sleeps a fixed
/// 150 ms per attempt, so with a dozen background writers on one file a writer is admitted up to 150 ms after the lock frees and the
/// writers starve each other. With it SQLite's own busy handler waits inside the call and retries within a few milliseconds.
/// The outer loop still bounds the whole wait by the connection's <c>Default Timeout</c>, and the busy timeout never exceeds that
/// timeout, so <see cref="DatabaseGuard"/>'s pessimistic-timeout contract is unchanged.
/// Stateless apart from the immutable timeout; thread-safe.
/// </summary>
public sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>The shipped timeout (<c>Database:SqliteBusyTimeoutMs</c>).</summary>
    public const int DefaultBusyTimeoutMs = 5000;

    /// <summary>The shipped bound on a characters-database command's lock wait (<c>Database:SqliteWriteTimeoutMs</c>).</summary>
    public const int DefaultWriteTimeoutMs = 5000;

    /// <summary>The largest accepted <c>Database:SqliteBusyTimeoutMs</c>.</summary>
    public const int MaxBusyTimeoutMs = 60000;

    private sealed record AppliedPragmaState(int BusyTimeoutMs, long WalJournalSizeLimit, SqliteSynchronousMode Synchronous, bool FileBacked);

    private static readonly ConcurrentDictionary<(int Busy, int Write, SqliteSynchronousMode Synchronous, long WalLimit, ILogger? Logger), SqliteConnectionPragmaInterceptor> Instances = new();
    private static readonly ConditionalWeakTable<sqlite3, AppliedPragmaState> InitializedHandles = new();

    private readonly ILogger? _logger;
    private readonly string _defaultFilePragmaText;
    private readonly string _defaultMemoryPragmaText;

    /// <summary>Files whose configured NORMAL was refused because they are not in WAL mode (value: the journal mode seen); cleared when the file is seen in WAL again.</summary>
    private readonly ConcurrentDictionary<string, string> _fellBack = new(StringComparer.OrdinalIgnoreCase);

    private SqliteConnectionPragmaInterceptor(int busyTimeoutMs, int writeTimeoutMs, SqliteSynchronousMode synchronous, long walJournalSizeLimit, ILogger? logger)
    {
        _logger = logger;
        WalJournalSizeLimit = walJournalSizeLimit;
        Synchronous = synchronous;
        BusyTimeoutMs = busyTimeoutMs;
        WriteTimeoutMs = writeTimeoutMs;
        _defaultMemoryPragmaText = FormattableString.Invariant($"PRAGMA busy_timeout = {busyTimeoutMs};");
        _defaultFilePragmaText = walJournalSizeLimit >= 0
            ? FormattableString.Invariant($"PRAGMA busy_timeout = {busyTimeoutMs}; PRAGMA wal_autocheckpoint = 0; PRAGMA journal_size_limit = {walJournalSizeLimit};")
            : _defaultMemoryPragmaText;
    }

    /// <summary>
    /// The configured <c>Database:SqliteSynchronous</c>. Normal is only applied to a database that is in WAL mode (in a rollback-journal mode NORMAL can corrupt the
    /// file on power loss); any other database gets FULL and an error log line. An empty database (page_count 0) is put into WAL first (<see cref="WalIfEmpty"/>). See <see cref="EffectiveFor"/>.
    /// </summary>
    public SqliteSynchronousMode Synchronous { get; }

    /// <summary>The mode actually in force for the database at <paramref name="dataSource"/>: Full when the configured Normal was refused because the file is not in WAL mode.</summary>
    public SqliteSynchronousMode EffectiveFor(string? dataSource)
        => Synchronous == SqliteSynchronousMode.Normal && !string.IsNullOrEmpty(dataSource) && _fellBack.ContainsKey(dataSource)
            ? SqliteSynchronousMode.Full
            : Synchronous;

    /// <summary>
    /// Negative (default): SQLite's own auto-checkpoint stays on. Zero or more: <see cref="SqliteCheckpointService"/> owns checkpointing, so every file-backed connection this interceptor
    /// opens gets <c>PRAGMA wal_autocheckpoint = 0</c> (a commit never runs a checkpoint, which fsyncs the main database file inside the committing transaction) and
    /// <c>PRAGMA journal_size_limit</c> of this many bytes (the writer that restarts the WAL after a full checkpoint is an application connection, so the limit must be set here, not only on the service's connection).
    /// </summary>
    public long WalJournalSizeLimit { get; }

    /// <summary>The configured busy timeout in milliseconds; 0 leaves SQLite's own "fail at once" behaviour.</summary>
    public int BusyTimeoutMs { get; }

    /// <summary>
    /// The bound in milliseconds on any command's lock wait on the connections this interceptor opens (0: none, the connection string's Default Timeout stands).
    /// Applied by lowering the connection's Default Timeout (whole seconds, rounded up; 0, unlimited, is replaced too) at open, before <c>busy_timeout</c> is derived from it, so EF's
    /// <c>BEGIN IMMEDIATE</c>, which the driver creates with that timeout, is bounded too. This is only the backstop: a command created BEFORE the open
    /// (EF's ExecuteUpdate/Delete, ExecuteScalar and raw SQL) has already copied the old Default Timeout, so <see cref="DataServiceCollectionExtensions.ConfigureProvider"/>
    /// also lowers it in the connection string. Used for the characters database, whose write queues must not sit in a
    /// 30 s lock wait.
    /// </summary>
    public int WriteTimeoutMs { get; }

    /// <summary>
    /// The shared interceptor for a timeout. One instance per value keeps EF's internal service provider cache keyed on the same
    /// interceptor object across the many option builds a host makes.
    /// </summary>
    public static SqliteConnectionPragmaInterceptor For(int busyTimeoutMs, int writeTimeoutMs = 0, SqliteSynchronousMode synchronous = SqliteSynchronousMode.Full, ILogger? logger = null, long walJournalSizeLimit = -1)
    {
        if (walJournalSizeLimit > SqliteCheckpointOptions.MaxBytes)
        {
            throw new InvalidOperationException(
                $"Database:Sqlite:Checkpoint:JournalSizeLimitBytes must be between 0 and {SqliteCheckpointOptions.MaxBytes}, not {walJournalSizeLimit}.");
        }

        if (busyTimeoutMs is < 0 or > MaxBusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Database:SqliteBusyTimeoutMs must be between 0 and {MaxBusyTimeoutMs}, not {busyTimeoutMs}.");
        }

        if (writeTimeoutMs is < 0 or > MaxBusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Database:SqliteWriteTimeoutMs must be between 0 and {MaxBusyTimeoutMs}, not {writeTimeoutMs}.");
        }

        if (!Enum.IsDefined(synchronous))
        {
            throw new InvalidOperationException($"Database:SqliteSynchronous must be Full or Normal, not {synchronous}.");
        }

        return Instances.GetOrAdd((busyTimeoutMs, writeTimeoutMs, synchronous, Math.Max(walJournalSizeLimit, -1), logger), static key => new SqliteConnectionPragmaInterceptor(key.Busy, key.Write, key.Synchronous, key.WalLimit, key.Logger));
    }

    /// <summary>
    /// The one place the write bound becomes a <c>Default Timeout</c>: <paramref name="writeTimeoutMs"/> in whole seconds, rounded up, replaces
    /// <paramref name="currentSeconds"/> when that is longer or 0 (0 means unlimited in Microsoft.Data.Sqlite, so a bound must override it);
    /// a shorter current value is kept, and a <paramref name="writeTimeoutMs"/> of 0 or less changes nothing.
    /// </summary>
    public static int ApplyWriteBound(int currentSeconds, int writeTimeoutMs)
    {
        if (writeTimeoutMs <= 0)
        {
            return currentSeconds;
        }

        int bound = (int)((writeTimeoutMs + 999L) / 1000); // fits an int for any int input
        return currentSeconds == 0 || bound < currentSeconds ? bound : currentSeconds;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            if (TrySkipInitializedHandle(sqlite, out int milliseconds, out bool fileBacked, out long effectiveWalLimit, out sqlite3? handle))
            {
                return;
            }

            using SqliteCommand command = CreatePragma(sqlite, milliseconds, fileBacked);
            command.ExecuteNonQuery();
            bool cacheHandle;
            if (Synchronous == SqliteSynchronousMode.Normal && fileBacked)
            {
                using SqliteCommand mode = sqlite.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode;";
                cacheHandle = ApplySynchronous(sqlite, WalIfEmpty(sqlite, mode.ExecuteScalar() as string));
            }
            else
            {
                cacheHandle = ApplySynchronous(sqlite, "wal");
            }

            RecordHandleState(handle, cacheHandle, milliseconds, effectiveWalLimit, fileBacked);
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            if (TrySkipInitializedHandle(sqlite, out int milliseconds, out bool fileBacked, out long effectiveWalLimit, out sqlite3? handle))
            {
                return;
            }

            await using SqliteCommand command = CreatePragma(sqlite, milliseconds, fileBacked);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            bool cacheHandle;
            if (Synchronous == SqliteSynchronousMode.Normal && fileBacked)
            {
                await using SqliteCommand mode = sqlite.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode;";
                string? journalMode = await mode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                cacheHandle = ApplySynchronous(sqlite, await WalIfEmptyAsync(sqlite, journalMode, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                cacheHandle = ApplySynchronous(sqlite, "wal");
            }

            RecordHandleState(handle, cacheHandle, milliseconds, effectiveWalLimit, fileBacked);
        }
    }

    private bool TrySkipInitializedHandle(SqliteConnection sqlite, out int milliseconds, out bool fileBacked, out long effectiveWalLimit, out sqlite3? handle)
    {
        sqlite.DefaultTimeout = ApplyWriteBound(sqlite.DefaultTimeout, WriteTimeoutMs);

        // Never wait inside SQLite longer than the connection's own timeout: that loop checks the clock only between attempts.
        milliseconds = (int)Math.Min(BusyTimeoutMs, sqlite.DefaultTimeout * 1000L);
        fileBacked = IsFileBacked(sqlite);
        effectiveWalLimit = fileBacked && WalJournalSizeLimit >= 0 ? WalJournalSizeLimit : -1;
        handle = sqlite.Handle;
        if (handle is not null && !handle.IsInvalid && !handle.IsClosed
            && InitializedHandles.TryGetValue(handle, out AppliedPragmaState? applied)
            && applied.BusyTimeoutMs == milliseconds
            && applied.WalJournalSizeLimit == effectiveWalLimit
            && applied.Synchronous == Synchronous
            && applied.FileBacked == fileBacked)
        {
            if (Synchronous == SqliteSynchronousMode.Normal)
            {
                _fellBack.TryRemove(sqlite.DataSource ?? string.Empty, out _);
            }

            return true;
        }

        return false;
    }

    private void RecordHandleState(sqlite3? handle, bool cacheHandle, int milliseconds, long effectiveWalLimit, bool fileBacked)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return;
        }

        if (cacheHandle)
        {
            InitializedHandles.AddOrUpdate(handle, new AppliedPragmaState(milliseconds, effectiveWalLimit, Synchronous, fileBacked));
        }
        else
        {
            InitializedHandles.Remove(handle);
        }
    }

    /// <summary>
    /// The journal mode NORMAL is judged against. A database that is not in WAL but is EMPTY (<c>page_count</c> 0) is put into WAL first and the resulting mode returned.
    /// <para>
    /// Why: on a new file the first connection is EF's own create (<c>SqliteDatabaseCreator.Create</c>), which opens the connection, which runs this interceptor,
    /// and only THEN runs <c>PRAGMA journal_mode = 'wal'</c>; the schema bootstrap repeats the switch, best effort, after it has accepted the schema. So this open
    /// used to see SQLite's default rollback journal and log the configured NORMAL as refused at ERROR ("FAIL" on the console), although the file was in WAL
    /// moments later and later connections got NORMAL. This change removes that false ERROR on the first open of a new file; startup completed either way.
    /// </para>
    /// <para>
    /// Scope: an empty database has no schema or rows, so nothing is lost, but its file IS written (the WAL header, plus <c>-wal</c>/<c>-shm</c>). That includes a
    /// zero-byte file that already existed (touched, or left by a crashed create), and it happens on any open, a validate-only bootstrap included, whenever NORMAL is
    /// configured. A populated non-WAL file is only read here and stays fail-closed on FULL. Runs only when NORMAL is configured and the file is not already in WAL
    /// (the mode is persistent), so the common open pays nothing extra. If the switch is refused (another connection holds the file) the mode is unchanged.
    /// </para>
    /// </summary>
    private static string? WalIfEmpty(SqliteConnection connection, string? journalMode)
    {
        if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            return journalMode;
        }

        try
        {
            using SqliteCommand pages = connection.CreateCommand();
            pages.CommandText = "PRAGMA page_count;";
            if (pages.ExecuteScalar() is not long count || count != 0)
            {
                return CurrentMode(connection) ?? journalMode;
            }

            using SqliteCommand wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            return wal.ExecuteScalar() as string ?? journalMode;
        }
        catch (SqliteException ex)
        {
            FaultLedger.Shared.Record("data.sqlite-pragma", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", connection.DataSource ?? string.Empty, ex.Message, AnomalySeverity.Warning);
            return journalMode; // fail closed: ApplySynchronous keeps FULL and reports the mode it was given
        }
    }

    /// <summary>
    /// The journal mode as it is now. Needed when <c>page_count</c> says the file is no longer empty: a concurrent opener may have switched it to WAL (which writes page 1) between
    /// this connection's earlier <c>journal_mode</c> read and its <c>page_count</c> read, so the earlier answer is stale and judging by it would leave a WAL file on FULL.
    /// </summary>
    private static string? CurrentMode(SqliteConnection connection)
    {
        using SqliteCommand mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        return mode.ExecuteScalar() as string;
    }

    private static async Task<string?> CurrentModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        return await mode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>The async form of <see cref="WalIfEmpty"/>, same rules.</summary>
    private static async Task<string?> WalIfEmptyAsync(SqliteConnection connection, string? journalMode, CancellationToken cancellationToken)
    {
        if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            return journalMode;
        }

        try
        {
            await using SqliteCommand pages = connection.CreateCommand();
            pages.CommandText = "PRAGMA page_count;";
            if (await pages.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long count || count != 0)
            {
                return await CurrentModeAsync(connection, cancellationToken).ConfigureAwait(false) ?? journalMode;
            }

            await using SqliteCommand wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            return await wal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? journalMode;
        }
        catch (SqliteException ex)
        {
            FaultLedger.Shared.Record("data.sqlite-pragma", ex);
            AnomalyLedger.Shared.Report("database", "pragma-failed", connection.DataSource ?? string.Empty, ex.Message, AnomalySeverity.Warning);
            return journalMode; // fail closed: ApplySynchronous keeps FULL and reports the mode it was given
        }
    }

    private static readonly ConcurrentDictionary<string, bool> FileBacked = new();

    /// <summary>Whether the connection is a plain read-write file database; cached per connection string, because it runs on every open.</summary>
    private static bool IsFileBacked(SqliteConnection connection)
        => FileBacked.GetOrAdd(connection.ConnectionString, static connectionString =>
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            return !string.IsNullOrEmpty(builder.DataSource) && builder.DataSource != ":memory:" && builder.Mode is not (SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly);
        });

    /// <summary>
    /// Sets <c>PRAGMA synchronous</c> for a just-opened connection. Fails closed: Normal is applied only when <paramref name="journalMode"/> is "wal" (SQLite documents that NORMAL
    /// outside WAL can corrupt the database on power loss), otherwise the connection stays on FULL and the refusal is logged at ERROR once per file (until the file is seen in WAL again).
    /// </summary>
    private bool ApplySynchronous(SqliteConnection connection, string? journalMode)
    {
        bool wal = string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase);
        string dataSource = connection.DataSource ?? string.Empty;
        if (Synchronous == SqliteSynchronousMode.Normal && !wal)
        {
            if (_fellBack.TryAdd(dataSource, journalMode ?? "(unknown)"))
            {
                _logger?.LogError(
                    "SQLite database {DataSource} is in journal mode '{JournalMode}', not WAL: Database:SqliteSynchronous=NORMAL is refused for it (NORMAL outside WAL can corrupt the database on power loss) and it stays on FULL",
                    dataSource, journalMode ?? "(unknown)");
            }
        }
        else if (Synchronous == SqliteSynchronousMode.Normal)
        {
            _fellBack.TryRemove(dataSource, out _);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Synchronous == SqliteSynchronousMode.Normal && wal ? "PRAGMA synchronous = NORMAL;" : "PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();
        return Synchronous != SqliteSynchronousMode.Normal || wal;
    }

    private SqliteCommand CreatePragma(SqliteConnection connection, int milliseconds, bool fileBacked)
    {
        SqliteCommand command = connection.CreateCommand();
        // When SqliteCheckpointService owns checkpointing, a commit on this connection must not run one (wal_autocheckpoint is per connection and not persistent), and the writer that restarts the WAL
        // after a full checkpoint is an application connection, so journal_size_limit is set here too. Folded into this command so an open costs no extra round trip.
        if (milliseconds == BusyTimeoutMs)
        {
            command.CommandText = fileBacked ? _defaultFilePragmaText : _defaultMemoryPragmaText;
        }
        else
        {
            command.CommandText = WalJournalSizeLimit >= 0 && fileBacked
                ? FormattableString.Invariant($"PRAGMA busy_timeout = {milliseconds}; PRAGMA wal_autocheckpoint = 0; PRAGMA journal_size_limit = {WalJournalSizeLimit};")
                : FormattableString.Invariant($"PRAGMA busy_timeout = {milliseconds};");
        }

        return command;
    }
}
