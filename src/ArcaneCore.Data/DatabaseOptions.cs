namespace ArcaneCore.Data;

/// <summary>
/// Supported relational engines. MariaDB is primary; MySQL and PostgreSQL 16 are supported
/// (charter §2); SQLite serves zero-setup development and the end-to-end tests (ROADMAP).
/// </summary>
public enum DatabaseProvider
{
    MariaDb = 0,
    MySql = 1,
    PostgreSql = 2,
    Sqlite = 3,
}

/// <summary>
/// SQLite's <c>PRAGMA synchronous</c> levels a deployment may choose (<c>Database:SqliteSynchronous</c>). Full (SQLite's default, 2) fsyncs the WAL at every commit.
/// Normal (1) skips that fsync in WAL mode: the database stays consistent, but the most recent commits can be lost on power loss or an OS crash
/// (never on an application crash). Normal is applied only to a database that is in WAL mode; any other (rollback-journal) database stays on Full, with an error logged.
/// </summary>
public enum SqliteSynchronousMode
{
    Full = 0,
    Normal = 1,
}

/// <summary>The three logical databases (ROADMAP § Persistence).</summary>
public enum DatabaseComponent
{
    /// <summary>Accounts and the realm list, shared by every realm.</summary>
    Auth,

    /// <summary>Player characters and their state, one per realm.</summary>
    Characters,

    /// <summary>Static world content (imported, read-only at runtime).</summary>
    World,
}

/// <summary>Provider and connection string for one logical database.</summary>
public sealed class DatabaseConnectionOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.MariaDb;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// SQLite only: this component's busy timeout in milliseconds (0..60000), overriding <c>Database:SqliteBusyTimeoutMs</c>.
    /// Unset inherits it. Other engines ignore it.
    /// </summary>
    public int? SqliteBusyTimeoutMs { get; set; }

    /// <summary>
    /// SQLite only: this component's <c>PRAGMA synchronous</c> (Full or Normal), overriding <c>Database:SqliteSynchronous</c>. Unset inherits it.
    /// Normal avoids the fsync stall at every commit but can lose the most recent commits on power loss or an OS crash; it is applied only when the database is in WAL mode, otherwise the
    /// database stays on Full and an error is logged. Other engines ignore it.
    /// </summary>
    public SqliteSynchronousMode? SqliteSynchronous { get; set; }

    /// <summary>
    /// PostgreSQL only: the existing maintenance database the single-writer advisory lock is taken in (overrides <c>Database:WriterLockDatabase</c>; unset inherits it, default <c>postgres</c>).
    /// PostgreSQL scopes advisory locks to the database a session is connected to, so every world must take the lock in the same database, and it must exist before the characters database does.
    /// The login needs CONNECT on it. MariaDB, MySQL and SQLite ignore it.
    /// </summary>
    public string? WriterLockDatabase { get; set; }
}

