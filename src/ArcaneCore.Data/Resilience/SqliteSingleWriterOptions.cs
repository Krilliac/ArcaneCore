namespace ArcaneCore.Data.Resilience;

/// <summary>
/// The in-process single writer of every file-backed SQLite database (<c>Database:Sqlite:SingleWriter</c>): see <see cref="SqliteWriteGate"/>. On by default; MariaDB, MySQL
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
    /// Whether write transactions and autocommit write statements that EF runs against a file-backed SQLite database queue on one in-process gate per file (default true; false only to roll back).
    /// With it on, the dozen background write queues of the characters database are handed SQLite's single write lock in arrival order instead of polling for it from SQLite's
    /// busy handler, which removes the "database is locked" failures that came from unfair polling. A waiter is bounded by its connection's write bound and then fails with SQLite's own busy error.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long (milliseconds, 0..3600000) a writer waits for the gate before it fails as busy. 0 (default) uses the connection's own lock-wait bound
    /// (<c>Database:SqliteWriteTimeoutMs</c> on the characters database, the connection string's Default Timeout elsewhere), so the wait a caller sees is the one it had before.
    /// </summary>
    public int WaitMs { get; set; }

    /// <summary>
    /// A hold older than this (milliseconds, 0..3600000, default 60000; 0 never) is released by the next waiter and logged at ERROR: a transaction that was never ended (leaked) must not
    /// stop every writer. Releasing it only lets the next writer try SQLite's lock itself, as it would without the gate.
    /// </summary>
    public int MaxHoldMs { get; set; } = DefaultMaxHoldMs;

    /// <summary>A writer that waited longer than this (milliseconds, 0..3600000, default 1000; 0 never) logs one WARN naming the holder it waited behind.</summary>
    public int WarnWaitMs { get; set; } = DefaultWarnWaitMs;

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
