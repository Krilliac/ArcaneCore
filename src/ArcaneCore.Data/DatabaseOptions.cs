namespace ArcaneCore.Data;

/// <summary>Supported relational engines (Charter §2: MariaDB primary, MySQL + Postgres also).</summary>
public enum DatabaseProvider
{
    MariaDb = 0,
    MySql = 1,
    PostgreSql = 2,
}

/// <summary>Database connection configuration, bound from the "Database" section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.MariaDb;

    public string ConnectionString { get; set; } = string.Empty;
}
