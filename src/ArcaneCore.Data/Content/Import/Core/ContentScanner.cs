using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Reads every input once, before any importer runs, and checks what the importers would
/// otherwise read silently wrong: that each table they read has its key columns (a renamed
/// <c>Entry</c> would make <c>CreatureDumpImporter</c> read every key as 0 and write one
/// template), that the keys do not collapse (<c>distinct keys &lt;= 1</c> over several rows),
/// and which dialect each table is in. Streaming: only per-table key sets are kept.
/// <para>
/// Only <c>INSERT</c>/<c>REPLACE</c> rows count as content. A dump is a snapshot, so
/// <c>UPDATE</c>/<c>DELETE</c>/<c>ALTER</c>/<c>TRUNCATE</c> statements (such as the classic-db
/// <c>Updates/</c> files) are not applied; they are counted so the report says so.
/// </para>
/// </summary>
public static class ContentScanner
{
    /// <summary>
    /// Scan <paramref name="inputs"/> in order with one shared <c>CREATE TABLE</c> registry.
    /// </summary>
    /// <exception cref="ImportSchemaException">A spec'd table lacks a key column, has an empty key, has collapsed keys, or matches no (or several) dialects.</exception>
    public static ScanResult Scan(IReadOnlyList<DumpInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var registry = MySqlDumpReader.NewTableRegistry();
        var tables = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase);
        var unapplied = new Dictionary<string, int>(StringComparer.Ordinal);
        string? dbVersion = null;

        foreach (DumpInput input in inputs)
        {
            using TextReader text = input.Open();
            var reader = new MySqlDumpReader(text, registry);
            foreach (object item in reader.Read())
            {
                switch (item)
                {
                    case DumpTable table:
                        State(tables, table.Name).AddColumns(table.Columns);
                        break;
                    case DumpRow row:
                        TableState state = State(tables, row.Table);
                        state.Add(row);
                        if (dbVersion is null && string.Equals(row.Table, "db_version", StringComparison.OrdinalIgnoreCase)
                            && row.TryGet(out string? version, "version"))
                        {
                            dbVersion = version;
                        }

                        break;
                }
            }

            foreach ((string key, int count) in reader.UnappliedStatements)
            {
                unapplied[key] = unapplied.GetValueOrDefault(key) + count;
            }
        }

        var result = new Dictionary<string, TableScan>(StringComparer.OrdinalIgnoreCase);
        foreach (TableState state in tables.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            result[state.Name] = state.Finish();
        }

        return new ScanResult(result, [.. inputs.Select(i => i.Name)], dbVersion, unapplied);
    }

    private static TableState State(Dictionary<string, TableState> tables, string name)
    {
        if (!tables.TryGetValue(name, out TableState? state))
        {
            tables[name] = state = new TableState(name);
        }

        return state;
    }

    private sealed class TableState(string name)
    {
        private readonly List<string> _columns = [];
        private readonly HashSet<string> _columnSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>? _keys = ContentTableSpecs.Find(name) is null ? null : new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<string>? _resolvedFor;
        private int[] _keyIndexes = [];
        private ContentDialect _dialect;
        private long _rows;

        public string Name => name;

        private TableSpec? Spec { get; } = ContentTableSpecs.Find(name);

        public void AddColumns(IReadOnlyList<string> columns)
        {
            foreach (string column in columns)
            {
                if (_columnSet.Add(column))
                {
                    _columns.Add(column);
                }
            }
        }

        public void Add(DumpRow row)
        {
            AddColumns(row.Columns);
            _rows++;
            if (Spec is null)
            {
                return;
            }

            if (!ReferenceEquals(_resolvedFor, row.Columns))
            {
                // A new column list (the CREATE TABLE order, or this INSERT's own list).
                _keyIndexes = Spec.ResolveKey(row.Columns);
                ContentDialect dialect = Spec.DetectDialect(row.Columns);
                if (_dialect != ContentDialect.Unknown && dialect != _dialect)
                {
                    throw new ImportSchemaException(name, null, $"table `{name}` is in dialect {dialect} in one statement and {_dialect} in another");
                }

                _dialect = dialect;
                _resolvedFor = row.Columns;
            }

            _keys!.Add(KeyOf(row));
        }

        public TableScan Finish()
        {
            long distinct = _keys?.Count ?? 0;
            if (Spec is not null && _rows > 1 && distinct <= 1)
            {
                throw new ImportSchemaException(
                    name, Spec.Keys[0].Canonical,
                    $"table `{name}`: {_rows} rows collapse to {distinct} distinct key(s) on ({string.Join(", ", Spec.Keys.Select(k => k.Canonical))}); " +
                    "the key column is mis-named or defaulted in the source, importing would keep one row");
            }

            string[] mapped = Spec is null ? [] : [.. _columns.Where(Spec.IsMapped)];
            string[] unmapped = Spec is null ? [.. _columns] : [.. _columns.Where(c => !Spec.IsMapped(c))];
            return new TableScan(name, [.. _columns], _rows, distinct, Spec is null ? 0 : _rows - distinct, Spec is not null, _dialect, mapped, unmapped);
        }

        private string KeyOf(DumpRow row)
        {
            if (_keyIndexes.Length == 1)
            {
                return KeyPart(row, 0) ?? string.Empty;
            }

            return string.Join('\u001F', _keyIndexes.Select((_, i) => KeyPart(row, i)));
        }

        private string? KeyPart(DumpRow row, int part)
        {
            string? value = _keyIndexes[part] < row.Values.Count ? row.Values[_keyIndexes[part]] : null;
            if (string.IsNullOrEmpty(value))
            {
                throw new ImportSchemaException(
                    name, Spec!.Keys[part].Canonical,
                    $"table `{name}`: row {_rows} has a NULL or empty `{Spec.Keys[part].Canonical}` key; importing it would read the key as 0");
            }

            return value;
        }
    }
}
