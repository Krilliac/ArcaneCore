using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Copies of a prepared database, so a test that needs the same starting state many times prepares it once and copies it: PostgreSQL uses
/// <c>CREATE DATABASE ... TEMPLATE</c> (the source must have no session, so the copy clears its pool first), SQLite copies the file, and
/// MariaDB, which has no database copy, runs one batch of the source's own <c>SHOW CREATE TABLE</c> statements and <c>INSERT ... SELECT</c>s.
/// <see cref="DescribeAsync"/> lists everything a database holds, so a copy can be compared with what it copies.
/// </summary>
internal sealed partial class TestDatabases
{
    private const int CloneAttempts = 4;
    private const int CloneCommandTimeoutSeconds = 300;

    /// <summary>The PostgreSQL error for a template that still has a session attached.</summary>
    private const string PostgresObjectInUse = "55006";

    /// <summary>What <see cref="CloneAsync"/> needs of a source database, read once by <see cref="CaptureAsync"/>. The source must not change while it is cloned.</summary>
    /// <param name="Source">The database to copy.</param>
    /// <param name="MariaDbScript">The statements that fill a MariaDB copy, run from inside the copy; null on the other engines.</param>
    /// <param name="MariaDbCreateOptions">The character set and collation clause of a MariaDB copy's <c>CREATE DATABASE</c>; null on the other engines.</param>
    internal sealed record DatabaseImage(DatabaseConnectionOptions Source, string? MariaDbScript, string? MariaDbCreateOptions);

    /// <summary>Read what cloning <paramref name="source"/> needs. An object a MariaDB copy would silently lose (a view, trigger, routine or event) is refused.</summary>
    public static async Task<DatabaseImage> CaptureAsync(DatabaseConnectionOptions source)
    {
        if (source.Provider is not (DatabaseProvider.MariaDb or DatabaseProvider.MySql))
        {
            return new DatabaseImage(source, null, null);
        }

        var builder = new MySqlConnectionStringBuilder(source.ConnectionString) { Pooling = false };
        string name = builder.Database;
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        string[] notCopied = await ReadRowsAsync(connection, $"""
            SELECT CONCAT(table_type, ' ', table_name) FROM information_schema.tables WHERE table_schema = {Literal(name)} AND table_type <> 'BASE TABLE'
            UNION ALL SELECT CONCAT('TRIGGER ', trigger_name) FROM information_schema.triggers WHERE trigger_schema = {Literal(name)}
            UNION ALL SELECT CONCAT('ROUTINE ', routine_name) FROM information_schema.routines WHERE routine_schema = {Literal(name)}
            UNION ALL SELECT CONCAT('EVENT ', event_name) FROM information_schema.events WHERE event_schema = {Literal(name)}
            """);
        if (notCopied.Length > 0)
        {
            throw new NotSupportedException($"a MariaDB clone copies base tables only, and {name} also has: {string.Join(", ", notCopied)}");
        }

        string createOptions = (await ReadRowsAsync(
            connection, $"SELECT CONCAT('CHARACTER SET ', default_character_set_name, ' COLLATE ', default_collation_name) FROM information_schema.schemata WHERE schema_name = {Literal(name)}")).Single();
        string[] tables = await MariaDbTablesAsync(connection, name);

        var script = new StringBuilder("SET SESSION foreign_key_checks = 0;\nSET SESSION sql_mode = 'NO_AUTO_VALUE_ON_ZERO';\n");
        if (tables.Length > 0)
        {
            string creates = string.Join("; ", tables.Select(t => "SHOW CREATE TABLE " + Backtick(t)));
            await foreach (string[] row in ReadBatchAsync(connection, creates))
            {
                script.Append(row[1]).Append(";\n");
            }

            string filled = string.Join(" UNION ALL ", tables.Select(t => $"SELECT {Literal(t)} FROM {Backtick(t)} HAVING COUNT(*) > 0"));
            foreach (string table in await ReadRowsAsync(connection, filled))
            {
                script.Append(CultureInfo.InvariantCulture, $"INSERT INTO {Backtick(table)} SELECT * FROM {Backtick(name)}.{Backtick(table)};\n");
            }
        }

        return new DatabaseImage(source, script.ToString(), createOptions);
    }

