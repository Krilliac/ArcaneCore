using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;

namespace ArcaneCore.Atlas.Reference.Display;

/// <summary>
/// The display-related DBC tables of the 1.12.1.5875 client, parsed once. A table the client does not have is tolerated (it is
/// simply not <see cref="Has"/>); a table with another field count than the one measured on build 5875 is refused by name.
/// Immutable after <see cref="Load"/>; safe to share between threads.
/// </summary>
public sealed class DisplayTables
{
    /// <summary>Table name to the field count measured on the 5875 client's own files.</summary>
    public static IReadOnlyDictionary<string, int> ExpectedFieldCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["CreatureDisplayInfo"] = 12,
        ["CreatureModelData"] = 16,
        ["CreatureDisplayInfoExtra"] = 19,
        ["ItemDisplayInfo"] = 23,
        ["GameObjectDisplayInfo"] = 12,
        ["CharSections"] = 10,
        ["ChrRaces"] = 29,
        ["AnimationData"] = 7,
        ["Emotes"] = 7,
        ["EmotesText"] = 19,
        ["CharHairGeosets"] = 6,
    };

    private readonly Dictionary<string, WdbcFile> _tables;
    private readonly Dictionary<string, Dictionary<uint, int>> _rowsById = new(StringComparer.Ordinal);

    private DisplayTables(Dictionary<string, WdbcFile> tables)
    {
        _tables = tables;
        foreach ((string name, WdbcFile table) in tables)
        {
            var rows = new Dictionary<uint, int>(table.RecordCount);
            for (int i = 0; i < table.RecordCount; i++)
            {
                rows.TryAdd(table.GetUInt32(i, 0), i);
            }

            _rowsById[name] = rows;
        }
    }

    /// <summary>The tables that were present, in name order.</summary>
    public IReadOnlyList<string> Available => [.. _tables.Keys.Order(StringComparer.Ordinal)];

    /// <exception cref="InvalidDataException">A present table is damaged or has the wrong field count (the message names it).</exception>
    public static DisplayTables Load(IClientFileSource files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var tables = new Dictionary<string, WdbcFile>(StringComparer.Ordinal);
        foreach ((string name, int fields) in ExpectedFieldCounts)
        {
            byte[]? bytes = files.Read($"DBFilesClient\\{name}.dbc");
            if (bytes is null)
            {
                continue;
            }

            WdbcFile table;
            try
            {
                table = WdbcFile.Parse(bytes);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{name}: {e.Message}", e);
            }

            if (table.FieldCount != fields)
            {
                throw new InvalidDataException($"{name} has {table.FieldCount} fields; the 1.12.1.5875 layout has {fields}.");
            }

            tables.Add(name, table);
        }

        return new DisplayTables(tables);
    }

    public bool Has(string name) => _tables.ContainsKey(name);

    public int FieldCount(string name) => Table(name).FieldCount;

    public int RecordCount(string name) => Table(name).RecordCount;

    /// <summary>The table's parsed file; throws <see cref="KeyNotFoundException"/> when the client has no such table.</summary>
    public WdbcFile Table(string name)
        => _tables.TryGetValue(name, out WdbcFile? table) ? table : throw new KeyNotFoundException($"The client has no {name} table.");

    /// <summary>The record index whose first field (the id) equals <paramref name="id"/>, or -1.</summary>
    public int FindRow(string name, uint id)
        => _rowsById.TryGetValue(name, out Dictionary<uint, int>? rows) && rows.TryGetValue(id, out int row) ? row : -1;
}
