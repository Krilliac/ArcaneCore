namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// One part of a table's key. <see cref="Canonical"/> is the name used in messages; the
/// importers look the column up by any of <see cref="Names"/> (case-insensitive). A key part has
/// no default: a table without it is rejected rather than read with an implicit 0.
/// </summary>
public sealed record KeyColumn(string Canonical, IReadOnlyList<string> Names)
{
    public KeyColumn(string canonical, params string[] aliases)
        : this(canonical, (IReadOnlyList<string>)[canonical, .. aliases])
    {
    }
}

/// <summary>
/// Columns that identify a dialect: every <see cref="Present"/> column exists and no
/// <see cref="Absent"/> column does.
/// </summary>
public sealed record DialectSignature(ContentDialect Dialect, IReadOnlyList<string> Present, IReadOnlyList<string> Absent)
{
    public DialectSignature(ContentDialect dialect, params string[] present)
        : this(dialect, (IReadOnlyList<string>)present, (IReadOnlyList<string>)[])
    {
    }

    public bool Matches(ISet<string> columns)
        => Present.All(columns.Contains) && !Absent.Any(columns.Contains);

    public override string ToString() => Dialect + " {" + string.Join(", ", Present) + (Absent.Count > 0 ? "; without " + string.Join(", ", Absent) : string.Empty) + "}";
}

/// <summary>
/// What the importers expect of one source table: its key, the columns they actually read
/// (<see cref="Mapped"/>, every alias of every dialect) and the signatures that tell the dialects
/// apart. Tables without a spec are not read by any importer yet.
/// </summary>
public sealed class TableSpec
{
    private readonly HashSet<string> _mapped;
    private readonly Func<string, bool>? _mappedWhen;

    /// <param name="mappedWhen">When the importer maps by rule rather than by list (a <see cref="RowMapper{T}"/>), the rule: whether it reads a column.</param>
    public TableSpec(string table, IReadOnlyList<KeyColumn> keys, IEnumerable<string> mapped, IReadOnlyList<DialectSignature> signatures, Func<string, bool>? mappedWhen = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        Table = table;
        Keys = keys;
        Signatures = signatures;
        _mappedWhen = mappedWhen;
        _mapped = new HashSet<string>(mapped, StringComparer.OrdinalIgnoreCase);
        foreach (KeyColumn key in keys)
        {
            _mapped.UnionWith(key.Names);
        }
    }

    public string Table { get; }

    public IReadOnlyList<KeyColumn> Keys { get; }

    public IReadOnlyList<DialectSignature> Signatures { get; }

    /// <summary>Every column name (key parts included, every alias) an importer reads.</summary>
    public IReadOnlyCollection<string> MappedColumns => _mapped;

    /// <summary>Whether an importer reads this source column.</summary>
    public bool IsMapped(string column) => _mapped.Contains(column) || (_mappedWhen?.Invoke(column) ?? false);

    /// <summary>
    /// The index in <paramref name="columns"/> of each key part.
    /// </summary>
    /// <exception cref="ImportSchemaException">A key part has none of its names among the columns.</exception>
    public int[] ResolveKey(IReadOnlyList<string> columns)
    {
        var indexes = new int[Keys.Count];
        for (int k = 0; k < Keys.Count; k++)
        {
            int found = -1;
            foreach (string name in Keys[k].Names)
            {
                found = IndexOf(columns, name);
                if (found >= 0)
                {
                    break;
                }
            }

            if (found < 0)
            {
                throw new ImportSchemaException(
                    Table,
                    Keys[k].Canonical,
                    $"table `{Table}` has no key column `{Keys[k].Canonical}` (accepted: {string.Join(", ", Keys[k].Names)}); columns found: {string.Join(", ", columns)}. " +
                    "Importing it would read every key as 0.");
            }

            indexes[k] = found;
        }

        return indexes;
    }

    /// <summary>
    /// The dialect whose signature matches <paramref name="columns"/>; <see cref="ContentDialect.Unknown"/>
    /// when the table has no signatures (one layout only).
    /// </summary>
    /// <exception cref="ImportSchemaException">No signature, or more than one, matches.</exception>
    public ContentDialect DetectDialect(IReadOnlyList<string> columns)
    {
        if (Signatures.Count == 0)
        {
            return ContentDialect.Unknown;
        }

        var set = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        DialectSignature[] matches = [.. Signatures.Where(s => s.Matches(set))];
        if (matches.Length == 1)
        {
            return matches[0].Dialect;
        }

        string found = string.Join(", ", columns);
        if (matches.Length == 0)
        {
            throw new ImportSchemaException(
                Table, null,
                $"table `{Table}`: no known dialect matches its columns ({found}); expected one of: {string.Join("; ", Signatures)}");
        }

        throw new ImportSchemaException(
            Table, null,
            $"table `{Table}`: ambiguous dialect, its columns ({found}) match {string.Join(" and ", matches.Select(m => m.ToString()))}");
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
