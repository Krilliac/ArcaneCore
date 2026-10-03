using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema;

/// <summary>A secondary (non-primary-key) index as the database's catalog reports it.</summary>
/// <param name="Name">The index name.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
/// <param name="Columns">The key columns in index order.</param>
public sealed record CatalogIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns);

/// <summary>
/// Reads what a database actually holds (tables, columns, indexes) from its own catalog, for
/// SQLite, MariaDB/MySQL (Pomelo) and PostgreSQL (Npgsql); any other provider throws
/// <see cref="NotSupportedException"/>.
/// <para>
/// These reads are raw ADO commands on the context's connection, joined to the context's
/// current transaction, so they see the effect of the statements issued before them and work
/// inside the bootstrapper's transaction. Being raw, they bypass EF command interceptors.
/// </para>
/// </summary>
public static class SchemaCatalog
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string PomeloProvider = "Pomelo.EntityFrameworkCore.MySql";
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Whether a table exists, asked of the engine's catalog (so a missing table is a plain
    /// "no", not a failed command in the logs).
    /// </summary>
    public static async Task<bool> TableExistsAsync(DbContext db, string table, CancellationToken cancellationToken = default)
    {
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            PomeloProvider =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name",
            NpgsqlProvider =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name",
            _ => throw Unsupported(db),
        };

        return await ScalarAsync(db, sql, cancellationToken, ("@name", table)).ConfigureAwait(false) > 0;
    }

    /// <summary>Whether a column exists, asked of the engine's catalog.</summary>
    public static async Task<bool> ColumnExistsAsync(DbContext db, string table, string column, CancellationToken cancellationToken = default)
    {
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider => "SELECT COUNT(*) FROM pragma_table_info(@name) WHERE name = @column",
            PomeloProvider =>
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @name AND column_name = @column",
            NpgsqlProvider =>
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @name AND column_name = @column",
            _ => throw Unsupported(db),
        };

        return await ScalarAsync(db, sql, cancellationToken, ("@name", table), ("@column", column)).ConfigureAwait(false) > 0;
    }

    /// <summary>The column names of a table, in no particular order.</summary>
    public static async Task<IReadOnlyList<string>> ReadColumnsAsync(DbContext db, string table, CancellationToken cancellationToken = default)
    {
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider => "SELECT name FROM pragma_table_info(@name)",
            PomeloProvider =>
                "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @name",
            NpgsqlProvider =>
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @name",
            _ => throw Unsupported(db),
        };

        var columns = new List<string>();
        await foreach (object?[] row in RowsAsync(db, sql, cancellationToken, ("@name", table)).ConfigureAwait(false))
        {
            columns.Add(Text(row[0]));
        }

        return columns;
    }

    /// <summary>
    /// The secondary indexes of a table: everything except the primary key and the indexes an
    /// engine creates for UNIQUE/PRIMARY KEY constraints in the table definition (EF's model
    /// declares indexes with CREATE INDEX, never as constraints).
    /// </summary>
    public static async Task<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(DbContext db, string table, CancellationToken cancellationToken = default)
    {
        // Every query returns one row per index column: (index name, unique 0/1, column), ordered
        // by index name and then by position inside the index.
        string sql = db.Database.ProviderName switch
        {
            SqliteProvider =>
                "SELECT il.name, il.\"unique\", ii.name FROM pragma_index_list(@name) AS il " +
                "JOIN pragma_index_info(il.name) AS ii WHERE il.origin = 'c' ORDER BY il.name, ii.seqno",
            PomeloProvider =>
                "SELECT index_name, CASE WHEN non_unique = 0 THEN 1 ELSE 0 END, column_name FROM information_schema.statistics " +
                "WHERE table_schema = DATABASE() AND table_name = @name AND index_name <> 'PRIMARY' ORDER BY index_name, seq_in_index",
            NpgsqlProvider =>
                "SELECT i.relname, CASE WHEN ix.indisunique THEN 1 ELSE 0 END, a.attname FROM pg_class t " +
                "JOIN pg_namespace ns ON ns.oid = t.relnamespace " +
                "JOIN pg_index ix ON ix.indrelid = t.oid " +
                "JOIN pg_class i ON i.oid = ix.indexrelid " +
                "CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " +
                "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " +
                "WHERE ns.nspname = current_schema() AND t.relname = @name AND NOT ix.indisprimary AND k.ord <= ix.indnkeyatts " +
                "ORDER BY i.relname, k.ord",
            _ => throw Unsupported(db),
        };

        var indexes = new List<CatalogIndex>();
        string? name = null;
        bool unique = false;
        var columns = new List<string>();
        await foreach (object?[] row in RowsAsync(db, sql, cancellationToken, ("@name", table)).ConfigureAwait(false))
        {
            string rowName = Text(row[0]);
            if (name is not null && !string.Equals(name, rowName, StringComparison.Ordinal))
            {
                indexes.Add(new CatalogIndex(name, unique, [.. columns]));
                columns.Clear();
            }

            name = rowName;
            unique = Convert.ToInt64(row[1], CultureInfo.InvariantCulture) != 0;
            columns.Add(Text(row[2]));
        }

        if (name is not null)
        {
            indexes.Add(new CatalogIndex(name, unique, [.. columns]));
        }

        return indexes;
    }

    /// <summary>Run a query that returns one number.</summary>
    internal static async Task<long> ScalarAsync(
        DbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using ConnectionLease lease = await ConnectionLease.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using DbCommand command = lease.CreateCommand(sql, parameters);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    internal static async IAsyncEnumerable<object?[]> RowsAsync(
        DbContext db, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using ConnectionLease lease = await ConnectionLease.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        await using DbCommand command = lease.CreateCommand(sql, parameters);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return values;
        }
    }

    internal static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    internal static NotSupportedException Unsupported(DbContext db)
        => new($"no catalog query for provider {db.Database.ProviderName}");

    /// <summary>Opens the context's connection if it is closed, and joins the context's current transaction.</summary>
    private sealed class ConnectionLease : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly DbTransaction? _transaction;
        private readonly bool _opened;

        private ConnectionLease(DbConnection connection, DbTransaction? transaction, bool opened)
        {
            _connection = connection;
            _transaction = transaction;
            _opened = opened;
        }

        public static async Task<ConnectionLease> OpenAsync(DbContext db, CancellationToken cancellationToken)
        {
            DbConnection connection = db.Database.GetDbConnection();
            bool opened = connection.State != ConnectionState.Open;
            if (opened)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ConnectionLease(connection, db.Database.CurrentTransaction?.GetDbTransaction(), opened);
        }

        public DbCommand CreateCommand(string sql, (string Name, object Value)[] parameters)
        {
            DbCommand command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            return command;
        }

        public async ValueTask DisposeAsync()
        {
            if (_opened)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
