using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>A column as one provider stores it.</summary>
/// <param name="Name">Column name.</param>
/// <param name="StoreType">The provider's store type (<c>varchar(16)</c>, <c>INTEGER</c>, <c>integer</c> ...).</param>
/// <param name="Nullable">Whether the column accepts NULL.</param>
public sealed record SnapshotColumn(string Name, string StoreType, bool Nullable);

/// <summary>A secondary index.</summary>
public sealed record SnapshotIndex(string Name, bool Unique, IReadOnlyList<string> Columns);

/// <summary>A table: columns in model order, primary key columns and secondary indexes.</summary>
public sealed record SnapshotTable(
    string Name, IReadOnlyList<SnapshotColumn> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<SnapshotIndex> Indexes);

/// <summary>One provider's schema at the snapshot version and, for a step, at the version before it.</summary>
/// <param name="Tables">The tables after the newest update file.</param>
/// <param name="Previous">The tables before the newest update file (null while the newest file is the baseline); lets the newest file be regenerated.</param>
public sealed record ProviderSnapshot(IReadOnlyList<SnapshotTable> Tables, IReadOnlyList<SnapshotTable>? Previous);

/// <summary>A declared row seed: its table, its key (<c>column=value</c>) and a SHA-256 of every column's typed value.</summary>
public sealed record SnapshotSeed(string Table, string Key, string Hash)
{
    /// <summary>The seeds a definition's steps declare (provider independent).</summary>
    public static IReadOnlyList<SnapshotSeed> Of(SchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Steps.SelectMany(s => s.Changes).OfType<EnsureRowChange>().Select(seed =>
        {
            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal) { [seed.KeyColumn] = seed.KeyValue };
            foreach ((string column, object? value) in seed.Values)
            {
                values[column] = value;
            }

            string canonical = seed.Table + "\n" + string.Join("\n", values.Select(v => v.Key + "=" + Typed(v.Value)));
            return new SnapshotSeed(seed.Table, seed.KeyColumn + "=" + Typed(seed.KeyValue), UpdateScriptWriter.Hash(canonical));
        }).OrderBy(s => s.Table, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    private static string Typed(object? value) => value switch
    {
        null => "null",
        bool b => "b:" + (b ? "1" : "0"),
        sbyte or byte or short or ushort or int or uint or long or ulong => "i:" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        string s => "s:" + s,
        _ => throw new NotSupportedException($"a schema seed value of type {value.GetType().Name} is not recordable"),
    };
}

/// <summary>
/// <c>sql/updates/&lt;component&gt;/schema-snapshot.json</c>: the shape of a component's schema (per provider: tables, columns
/// with store types and nullability, index shapes) after its newest update file, plus a manifest of every released file's
/// content hash. The next schema step is generated against it, and a model change that no step accounts for is refused.
/// The declared <see cref="EnsureRowChange"/> seeds (table, key, hash of the values) are recorded too, so editing a seed's values
/// without a new step fails the golden test. Other initial data comes from the daemons' initializers, not from schema.
/// </summary>
/// <param name="Component">auth, characters or world.</param>
/// <param name="Version">The schema version after the newest update file.</param>
/// <param name="Providers">Keyed by provider folder name (sqlite, mariadb, postgresql).</param>
/// <param name="Files">Path relative to the component folder -> SHA-256 (hex) of the file's LF-normalised text, for every update file.</param>
/// <param name="Seeds">The declared row seeds after the newest update file; null in a snapshot written before seeds were recorded.</param>
public sealed record SchemaSnapshot(
    string Component, int Version, IReadOnlyDictionary<string, ProviderSnapshot> Providers, IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<SnapshotSeed>? Seeds = null)
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ColumnConverter() },
    };

    /// <summary>
    /// The committed JSON form: LF line endings, sorted keys, one line per table (a diff of a step shows the tables it touches
    /// and a column is the array <c>["Name","storeType",nullable]</c>).
    /// </summary>
    public string ToJson()
    {
        static string Js(string value) => JsonSerializer.Serialize(value);
        string Tables(IEnumerable<SnapshotTable> tables, string indent)
            => "[\n" + string.Join(",\n", tables.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => indent + "  " + JsonSerializer.Serialize(t, s_json))) + "\n" + indent + "]";

        var json = new System.Text.StringBuilder();
        json.Append("{\n  \"component\": ").Append(Js(Component)).Append(",\n  \"version\": ").Append(Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\n  \"providers\": {\n");
        json.AppendJoin(",\n", Providers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            $"    {Js(p.Key)}: {{\n      \"tables\": {Tables(p.Value.Tables, "      ")},\n      \"previous\": " +
            (p.Value.Previous is null ? "null" : Tables(p.Value.Previous, "      ")) + "\n    }"));
        json.Append("\n  },\n  \"seeds\": [");
        SnapshotSeed[] seeds = [.. (Seeds ?? []).OrderBy(s => s.Table, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal)];
        if (seeds.Length > 0)
        {
            json.Append('\n').AppendJoin(",\n", seeds.Select(s => "    " + JsonSerializer.Serialize(s, s_json))).Append("\n  ");
        }

        json.Append("],\n  \"files\": {\n");
        json.AppendJoin(",\n", Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"    {Js(f.Key)}: {Js(f.Value)}"));
        json.Append("\n  }\n}\n");
        return json.ToString();
    }

    public static SchemaSnapshot Parse(string json)
        => JsonSerializer.Deserialize<SchemaSnapshot>(json, s_json) ?? throw new InvalidDataException("schema-snapshot.json is empty");

    private sealed class ColumnConverter : System.Text.Json.Serialization.JsonConverter<SnapshotColumn>
    {
        public override SnapshotColumn Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("a snapshot column is [name, storeType, nullable]");
            }

            reader.Read();
            string name = reader.GetString() ?? throw new JsonException("a snapshot column needs a name");
            reader.Read();
            string storeType = reader.GetString() ?? throw new JsonException("a snapshot column needs a store type");
            reader.Read();
            bool nullable = reader.GetBoolean();
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("a snapshot column is [name, storeType, nullable]");
            }

            return new SnapshotColumn(name, storeType, nullable);
        }

        public override void Write(Utf8JsonWriter writer, SnapshotColumn value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value.Name);
            writer.WriteStringValue(value.StoreType);
            writer.WriteBooleanValue(value.Nullable);
            writer.WriteEndArray();
        }
    }
}

