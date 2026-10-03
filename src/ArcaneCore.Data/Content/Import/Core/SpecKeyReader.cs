using System.Globalization;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Reads a row's key as unsigned numbers using the table's <see cref="TableSpec"/>: a key
/// column the row does not have is an <see cref="ImportSchemaException"/> (never an implicit 0),
/// as is a key value that is NULL or not an unsigned number. The column lookup is cached per
/// column list, so the per-row cost is a few array reads.
/// </summary>
internal sealed class SpecKeyReader
{
    private readonly Dictionary<string, (IReadOnlyList<string> Columns, int[] Indexes)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public uint[] Read(DumpRow row, string table)
    {
        TableSpec spec = ContentTableSpecs.Find(table)
            ?? throw new InvalidOperationException($"no table spec for `{table}`");
        if (!_cache.TryGetValue(table, out var cached) || !ReferenceEquals(cached.Columns, row.Columns))
        {
            cached = (row.Columns, spec.ResolveKey(row.Columns));
            _cache[table] = cached;
        }

        var key = new uint[cached.Indexes.Length];
        for (int i = 0; i < key.Length; i++)
        {
            string? raw = cached.Indexes[i] < row.Values.Count ? row.Values[cached.Indexes[i]] : null;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out key[i]))
            {
                string column = spec.Keys[i].Canonical;
                throw new ImportSchemaException(
                    table, column, $"table `{table}`: key value '{raw ?? "NULL"}' of `{column}` is not an unsigned number");
            }
        }

        return key;
    }
}
