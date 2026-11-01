using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade.Copy;

/// <summary>One table's outcome of a copy: the rows counted on each side and whether the content check agreed.</summary>
/// <param name="Table">The table.</param>
/// <param name="SourceRows">Rows read from the source (counted while copying, and again by <c>COUNT(*)</c> after).</param>
/// <param name="TargetRows">Rows the target holds after the copy.</param>
/// <param name="ContentMatches">Whether the order-independent digest of every converted source row equals the digest of every target row read back.</param>
internal sealed record CopyTableResult(string Table, long SourceRows, long TargetRows, bool ContentMatches)
{
    public bool IsOk => SourceRows == TargetRows && ContentMatches;
}

/// <summary>
/// The engine of <c>arcane-db copy</c> for one component: the model's tables and their order, the structure checks, the
/// emptiness check, the row copy in one target transaction, the sequence reset and the verification. Stateless; every method takes
/// the two contexts. A context is owned by one thread at a time (the caller awaits each call before the next).
/// </summary>
internal static partial class DatabaseCopy
{
    /// <summary>Most rows in one INSERT.</summary>
    public const int MaxBatchRows = 500;

    private const int MaxBatchParameters = 20000;
    internal const int DefaultMaxBatchBytes = 4_000_000;
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string PomeloProvider = "Pomelo.EntityFrameworkCore.MySql";
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>The component's tables (the version table excluded) from the context's EF model, in no particular order.</summary>
    public static IReadOnlyList<CopyTable> TablesOf(DbContext db, SchemaDefinition schema)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        return [.. model.Tables
            .Where(t => !string.Equals(t.Name, schema.VersionTable, StringComparison.OrdinalIgnoreCase))
            .Select(t => new CopyTable(
                t.Name,
                [.. t.Columns.Select(c => c.Name)],
                t.PrimaryKey is null ? [] : [.. t.PrimaryKey.Columns.Select(c => c.Name)],
                [.. t.ForeignKeyConstraints.Select(f => f.PrincipalTable.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
                [.. t.Columns.Select(ModelClrType)]))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    /// <summary>Every table name every component's model owns (so another component's table in a shared database is not "foreign").</summary>
    public static ISet<string> ModelTableNames(IEnumerable<DbContext> contexts)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DbContext db in contexts)
        {
            names.UnionWith(db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Select(t => t.Name));
        }

        return names;
    }

    /// <summary>The actual columns of each table that exists (case-insensitive sets keyed by table name).</summary>
    public static async Task<Dictionary<string, HashSet<string>>> ReadColumnsAsync(DbContext db, IEnumerable<string> tables, CancellationToken ct)
    {
        var present = new HashSet<string>(await SchemaCatalogInfo.ReadTablesAsync(db, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string table in tables.Where(present.Contains))
        {
            result[table] = new HashSet<string>(await SchemaCatalog.ReadColumnsAsync(db, table, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// What is wrong with the database's structure against the model: a model table that is missing, a table nothing owns (it would be
    /// left behind), and a table whose columns differ. Empty when the database has exactly the model's tables and columns.
    /// </summary>
    public static async Task<IReadOnlyList<string>> StructureProblemsAsync(
        DbContext db, IReadOnlyList<CopyTable> tables, ISet<string> allModelTables, string role, CancellationToken ct)
    {
        var problems = new List<string>();
        var present = new HashSet<string>(await SchemaCatalogInfo.ReadTablesAsync(db, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        foreach (string extra in present.Where(t => !allModelTables.Contains(t)).Order(StringComparer.Ordinal))
        {
            problems.Add($"the {role} holds the table '{extra}', which no ArcaneCore data module owns; copy would not carry it (drop or move it first)");
        }

        foreach (CopyTable table in tables)
        {
            if (!present.Contains(table.Name))
            {
                problems.Add($"the {role} lacks the table '{table.Name}'");
                continue;
            }

            var actual = new HashSet<string>(await SchemaCatalog.ReadColumnsAsync(db, table.Name, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
            problems.AddRange(ColumnDifferences(table.Name, table.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase), "the model", actual, $"the {role}"));
        }

        return problems;
    }

    /// <summary>Describe how two column sets of one table differ; empty when equal.</summary>
    public static IEnumerable<string> ColumnDifferences(string table, ISet<string> left, string leftName, ISet<string> right, string rightName)
    {
        string[] onlyLeft = [.. left.Where(c => !right.Contains(c)).Order(StringComparer.Ordinal)];
        string[] onlyRight = [.. right.Where(c => !left.Contains(c)).Order(StringComparer.Ordinal)];
        if (onlyLeft.Length > 0)
        {
            yield return $"table '{table}': column(s) {string.Join(", ", onlyLeft)} exist in {leftName} but not in {rightName}";
        }

        if (onlyRight.Length > 0)
        {
            yield return $"table '{table}': column(s) {string.Join(", ", onlyRight)} exist in {rightName} but not in {leftName}";
        }
    }

    /// <summary>
    /// The rows each existing table holds that the schema bootstrap did not write itself (rows declared by <see cref="EnsureRowChange"/> are the
    /// bootstrap's), so a freshly created target counts as empty. Tables that do not exist yet are skipped.
    /// </summary>
    public static async Task<IReadOnlyList<(string Table, long Rows)>> DataRowsAsync(
        DbContext db, IReadOnlyList<CopyTable> tables, SchemaDefinition schema, CancellationToken ct)
    {
        var present = new HashSet<string>(await SchemaCatalogInfo.ReadTablesAsync(db, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        ILookup<string, EnsureRowChange> seeds = schema.Steps.SelectMany(s => s.Changes).OfType<EnsureRowChange>()
            .Concat(schema.ModelSeedDeclarations).ToLookup(s => s.Table, StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, long)>();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection connection = db.Database.GetDbConnection();
            foreach (CopyTable table in tables.Where(t => present.Contains(t.Name)))
            {
                await using DbCommand command = connection.CreateCommand();
                var text = new StringBuilder("SELECT COUNT(*) FROM ").Append(sql.DelimitIdentifier(table.Name));
                var clauses = new List<string>();
                int p = 0;
                foreach (IGrouping<string, EnsureRowChange> byColumn in seeds[table.Name].GroupBy(s => s.KeyColumn, StringComparer.OrdinalIgnoreCase))
                {
                    var names = new List<string>();
                    foreach (EnsureRowChange seed in byColumn.DistinctBy(s => s.KeyValue))
                    {
                        string name = "@s" + p++;
                        DbParameter parameter = command.CreateParameter();
                        parameter.ParameterName = name;
                        parameter.Value = seed.KeyValue;
                        command.Parameters.Add(parameter);
                        names.Add(name);
                    }

                    clauses.Add($"{sql.DelimitIdentifier(byColumn.Key)} IN ({string.Join(", ", names)})");
                }

                if (clauses.Count > 0)
                {
                    text.Append(" WHERE NOT (").Append(string.Join(" OR ", clauses)).Append(')');
                }

                command.CommandText = text.ToString();
                long rows = System.Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (rows > 0)
                {
                    result.Add((table.Name, rows));
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return result;
    }


    /// <summary>Each target column's kind from the target's catalog types; a SQLite target takes native primitives.</summary>
    public static async Task<Dictionary<string, Dictionary<string, CopyKind>>> ReadKindsAsync(DbContext target, IReadOnlyList<CopyTable> tables, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, CopyKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (CopyTable table in tables)
        {
            result[table.Name] = new Dictionary<string, CopyKind>(StringComparer.OrdinalIgnoreCase);
        }

        string? query = target.Database.ProviderName switch
        {
            SqliteProvider => null,
            PomeloProvider => "SELECT table_name, column_name, data_type, column_type FROM information_schema.columns WHERE table_schema = DATABASE()",
            NpgsqlProvider => "SELECT table_name, column_name, data_type, data_type FROM information_schema.columns WHERE table_schema = current_schema()",
            _ => throw SchemaCatalog.Unsupported(target),
        };

        if (query is null)
        {
            foreach (CopyTable table in tables)
            {
                foreach (string column in table.Columns)
                {
                    result[table.Name][column] = CopyKind.Native;
                }
            }

            return result;
        }

        await foreach (object?[] row in SchemaCatalog.RowsAsync(target, query, ct).ConfigureAwait(false))
        {
            if (result.TryGetValue(SchemaCatalog.Text(row[0]), out Dictionary<string, CopyKind>? columns))
            {
                columns[SchemaCatalog.Text(row[1])] = CopyValues.KindOfServerType(SchemaCatalog.Text(row[2]), SchemaCatalog.Text(row[3]));
            }
        }

        return result;
    }


    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// An order-independent digest of a table's rows: the SHA-256 of each row's canonical text, summed lane-wise (so neither the engines' differing
    /// collations nor row order matter), with the row count. Two tables with the same rows have the same digest.
    /// </summary>
    internal sealed class TableDigest : IEquatable<TableDigest>
    {
        private readonly ulong[] _lanes = new ulong[4];

        public long Rows { get; private set; }

        public void Add(string canonicalRow)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRow));
            for (int i = 0; i < 4; i++)
            {
                _lanes[i] = unchecked(_lanes[i] + BitConverter.ToUInt64(hash, i * 8));
            }

            Rows++;
        }

        public bool Equals(TableDigest? other) => other is not null && Rows == other.Rows && _lanes.AsSpan().SequenceEqual(other._lanes);

        public override bool Equals(object? obj) => Equals(obj as TableDigest);

        public override int GetHashCode() => HashCode.Combine(Rows, _lanes[0]);

        public override string ToString() => string.Concat(_lanes.Select(l => l.ToString("x16", CultureInfo.InvariantCulture)));
    }
}

/// <summary>A row the copy could not convert or insert; the message names the table and column and never holds a connection string.</summary>
internal sealed class CopyDataException(string message, Exception? inner = null) : Exception(message, inner);

internal static partial class DatabaseCopy
{
    /// <summary>The CLR type the model stores a column as (the value converter's provider type when there is one, an enum as its integer, never nullable).</summary>
    internal static Type ModelClrType(IColumn column)
    {
        IProperty property = column.PropertyMappings[0].Property;
        Type type = property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum ? Enum.GetUnderlyingType(type) : type;
    }
}