/// <summary>
/// Database configuration, bound from the "Database" section. Each component may have its own
/// sub-section (<c>Database:Auth</c>, <c>Database:Characters</c>, <c>Database:World</c>); a
/// component without one falls back to the section's own Provider/ConnectionString, which is
/// the single-database layout M1–M4 configurations use.
/// </summary>
[Kernel.Configuration.ConfigDaemons(Kernel.Configuration.ConfigDaemon.Both)]
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.MariaDb;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// SQLite only: how long (0..60000 ms, default 5000) a connection waits inside SQLite for another connection's write lock before
    /// reporting "database is locked", set as <c>PRAGMA busy_timeout</c> on every connection (never more than the connection string's
    /// Default Timeout, and clamped to <c>Resilience:Database:QueryTimeoutMs</c> when the database guard is on, so a refused call never
    /// outlives its deadline in SQLite's busy handler). Without it a waiting writer retries only every 150 ms, so the many background writers on one SQLite file starve
    /// each other. 0 disables the pragma. MariaDB, MySQL and PostgreSQL ignore it (docs/areas/persistence.md).
    /// </summary>
    public int SqliteBusyTimeoutMs { get; set; } = ArcaneCore.Data.Resilience.SqliteConnectionPragmaInterceptor.DefaultBusyTimeoutMs;

    /// <summary>
    /// SQLite only: the <c>PRAGMA synchronous</c> set on every connection, Full (default) or Normal. Full fsyncs the WAL at every commit, so a committed save survives power loss.
    /// Normal skips that fsync, which removes multi-second commit stalls on a slow disk (and the "database is locked" failures they cause in other writers); the database
    /// stays consistent, but the most recent commits can be lost on power loss or an OS crash, never on an application crash. Normal is applied only to a database that is in WAL mode
    /// (the schema bootstrap switches file databases to WAL, best effort): a database that is not in WAL mode (the switch failed or was refused) stays on Full and an error is logged,
    /// because Normal outside WAL can corrupt the database on power loss. Each component may override it with a Database:Auth, Database:Characters or Database:World
    /// SqliteSynchronous key. MariaDB, MySQL and PostgreSQL ignore it (docs/areas/persistence.md).
    /// </summary>
    public SqliteSynchronousMode SqliteSynchronous { get; set; } = SqliteSynchronousMode.Full;

    /// <summary>
    /// SQLite only, characters database only: the longest (0..60000 ms, default 5000; rounded up to whole seconds) any command on a characters connection
    /// waits for another connection's write lock before it fails as "database is locked". It is written into the connection string's Default Timeout (30 s) when smaller, and
    /// replaces 0 (unlimited), so it bounds every command including EF's first one and its <c>BEGIN IMMEDIATE</c>, and through the busy-timeout clamp SQLite's own wait. Without it a
    /// writer holding the file for a minute held each save queue's single consumer for the full 30 s per attempt. Failed saves are retained and retried
    /// by their queues. 0 keeps the connection string's Default Timeout. MariaDB, MySQL and PostgreSQL ignore it (docs/areas/persistence.md).
    /// </summary>
    public int SqliteWriteTimeoutMs { get; set; } = ArcaneCore.Data.Resilience.SqliteConnectionPragmaInterceptor.DefaultWriteTimeoutMs;

    /// <summary>
    /// PostgreSQL only: the existing maintenance database (default <c>postgres</c>) in which the world takes its single-writer advisory lock. PostgreSQL scopes advisory locks to the database
    /// the session is connected to, so all worlds of one characters database must lock in the same database, one that exists before the characters database is created. The login needs
    /// CONNECT on it. Each component may override it with a Database:Characters WriterLockDatabase key. MariaDB, MySQL and SQLite ignore it (docs/guide/operations.md).
    /// </summary>
    public string WriterLockDatabase { get; set; } = ArcaneCore.Data.Characters.Ownership.CharacterDatabaseWriterLock.DefaultPostgresLockDatabase;

    /// <summary>
    /// How long (milliseconds, default 2000; 0 disables) a database transaction may stay open before it is logged at WARN with the DbContext and the
    /// code that closed it, on every provider. A transaction that holds the write lock this long is what makes other writers fail with
    /// "database is locked", so the next holder names itself in the log (docs/areas/persistence.md).
    /// </summary>
    public int SlowTransactionWarnMs { get; set; } = ArcaneCore.Data.Resilience.SlowTransactionInterceptor.DefaultWarnMs;

    /// <summary>
    /// How long (milliseconds, default 2000; 0 disables) a single database command may take to execute before it is logged at WARN with the DbContext, whether
    /// it ran inside a transaction or autocommitted, whether it was a write, the first 80 characters of its SQL (parameter values are never logged) and the code
    /// that issued it, on every provider. It sees what the transaction warning cannot: writes that autocommit and wait in "database is locked". Identical
    /// warnings are limited to one per 10 s (docs/areas/persistence.md).
    /// </summary>
    public int SlowCommandWarnMs { get; set; } = ArcaneCore.Data.Resilience.SlowCommandInterceptor.DefaultWarnMs;

    /// <summary>SQLite-only settings (<c>Database:Sqlite</c>): the background WAL checkpointer (<c>Database:Sqlite:Checkpoint</c>). Other engines ignore them.</summary>
    public ArcaneCore.Data.Resilience.SqliteDatabaseOptions Sqlite { get; set; } = new();

    public DatabaseConnectionOptions? Auth { get; set; }

    [Kernel.Configuration.ConfigDaemons(Kernel.Configuration.ConfigDaemon.World)]
    public DatabaseConnectionOptions? Characters { get; set; }

    [Kernel.Configuration.ConfigDaemons(Kernel.Configuration.ConfigDaemon.World)]
    public DatabaseConnectionOptions? World { get; set; }

    /// <summary>What start-up may do to the schema (<c>Database:Upgrade</c>); the historic create-and-upgrade by default.</summary>
    public ArcaneCore.Data.Schema.Upgrade.DatabaseUpgradeOptions Upgrade { get; set; } = new();

    public DatabaseConnectionOptions Resolve(DatabaseComponent component)
    {
        DatabaseConnectionOptions? specific = component switch
        {
            DatabaseComponent.Auth => Auth,
            DatabaseComponent.Characters => Characters,
            DatabaseComponent.World => World,
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };

        if (specific is not null && !string.IsNullOrWhiteSpace(specific.ConnectionString))
        {
            return specific.SqliteBusyTimeoutMs is not null && specific.SqliteSynchronous is not null
                ? specific
                : new DatabaseConnectionOptions
                {
                    Provider = specific.Provider, ConnectionString = specific.ConnectionString,
                    SqliteBusyTimeoutMs = specific.SqliteBusyTimeoutMs ?? SqliteBusyTimeoutMs,
                    SqliteSynchronous = specific.SqliteSynchronous ?? SqliteSynchronous,
                    WriterLockDatabase = specific.WriterLockDatabase ?? WriterLockDatabase,
                };
        }

        return new DatabaseConnectionOptions
        {
            Provider = Provider, ConnectionString = ConnectionString, SqliteBusyTimeoutMs = SqliteBusyTimeoutMs, SqliteSynchronous = SqliteSynchronous,
            WriterLockDatabase = WriterLockDatabase,
        };
    }
}
