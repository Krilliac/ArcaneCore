namespace ArcaneCore.Data.Resilience;

/// <summary>
/// The in-process single writer of every file-backed SQLite database (<c>Database:Sqlite:SingleWriter</c>): see <see cref="SqliteWriteGate"/>. Always on for file-backed SQLite; its only setting is <see cref="StatsIntervalMs"/> (the monitoring cadence). MariaDB, MySQL
/// and PostgreSQL ignore it. Plain data, read once when a host builds its database contexts; validated by <see cref="Validate"/>.
/// </summary>
public sealed class SqliteSingleWriterOptions
{
    /// <summary>The shipped <see cref="MaxHoldMs"/>.</summary>
    public const int DefaultMaxHoldMs = 60_000;

    /// <summary>The shipped <see cref="WarnWaitMs"/>.</summary>
    public const int DefaultWarnWaitMs = 1000;

    /// <summary>The shipped <see cref="StatsIntervalMs"/>.</summary>
    public const int DefaultStatsIntervalMs = 300_000;

    /// <summary>The largest accepted millisecond setting (one hour).</summary>
    public const int MaxMs = 3_600_000;

    /// <summary>
    /// Whether the gate is attached (always, outside tests). Not a setting: the gate is a fix and ships on (2026-10-17). With it, the dozen background write queues of the
    /// characters database are handed SQLite's single write lock in arrival order instead of polling for it from SQLite's busy handler, which removes the "database is locked"
    /// failures that came from unfair polling. An async waiter gives up when the gate has handed off to no one for its connection's write bound, or after the hard cap (four times the bound, at most 30 s unless the bound is larger) of
    /// active waiting; a blocking waiter keeps the single bound as its total. A caller's cancellation token or budget supersedes both. It then fails with SQLite's own busy error.
    /// </summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>
    /// The bound (milliseconds, 0..3600000) of a writer's wait for the gate: how long the gate may hand off to no one before the writer fails as busy, and for a blocking writer its total wait
    /// (a test seam, not a setting). An async writer in a moving queue may wait up to the hard cap (four times the bound, at most 30 s unless the bound is larger) of active time. 0 (default)
    /// uses the connection's own lock-wait bound (<c>Database:SqliteWriteTimeoutMs</c> on the characters database, the connection string's Default Timeout elsewhere, 30 s without one).
    /// </summary>
    internal int WaitMs { get; set; }

    /// <summary>
    /// A hold older than this (milliseconds, 0..3600000, default 60000; 0 never) is released by the next waiter and logged at ERROR: a transaction that was never ended (leaked) must not
    /// stop every writer. Releasing it only lets the next writer try SQLite's lock itself, as it would without the gate. A test seam, not a setting.
    /// </summary>
    internal int MaxHoldMs { get; set; } = DefaultMaxHoldMs;

    /// <summary>A writer that waited longer than this (milliseconds, 0..3600000, default 1000; 0 never) logs one WARN naming the holder it waited behind. A test seam, not a setting.</summary>
    internal int WarnWaitMs { get; set; } = DefaultWarnWaitMs;

    /// <summary>How often (milliseconds, 0..3600000, default 300000; 0 never) each gate logs one INFO line with its writes, contended writes, waits, longest hold and timeouts for the interval.</summary>
    public int StatsIntervalMs { get; set; } = DefaultStatsIntervalMs;

    /// <summary>Throws when a setting is outside its range.</summary>
    public void Validate()
    {
        Check(WaitMs, "WaitMs");
        Check(MaxHoldMs, "MaxHoldMs");
        Check(WarnWaitMs, "WarnWaitMs");
        Check(StatsIntervalMs, "StatsIntervalMs");

        static void Check(int value, string name)
        {
            if (value is < 0 or > MaxMs)
                throw new InvalidOperationException($"Database:Sqlite:SingleWriter:{name} must be 0..{MaxMs} milliseconds (was {value})");
        }
    }
}
