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
}

/// <summary>
/// Database configuration, bound from the "Database" section. Each component may have its own
/// sub-section (<c>Database:Auth</c>, <c>Database:Characters</c>, <c>Database:World</c>); a
/// component without one falls back to the section's own Provider/ConnectionString, which is
/// the single-database layout M1–M4 configurations use.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.MariaDb;

    public string ConnectionString { get; set; } = string.Empty;

    public DatabaseConnectionOptions? Auth { get; set; }

    public DatabaseConnectionOptions? Characters { get; set; }

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
            return specific;
        }

        return new DatabaseConnectionOptions { Provider = Provider, ConnectionString = ConnectionString };
    }
}
