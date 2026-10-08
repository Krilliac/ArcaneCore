using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// Reads every build-5875 Map.dbc row as a <c>map_template</c> row (cmangos <c>MapEntryfmt</c>
/// "nxixssssssssxxxxxxxixxxxxxxxxxxxxxxxxxixxx", DBCStructure.h:572-603): field 0 the id, field 2 the instance type (0 common, 1 dungeon,
/// 2 raid, 3 battleground, the values of vmangos <c>map_template.map_type</c>), field 4 the enUS name, field 19 the linked zone
/// (<c>m_areaTableID</c>). The columns the DBC does not carry (parent, player limit, reset delay, ghost entrance, script) get the SQL
/// defaults; the content importer fills them from the dump (<see cref="MapAreaDbcImporter"/>). Both continents must be there and be common
/// maps; an unknown instance type or a repeated id is refused.
/// </summary>
public static class MapDbcReader
{
    public const int FieldCount = 42;

    public static IReadOnlyList<MapTemplateRow> Read(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<MapTemplateRow> Load(string path) => Read(path);

    public static IReadOnlyList<MapTemplateRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireLayout(file);

        var rows = new List<MapTemplateRow>(file.RecordCount);
        var seen = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint entry = file.GetUInt32(row, 0);
            if (!seen.Add(entry))
                throw new InvalidDataException($"Map.dbc contains the map id {entry} twice");

            uint type = file.GetUInt32(row, 2);
            if (type > (uint)MapType.Battleground)
                throw new InvalidDataException($"Map.dbc map {entry} has the unknown instance type {type}");
            if (entry is 0 or 1 && type != (uint)MapType.Common)
                throw new InvalidDataException($"Map.dbc continent {entry} is not a common map");

            string name = file.GetStringStrict(row, 4);
            if (name.Length > 128) throw new InvalidDataException($"Map {entry} name exceeds the mapped 128-character limit");
            rows.Add(new MapTemplateRow
            {
                Entry = entry,
                Parent = 0,
                MapType = (byte)type,
                LinkedZone = file.GetUInt32(row, 19),
                PlayerLimit = 0,
                ResetDelay = 0,
                GhostEntranceMap = -1,
                GhostEntranceX = 0,
                GhostEntranceY = 0,
                MapName = name,
                ScriptName = string.Empty,
            });
        }

        if (!seen.Contains(0) || !seen.Contains(1))
            throw new InvalidDataException("Map.dbc must contain both continent ids 0 and 1");

        return rows.OrderBy(row => row.Entry).ToArray();
    }

    private static void RequireLayout(DbcFile file)
    {
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
            throw new InvalidDataException($"Map.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}/{file.RecordSize}");
    }
}
