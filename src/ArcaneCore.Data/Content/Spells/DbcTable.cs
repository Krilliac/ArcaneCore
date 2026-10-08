namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// The rows of one client DBC keyed by their id (field 0), in file order. Built by a table's reader, which
/// refuses a zero or repeated id, so a lookup by id is exact. Reusable by any reader whose rows have a unique id.
/// </summary>
/// <typeparam name="TRow">The decoded row.</typeparam>
public sealed class DbcTable<TRow>
    where TRow : class
{
    private readonly Dictionary<uint, TRow> _byId;

    public DbcTable(string fileName, IEnumerable<TRow> rows, Func<TRow, uint> id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(id);
        FileName = fileName;
        var ordered = new List<TRow>();
        _byId = [];
        foreach (TRow row in rows)
        {
            uint key = id(row);
            if (key == 0 || !_byId.TryAdd(key, row))
            {
                throw new InvalidDataException($"{fileName} has a zero or duplicate id {key}");
            }

            ordered.Add(row);
        }

        Rows = ordered;
    }

    /// <summary>The DBC file name the rows came from (for messages), e.g. "SoundEntries.dbc".</summary>
    public string FileName { get; }

    /// <summary>Every row in file order.</summary>
    public IReadOnlyList<TRow> Rows { get; }

    public int Count => Rows.Count;

    public bool Contains(uint id) => _byId.ContainsKey(id);

    /// <summary>The row with <paramref name="id"/>, or null.</summary>
    public TRow? Find(uint id) => _byId.GetValueOrDefault(id);
}