/// <summary>
/// A mutable model of a catalog: what a database holds after the update files so far. Seeded from a snapshot (or empty),
/// advanced by a plan's create actions, compared with the code's model.
/// </summary>
internal sealed class SnapshotCatalog
{
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.Ordinal);

    private sealed class TableState(string name)
    {
        public string Name { get; } = name;
        public List<SnapshotColumn> Columns { get; } = [];
        public List<string> PrimaryKey { get; } = [];
        public List<SnapshotIndex> Indexes { get; } = [];
    }

    public static SnapshotCatalog Empty() => new();

    public static SnapshotCatalog From(IEnumerable<SnapshotTable> tables)
    {
        var catalog = new SnapshotCatalog();
        foreach (SnapshotTable table in tables)
        {
            var state = new TableState(table.Name);
            state.Columns.AddRange(table.Columns);
            state.PrimaryKey.AddRange(table.PrimaryKey);
            state.Indexes.AddRange(table.Indexes);
            catalog._tables[table.Name] = state;
        }

        return catalog;
    }

    /// <summary>The catalog the code's model describes for <paramref name="db"/>'s provider.</summary>
    public static SnapshotCatalog FromModel(DbContext db, SchemaDefinition definition)
    {
        SchemaChangeDecider.ModelOperations ops = SchemaChangeDecider.ReadModel(db, definition);
        var catalog = new SnapshotCatalog();
        foreach (CreateTableOperation table in ops.Tables.Values)
        {
            catalog.AddTable(table, table.PrimaryKey?.Columns ?? []);
            foreach (CreateIndexOperation index in ops.IndexesByTable[table.Name])
            {
                catalog.AddIndex(index);
            }
        }

        return catalog;
    }

    public bool HasTable(string table) => _tables.ContainsKey(table);

    /// <summary>The primary key columns of a table (read from the model before a SQL generator can reshape its operation, as SQLite's does for an autoincrement key).</summary>
    public IReadOnlyList<string> PrimaryKeyOf(string table) => _tables.TryGetValue(table, out TableState? state) ? [.. state.PrimaryKey] : [];

    public bool HasColumn(string table, string column)
        => _tables.TryGetValue(table, out TableState? state) && state.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ColumnNames(string table)
        => _tables.TryGetValue(table, out TableState? state) ? [.. state.Columns.Select(c => c.Name)] : [];

    public IReadOnlyList<CatalogIndex> IndexesOf(string table)
        => _tables.TryGetValue(table, out TableState? state) ? [.. state.Indexes.Select(i => new CatalogIndex(i.Name, i.Unique, i.Columns))] : [];

    /// <summary>The catalog after the creates of a planned step.</summary>
    public void Apply(PlannedStep step, SnapshotCatalog model)
    {
        // A row seed (ChangeObject.Row) changes no schema object: it is in the file as a statement but not in the catalog.
        foreach (PlannedAction action in step.Actions.Where(a => a.Decision == ChangeDecision.Create && a.Object != ChangeObject.Row))
        {
            switch (action.Operation)
            {
                case CreateTableOperation table:
                    AddTable(table, model.PrimaryKeyOf(table.Name));
                    break;
                case AddColumnOperation column:
                    TableState owner = _tables[column.Table];
                    owner.Columns.Add(ToColumn(column));
                    break;
                case CreateIndexOperation index:
                    AddIndex(index);
                    break;
                default:
                    throw new InvalidOperationException($"a planned create of {action.Table} carries no operation");
            }
        }
    }

    public IReadOnlyList<SnapshotTable> ToTables()
        => [.. _tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new SnapshotTable(
            t.Name, [.. t.Columns], [.. t.PrimaryKey], [.. t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)]))];

    /// <summary>Every way this catalog differs from <paramref name="model"/> (empty when they are the same schema).</summary>
    public IReadOnlyList<CatalogDifference> Differences(SnapshotCatalog model)
    {
        var found = new List<CatalogDifference>();
        foreach ((string name, TableState have) in _tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!model._tables.TryGetValue(name, out TableState? want))
            {
                found.Add(new CatalogDifference(false, $"table {name} is in the update files but no longer in the model"));
                continue;
            }

            foreach (SnapshotColumn column in have.Columns)
            {
                SnapshotColumn? other = want.Columns.FirstOrDefault(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                if (other is null)
                {
                    found.Add(new CatalogDifference(false, $"column {name}.{column.Name} is in the update files but no longer in the model"));
                }
                else if (!string.Equals(other.StoreType, column.StoreType, StringComparison.OrdinalIgnoreCase) || other.Nullable != column.Nullable)
                {
                    found.Add(new CatalogDifference(
                        false,
                        $"column {name}.{column.Name} changed from {Describe(column)} to {Describe(other)}"));
                }
            }

            foreach (SnapshotColumn column in want.Columns.Where(c => !have.Columns.Any(h => string.Equals(h.Name, c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                found.Add(new CatalogDifference(true, $"column {name}.{column.Name} is in the model but in no update file"));
            }

            if (!have.PrimaryKey.SequenceEqual(want.PrimaryKey, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(new CatalogDifference(false, $"the primary key of {name} changed from ({string.Join(", ", have.PrimaryKey)}) to ({string.Join(", ", want.PrimaryKey)})"));
            }

            foreach (SnapshotIndex index in have.Indexes)
            {
                SnapshotIndex? other = want.Indexes.FirstOrDefault(i => string.Equals(i.Name, index.Name, StringComparison.Ordinal));
                if (other is null || other.Unique != index.Unique || !other.Columns.SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(new CatalogDifference(false, $"index {index.Name} on {name} is in the update files but the model no longer has it in that shape"));
                }
            }

            foreach (SnapshotIndex index in want.Indexes.Where(i => !have.Indexes.Any(h => string.Equals(h.Name, i.Name, StringComparison.Ordinal))))
            {
                found.Add(new CatalogDifference(true, $"index {index.Name} on {name} is in the model but in no update file"));
            }
        }

        foreach (string name in model._tables.Keys.Where(n => !_tables.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            found.Add(new CatalogDifference(true, $"table {name} is in the model but in no update file"));
        }

        return found;
    }

    private static string Describe(SnapshotColumn column) => $"{column.StoreType} {(column.Nullable ? "NULL" : "NOT NULL")}";

    private void AddTable(CreateTableOperation table, IReadOnlyList<string> primaryKey)
    {
        var state = new TableState(table.Name);
        state.Columns.AddRange(table.Columns.Select(ToColumn));
        state.PrimaryKey.AddRange(primaryKey);

        _tables[table.Name] = state;
    }

    private void AddIndex(CreateIndexOperation index)
        => _tables[index.Table].Indexes.Add(new SnapshotIndex(index.Name, index.IsUnique, [.. index.Columns]));

    private static SnapshotColumn ToColumn(AddColumnOperation column)
        => new(column.Name, column.ColumnType ?? string.Empty, column.IsNullable);
}

/// <summary>One difference between the update files' catalog and the model; <see cref="ModelAhead"/> is the additive kind a new step would carry.</summary>
internal sealed record CatalogDifference(bool ModelAhead, string Description);