    /// <summary>A new database of this instance in the state of <paramref name="image"/>'s source. It is released like any other.</summary>
    public async Task<DatabaseConnectionOptions> CloneAsync(DatabaseImage image)
    {
        DatabaseConnectionOptions clone = await CreateAsync(image.Source.Provider);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await CloneOnceAsync(image, clone);
                return clone;
            }
            catch (Exception ex) when (attempt < CloneAttempts && (ex is DbException || ex is TimeoutException))
            {
                // A MariaDB copy is not atomic: the next attempt starts from nothing.
                if (clone.Provider != DatabaseProvider.Sqlite)
                {
                    await DropOnceAsync(clone);
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
    }

    private static async Task CloneOnceAsync(DatabaseImage image, DatabaseConnectionOptions clone)
    {
        ClearPool(image.Source);
        switch (clone.Provider)
        {
            case DatabaseProvider.Sqlite:
            {
                string from = new SqliteConnectionStringBuilder(image.Source.ConnectionString).DataSource;
                string to = new SqliteConnectionStringBuilder(clone.ConnectionString).DataSource;
                File.Copy(from, to, overwrite: true);

                // A database in WAL mode normally folds the log in when its last handle closes; one that did not keeps committed pages there.
                if (File.Exists(from + "-wal"))
                {
                    File.Copy(from + "-wal", to + "-wal", overwrite: true);
                }

                break;
            }

            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(clone.ConnectionString) { Pooling = false };
                string name = builder.Database;
                builder.Database = string.Empty;
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandTimeout = CloneCommandTimeoutSeconds;
                command.CommandText = $"CREATE DATABASE {Backtick(name)} {image.MariaDbCreateOptions};\nUSE {Backtick(name)};\n{image.MariaDbScript}";
                await command.ExecuteNonQueryAsync();
                break;
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(clone.ConnectionString);
                string name = builder.Database!;
                string source = new NpgsqlConnectionStringBuilder(image.Source.ConnectionString).Database!;
                builder.Database = "postgres";
                builder.Pooling = false;
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandTimeout = CloneCommandTimeoutSeconds;
                command.CommandText = $"CREATE DATABASE {DoubleQuote(name)} TEMPLATE {DoubleQuote(source)}";
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresObjectInUse)
                {
                    // A session on the template that clearing the pool did not reach. Nothing may be using a template, so it is ended and the clone retried.
                    await using NpgsqlCommand terminate = connection.CreateCommand();
                    terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @n AND pid <> pg_backend_pid()";
                    terminate.Parameters.AddWithValue("n", source);
                    await terminate.ExecuteNonQueryAsync();
                    throw;
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(clone));
        }
    }

    /// <summary>
    /// Every table definition, index, constraint, sequence position and row of a database as sorted lines, so two databases in the same state give
    /// the same list. The database's own name appears nowhere in it. Reads over one connection of its own, closed at the end, and leaves no pooled session behind.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DescribeAsync(DatabaseConnectionOptions options)
    {
        var lines = new List<string>();
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
            {
                var builder = new SqliteConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                await using var connection = new SqliteConnection(builder.ConnectionString);
                await connection.OpenAsync();
                lines.AddRange(await ReadRowsAsync(connection, "SELECT 'user_version ' || user_version FROM pragma_user_version"));
                lines.AddRange(await ReadRowsAsync(connection, "SELECT type || ' ' || name || ' on ' || tbl_name || ': ' || COALESCE(sql, '') FROM sqlite_master"));
                string[] tables = await ReadRowsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table'");
                await AddRowsAsync(connection, lines, tables, DoubleQuote);
                break;
            }

            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = false, ConvertZeroDateTime = true };
                string name = builder.Database;
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                lines.AddRange(await ReadRowsAsync(connection, $"SELECT CONCAT(default_character_set_name, ' ', default_collation_name) FROM information_schema.schemata WHERE schema_name = {Literal(name)}"));
                lines.AddRange(await ReadRowsAsync(connection, $"SELECT CONCAT(table_type, ' ', table_name) FROM information_schema.tables WHERE table_schema = {Literal(name)}"));
                string[] tables = await MariaDbTablesAsync(connection, name);
                if (tables.Length > 0)
                {
                    await foreach (string[] row in ReadBatchAsync(connection, string.Join("; ", tables.Select(t => "SHOW CREATE TABLE " + Backtick(t)))))
                    {
                        lines.Add(row[1]);
                    }
                }

