using System.Data.Common;
using System.Globalization;
using System.Text;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>Where the committed update files are, how to read them and how to run them the way the operator's client does.</summary>
internal static class SqlUpdateSupport
{
    public const string UpdateVariable = "ARCANECORE_UPDATE_SQL";

    public const string RegenerateCommand =
        "ARCANECORE_UPDATE_SQL=1 dotnet test tests/ArcaneCore.Data.Tests --filter SqlUpdateGoldenTests (or: arcane-db export-updates --out sql/updates)";

    public static bool UpdateRequested => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Root
        => Path.Combine(RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found"), "sql", "updates");

    public static string Folder(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "sqlite",
        DatabaseProvider.MariaDb or DatabaseProvider.MySql => "mariadb",
        DatabaseProvider.PostgreSql => "postgresql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static readonly SemaphoreSlim s_regenerate = new(1, 1);
    private static bool s_regenerated;

    /// <summary>With <c>ARCANECORE_UPDATE_SQL=1</c>, once per run: write the update files and snapshots (the golden tests then compare equal).</summary>
    public static async Task RegenerateIfRequestedAsync()
    {
        if (!UpdateRequested)
        {
            return;
        }

        await s_regenerate.WaitAsync();
        try
        {
            if (!s_regenerated)
            {
                await UpdateScriptWriter.WriteAsync(Root, UpdateComponent.All);
                s_regenerated = true;
            }
        }
        finally
        {
            s_regenerate.Release();
        }
    }

    public static string ReadNormalized(string path) => UpdateScriptWriter.Normalize(File.ReadAllText(path));

    public static SchemaSnapshot ReadSnapshot(string component)
    {
        string path = Path.Combine(Root, component, UpdateScriptWriter.SnapshotFileName);
        if (!File.Exists(path))
        {
            throw new Xunit.Sdk.XunitException($"sql/updates/{component}/{UpdateScriptWriter.SnapshotFileName} does not exist. Generate it: {RegenerateCommand}");
        }

        return SchemaSnapshot.Parse(File.ReadAllText(path));
    }

    /// <summary>The component's files for a provider in the order they are applied: the baseline, then the steps by version.</summary>
    public static IReadOnlyList<string> OrderedFiles(string component, DatabaseProvider provider)
    {
        string directory = Path.Combine(Root, component, Folder(provider));
        if (!Directory.Exists(directory))
        {
            throw new Xunit.Sdk.XunitException($"sql/updates/{component}/{Folder(provider)} does not exist. Generate it: {RegenerateCommand}");
        }

        string[] all = [.. Directory.GetFiles(directory, "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)];
        return
        [
            .. all.Where(f => Path.GetFileName(f).StartsWith("base_v", StringComparison.Ordinal)),
            .. all.Where(f => !Path.GetFileName(f).StartsWith("base_v", StringComparison.Ordinal)),
        ];
    }

    /// <summary>
    /// Run a file's text on the context's connection as the operator's client would: SQLite and PostgreSQL take the whole text
    /// (their clients send it as written), MariaDB is split the way the mysql client does, honouring DELIMITER.
    /// </summary>
    public static async Task ExecuteAsync(DbContext db, DatabaseProvider provider, string text)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            DbConnection connection = db.Database.GetDbConnection();
            IReadOnlyList<string> statements = provider is DatabaseProvider.MariaDb or DatabaseProvider.MySql ? MysqlClientSplitter.Split(text) : [text];
            try
            {
                foreach (string statement in statements)
                {
                    await using DbCommand command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch (DbException)
            {
                // A failed script ends the client's session, which rolls an open transaction back; do the same on this pooled connection.
                await using DbCommand rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                try
                {
                    await rollback.ExecuteNonQueryAsync();
                }
                catch (DbException)
                {
                    // no transaction was open
                }

                throw;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Every user table of the database with its columns (store type, nullability, default), primary key, indexes, and the
    /// version table's rows, as one comparable text. Independent of the update-file writer: it reads the engine's catalog.
    /// </summary>
    public static async Task<string> CatalogDumpAsync(DbContext db, SchemaDefinition definition, bool tolerateMissingVersionTable = false)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        string provider = db.Database.ProviderName ?? string.Empty;
        string tableSql = provider switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' ORDER BY name",
            "Pomelo.EntityFrameworkCore.MySql" => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name",
            var other => throw new NotSupportedException(other),
        };
        string columnSql = provider switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@t) ORDER BY name",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT column_name, column_type, is_nullable, column_default, character_set_name, extra, column_key FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @t ORDER BY column_name",
            _ =>
                "SELECT column_name, data_type, character_maximum_length, is_nullable, column_default, is_identity FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name = @t ORDER BY column_name",
        };

        var text = new StringBuilder();
        foreach (string table in await QueryAsync(db, tableSql, null, r => r.GetString(0)))
        {
            text.Append("table ").Append(table).Append('\n');
            foreach (string column in await QueryAsync(db, columnSql, table, Row))
            {
                text.Append("  column ").Append(column).Append('\n');
            }

            foreach (IndexShape index in await SchemaProbe.ActualIndexesAsync(db, [table]))
            {
                text.Append("  index ").Append(index).Append('\n');
            }
        }

        if (tolerateMissingVersionTable && !await SchemaCatalog.TableExistsAsync(db, definition.VersionTable))
        {
            return text.ToString();
        }

        text.Append("version rows ").Append(UpgradeTestSupport.Quote(db, definition.VersionTable)).Append(": ")
            .Append(await UpgradeTestSupport.CountRowsAsync(db, definition.VersionTable)).Append(" at ")
            .Append(await UpgradeTestSupport.ReadVersionAsync(db, definition.Component)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Every row of every table a declared <see cref="EnsureRowChange"/> seeds, ordered by the key column, as comparable text.
    /// Values are typed alike across engines (a boolean prints as 0/1 whatever the column's storage).
    /// </summary>
    public static async Task<string> SeedRowsAsync(DbContext db, SchemaDefinition definition)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        var text = new StringBuilder();
        foreach (IGrouping<string, EnsureRowChange> table in definition.Steps.SelectMany(s => s.Changes).OfType<EnsureRowChange>()
            .GroupBy(s => s.Table, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string[] columns = [.. table.SelectMany(s => s.Values.Keys.Append(s.KeyColumn)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            string select = $"SELECT {string.Join(", ", columns.Select(c => UpgradeTestSupport.Quote(db, c)))} FROM {UpgradeTestSupport.Quote(db, table.Key)} " +
                $"ORDER BY {string.Join(", ", columns.Select(c => UpgradeTestSupport.Quote(db, c)))}";
            text.Append("seed table ").Append(table.Key).Append(" (").Append(string.Join(", ", columns)).Append(")\n");
            foreach (string row in await QueryAsync(db, select, null, SeedRow))
            {
                text.Append("  ").Append(row).Append('\n');
            }
        }

        return text.ToString();
    }

    private static string SeedRow(DbDataReader reader)
        => string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) switch
        {
            null or DBNull => "NULL",
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var v => v.ToString(),
        }));

    private static string Row(DbDataReader reader)
        => string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) switch
        {
            null or DBNull => "NULL",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var v => v.ToString(),
        }));

    private static async Task<List<string>> QueryAsync(DbContext db, string sql, string? table, Func<DbDataReader, string> read)
    {
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        if (table is not null)
        {
            DbParameter p = command.CreateParameter();
            p.ParameterName = "@t";
            p.Value = table;
            command.Parameters.Add(p);
        }

        var rows = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}

/// <summary>
/// Splits a script into statements the way the <c>mysql</c>/<c>mariadb</c> command-line client does: a <c>DELIMITER x</c> line
/// changes the terminator, quotes and backticks protect it, and comments are dropped.
/// </summary>
internal static class MysqlClientSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        string delimiter = ";";
        bool blank = true;
        int i = 0;
        while (i < script.Length)
        {
            if (blank && string.Compare(script, i, "DELIMITER ", 0, 10, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int end = script.IndexOf('\n', i);
                end = end < 0 ? script.Length : end;
                delimiter = script[(i + 10)..end].Trim();
                i = end + 1;
                continue;
            }

            char c = script[i];
            if (c == '-' && i + 2 < script.Length && script[i + 1] == '-' && char.IsWhiteSpace(script[i + 2]))
            {
                int end = script.IndexOf('\n', i);
                i = end < 0 ? script.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? script.Length : end + 2;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                int j = i + 1;
                while (j < script.Length)
                {
                    if (script[j] == '\\' && c != '`')
                    {
                        j += 2;
                        continue;
                    }

                    if (script[j] == c)
                    {
                        if (j + 1 < script.Length && script[j + 1] == c)
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                current.Append(script, i, Math.Min(j + 1, script.Length) - i);
                blank = false;
                i = j + 1;
                continue;
            }

            if (string.CompareOrdinal(script, i, delimiter, 0, delimiter.Length) == 0)
            {
                Flush();
                i += delimiter.Length;
                continue;
            }

            current.Append(c);
            blank &= char.IsWhiteSpace(c);
            i++;
        }

        Flush();
        return statements;

        void Flush()
        {
            string statement = current.ToString().Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }

            current.Clear();
            blank = true;
        }
    }
}
