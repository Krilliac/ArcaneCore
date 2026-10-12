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
    /// SQLite only, characters database only: the longest (0..60000 ms, default 5000; rounded up to whole seconds) any command on a characters connection
    /// waits for another connection's write lock before it fails as "database is locked". It replaces the connection's Default Timeout (30 s) when smaller, set
    /// when the connection opens, so it also bounds EF's <c>BEGIN IMMEDIATE</c> and, through the busy-timeout clamp, SQLite's own wait. Without it a
    /// writer holding the file for a minute held each save queue's single consumer for the full 30 s per attempt. Failed saves are retained and retried
    /// by their queues. 0 keeps the connection string's Default Timeout. MariaDB, MySQL and PostgreSQL ignore it (docs/areas/persistence.md).
    /// </summary>
    public int SqliteWriteTimeoutMs { get; set; } = ArcaneCore.Data.Resilience.SqliteConnectionPragmaInterceptor.DefaultWriteTimeoutMs;

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
            return specific.SqliteBusyTimeoutMs is null
                ? new DatabaseConnectionOptions
                {
                    Provider = specific.Provider, ConnectionString = specific.ConnectionString, SqliteBusyTimeoutMs = SqliteBusyTimeoutMs,
                }
                : specific;
        }

        return new DatabaseConnectionOptions { Provider = Provider, ConnectionString = ConnectionString, SqliteBusyTimeoutMs = SqliteBusyTimeoutMs };
    }
}