                await AddRowsAsync(connection, lines, tables, Backtick);
                break;
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                const string User = "NOT IN ('pg_catalog', 'information_schema', 'pg_toast')";
                lines.AddRange(await ReadRowsAsync(connection, "SELECT concat_ws(' ', pg_encoding_to_char(encoding), datcollate, datctype) FROM pg_database WHERE datname = current_database()"));
                lines.AddRange(await ReadRowsAsync(connection, $"""
                    SELECT concat_ws(' | ', table_schema, table_name, ordinal_position, column_name, data_type, udt_name, character_maximum_length, numeric_precision, numeric_scale, is_nullable, column_default, is_identity, identity_generation, is_generated, collation_name)
                    FROM information_schema.columns WHERE table_schema {User}
                    """));
                lines.AddRange(await ReadRowsAsync(connection, $"SELECT concat_ws(' | ', schemaname, tablename, indexname, indexdef) FROM pg_indexes WHERE schemaname {User}"));
                lines.AddRange(await ReadRowsAsync(connection, $"""
                    SELECT concat_ws(' | ', ns.nspname, c.conrelid::regclass::text, c.conname, c.contype::text, pg_get_constraintdef(c.oid))
                    FROM pg_constraint c JOIN pg_namespace ns ON ns.oid = c.connamespace WHERE ns.nspname {User}
                    """));
                lines.AddRange(await ReadRowsAsync(connection, $"SELECT concat_ws(' | ', schemaname, sequencename, last_value, increment_by) FROM pg_sequences WHERE schemaname {User}"));
                lines.AddRange(await ReadRowsAsync(connection, $"""
                    SELECT concat_ws(' | ', ns.nspname, c.relname, c.relkind::text) FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
                    WHERE ns.nspname {User} AND c.relkind NOT IN ('r', 'i', 'S', 't')
                    """));
                lines.AddRange(await ReadRowsAsync(connection, "SELECT 'trigger ' || tgname || ' on ' || tgrelid::regclass::text FROM pg_trigger WHERE NOT tgisinternal"));
                lines.AddRange(await ReadRowsAsync(connection, $"SELECT 'function ' || ns.nspname || '.' || p.proname FROM pg_proc p JOIN pg_namespace ns ON ns.oid = p.pronamespace WHERE ns.nspname {User}"));
                string[] tables = await ReadRowsAsync(connection, $"SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema {User}");
                await AddRowsAsync(connection, lines, tables, qualified => qualified);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static Task<string[]> MariaDbTablesAsync(DbConnection connection, string name)
        => ReadRowsAsync(connection, $"SELECT table_name FROM information_schema.tables WHERE table_schema = {Literal(name)} AND table_type = 'BASE TABLE' ORDER BY table_name");

    /// <summary>Every row of <paramref name="tables"/>, one line each, read in one batch: a database has hundreds of tables and a clone is compared for every upgrade step.</summary>
    private static async Task AddRowsAsync(DbConnection connection, List<string> lines, string[] tables, Func<string, string> quote)
    {
        if (tables.Length == 0)
        {
            return;
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = CloneCommandTimeoutSeconds;
        command.CommandText = string.Join("; ", tables.Select(t => "SELECT * FROM " + quote(t)));
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        int index = 0;
        do
        {
            while (await reader.ReadAsync())
            {
                lines.Add($"row {tables[index]}: {string.Join(" | ", Fields(reader))}");
            }

            index++;
        }
        while (await reader.NextResultAsync());

        if (index != tables.Length)
        {
            throw new InvalidOperationException($"read {index} result sets for {tables.Length} tables");
        }
    }

    private static async Task<string[]> ReadRowsAsync(DbConnection connection, string sql)
    {
        var rows = new List<string>();
        await foreach (string[] row in ReadBatchAsync(connection, sql))
        {
            rows.Add(row[0]);
        }

        return [.. rows];
    }

    /// <summary>Every row of every result set of <paramref name="sql"/>, as text.</summary>
    private static async IAsyncEnumerable<string[]> ReadBatchAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = CloneCommandTimeoutSeconds;
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                yield return Fields(reader);
            }
        }
        while (await reader.NextResultAsync());
    }

    private static string[] Fields(DbDataReader reader)
    {
        var fields = new string[reader.FieldCount];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = Field(reader.GetValue(i));
        }

        return fields;
    }

    private static string Field(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        string text => text,
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Field)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Backtick(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string DoubleQuote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
