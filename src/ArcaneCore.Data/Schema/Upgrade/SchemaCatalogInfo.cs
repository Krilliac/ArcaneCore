using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>A column as the database's catalog reports it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="IsNullable">Whether the column accepts NULL.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the primary key (known on SQLite and MariaDB/MySQL; PostgreSQL reports false and the key columns are NOT NULL anyway).</param>
public sealed record CatalogColumn(string Name, bool IsNullable, bool IsPrimaryKey);

/// <summary>
/// Database-level settings the drift check looks at. Null where the engine has no such setting
/// or it could not be read.
/// </summary>
/// <param name="DatabaseCharset">MariaDB/MySQL: the database's default character set (<c>utf8mb4</c> expected).</param>
/// <param name="DatabaseEncoding">PostgreSQL: the database's encoding (<c>UTF8</c> expected).</param>
/// <param name="TableEngines">MariaDB/MySQL: the storage engine of each base table (<c>InnoDB</c> expected).</param>
public sealed record DatabaseSettings(
    string? DatabaseCharset,
    string? DatabaseEncoding,
    IReadOnlyDictionary<string, string> TableEngines);

/// <summary>
/// The catalog reads only the upgrade tooling needs (column nullability, the table list, database
/// settings); raw ADO on the context's connection like <see cref="SchemaCatalog"/>.
/// </summary>
internal static class SchemaCatalogInfo
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string PomeloProvider = "Pomelo.EntityFrameworkCore.MySql";
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>The columns of a table with their nullability.</summary>
    public static async Task<IReadOnlyList<CatalogColumn>> ReadColumnInfoAsync(DbContext db, string table, CancellationToken ct = default)
    {
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider => "SELECT name, CASE WHEN \"notnull\" = 0 THEN 1 ELSE 0 END, CASE WHEN pk > 0 THEN 1 ELSE 0 END FROM pragma_table_info(@name)",
            PomeloProvider =>
                "SELECT column_name, CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END, CASE WHEN column_key = 'PRI' THEN 1 ELSE 0 END " +
                "FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @name",
            NpgsqlProvider =>
                "SELECT column_name, CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END, 0 " +
                "FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @name",
            _ => throw SchemaCatalog.Unsupported(db),
        };

        var columns = new List<CatalogColumn>();
        await foreach (object?[] row in SchemaCatalog.RowsAsync(db, sql, ct, ("@name", table)).ConfigureAwait(false))
        {
            columns.Add(new CatalogColumn(
                SchemaCatalog.Text(row[0]),
                Convert.ToInt64(row[1], CultureInfo.InvariantCulture) != 0,
                Convert.ToInt64(row[2], CultureInfo.InvariantCulture) != 0));
        }

        return columns;
    }

    /// <summary>Every base table of the database (SQLite: without its internal sqlite_ tables).</summary>
    public static async Task<IReadOnlyList<string>> ReadTablesAsync(DbContext db, CancellationToken ct = default)
    {
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider => "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            PomeloProvider => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'",
            NpgsqlProvider => "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'",
            _ => throw SchemaCatalog.Unsupported(db),
        };

        var tables = new List<string>();
        await foreach (object?[] row in SchemaCatalog.RowsAsync(db, sql, ct).ConfigureAwait(false))
        {
            tables.Add(SchemaCatalog.Text(row[0]));
        }

        return tables;
    }

    /// <summary>The database-level settings of the engines that have them.</summary>
    public static async Task<DatabaseSettings> ReadDatabaseSettingsAsync(DbContext db, CancellationToken ct = default)
    {
        var engines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? charset = null;
        string? encoding = null;
        switch (db.Database.ProviderName)
        {
            case PomeloProvider:
                await foreach (object?[] row in SchemaCatalog.RowsAsync(
                    db, "SELECT default_character_set_name FROM information_schema.schemata WHERE schema_name = DATABASE()", ct).ConfigureAwait(false))
                {
                    charset = SchemaCatalog.Text(row[0]);
                }

                await foreach (object?[] row in SchemaCatalog.RowsAsync(
                    db, "SELECT table_name, engine FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'", ct).ConfigureAwait(false))
                {
                    engines[SchemaCatalog.Text(row[0])] = SchemaCatalog.Text(row[1]);
                }

                break;
            case NpgsqlProvider:
                await foreach (object?[] row in SchemaCatalog.RowsAsync(
                    db, "SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database()", ct).ConfigureAwait(false))
                {
                    encoding = SchemaCatalog.Text(row[0]);
                }

                break;
            case SqliteProvider:
                break;
            default:
                throw SchemaCatalog.Unsupported(db);
        }

        return new DatabaseSettings(charset, encoding, engines);
    }
}
