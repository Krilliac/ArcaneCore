namespace ArcaneCore.Data.Resilience;

/// <summary>
/// SQLite-specific settings, bound from <c>Database:Sqlite</c>. Other engines ignore the whole section.
/// </summary>
public sealed class SqliteDatabaseOptions
{
    /// <summary>Who checkpoints the write-ahead log of every file-backed SQLite database (<c>Database:Sqlite:Checkpoint</c>).</summary>
    public SqliteCheckpointOptions Checkpoint { get; set; } = new();

    /// <summary>The in-process single writer per database file (<c>Database:Sqlite:SingleWriter</c>, off by default): see <see cref="SqliteWriteGate"/>.</summary>
    public SqliteSingleWriterOptions SingleWriter { get; set; } = new();
}

/// <summary>
/// WAL checkpoint ownership for file-backed SQLite databases (<c>Database:Sqlite:Checkpoint</c>). By default SQLite checkpoints inside the commit that pushes the WAL past
/// 1000 pages (about 4 MB), and that commit also fsyncs the main database file, so a game save that happens to cross the threshold stalls for seconds on a slow disk
/// (even under <c>synchronous=NORMAL</c>, which only removes the WAL fsync). With this enabled, the connections EF opens set <c>wal_autocheckpoint=0</c> and a
/// background service (<see cref="SqliteCheckpointService"/>) checkpoints on its own connection and thread instead, so no commit runs a checkpoint inside its own transaction. The service's I/O (WAL fsync, page writes, database fsync) still competes for the same disk as the commits.
/// MariaDB, MySQL and PostgreSQL, and in-memory or read-only SQLite databases, are unaffected.
/// Plain data; validated by <see cref="Validate"/>.
/// </summary>
public sealed class SqliteCheckpointOptions
{
    /// <summary>The shipped <see cref="IntervalMs"/>.</summary>
    public const int DefaultIntervalMs = 2000;

    /// <summary>The shipped <see cref="JournalSizeLimitBytes"/> (32 MiB).</summary>
    public const long DefaultJournalSizeLimitBytes = 32L * 1024 * 1024;

    /// <summary>The smallest accepted <see cref="IntervalMs"/>.</summary>
    public const int MinIntervalMs = 100;

    /// <summary>The largest accepted <see cref="IntervalMs"/> (one hour).</summary>
    public const int MaxIntervalMs = 3_600_000;

    /// <summary>The largest accepted byte setting (1 TiB).</summary>
    public const long MaxBytes = 1024L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether the background checkpointer owns checkpointing (default true). False leaves SQLite's own auto-checkpoint (1000 pages, inside the committing connection) untouched.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often (milliseconds, 100..3600000, default 2000) the background service runs <c>PRAGMA wal_checkpoint(PASSIVE)</c> on each database. PASSIVE takes no writer lock and waits on no lock,
    /// but its I/O still competes for the same disk; a shorter interval keeps the WAL smaller at the cost of more wake-ups.
    /// </summary>
    public int IntervalMs { get; set; } = DefaultIntervalMs;

    /// <summary>
    /// <c>PRAGMA journal_size_limit</c> in bytes (0..1099511627776, default 33554432 = 32 MiB), set on every connection to the database: once a checkpoint has copied the whole WAL
    /// into the database and the next writer restarts it, SQLite truncates the file down to this size instead of leaving it at its high-water mark. This is the only thing that shrinks the -wal file while the server runs, and only when the log restarts (continuous overlapping readers can postpone that; a warning is logged when the file stays above 4 x max(this limit, 1 MiB) for 30 s while commits keep arriving or the copy is incomplete): the service never runs a TRUNCATE checkpoint during operation, because TRUNCATE takes the exclusive writer lock and holds it through the copy of the frames written since the last PASSIVE, the WAL fsync and the database fsync (which on Windows flushes every page earlier PASSIVE runs wrote), which would put the stall this feature removes back in front of a game save. Only the final checkpoint at host stop, after the world has drained its saves, is a TRUNCATE; it waits at most about 250 ms for readers and writers, and holds the writer lock through its copy and syncs. If the Auth database is one file shared by the world and realm daemons, either daemon's shutdown TRUNCATE can briefly stall the other's auth writes.
    /// </summary>
    public long JournalSizeLimitBytes { get; set; } = DefaultJournalSizeLimitBytes;

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the key when a value is outside its documented range.</summary>
    public void Validate()
    {
        if (IntervalMs is < MinIntervalMs or > MaxIntervalMs)
        {
            throw new InvalidOperationException($"Database:Sqlite:Checkpoint:IntervalMs must be between {MinIntervalMs} and {MaxIntervalMs}, not {IntervalMs}.");
        }

        if (JournalSizeLimitBytes is < 0 or > MaxBytes)
        {
            throw new InvalidOperationException($"Database:Sqlite:Checkpoint:JournalSizeLimitBytes must be between 0 and {MaxBytes}, not {JournalSizeLimitBytes}.");
        }
    }
}
