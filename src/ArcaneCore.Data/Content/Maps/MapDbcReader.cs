using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>Reads the build-5875 Map.dbc rows needed to seed the two continent maps.</summary>
public static class MapDbcReader
{
    public const int FieldCount = 42;
    private const uint CommonMapType = 0;

    public static IReadOnlyList<MapTemplateRow> Read(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<MapTemplateRow> Load(string path) => Read(path);

    public static IReadOnlyList<MapTemplateRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        RequireLayout(file);

        var rows = new List<MapTemplateRow>(2);
        var seen = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint entry = file.GetUInt32(row, 0);
            if (entry is not (0 or 1))
                continue;

            if (!seen.Add(entry))
                throw new InvalidDataException("Map.dbc contains a duplicate continent id");
            if (file.GetUInt32(row, 2) != CommonMapType)
                throw new InvalidDataException("Map.dbc continent is not a common map");

            string name = file.GetStringStrict(row, 4);
            if (name.Length > 128) throw new InvalidDataException($"Map {entry} name exceeds the mapped 128-character limit");
            rows.Add(new MapTemplateRow
            {
                Entry = entry,
                Parent = 0,
                MapType = (byte)MapType.Common,
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

        if (seen.Count != 2)
            throw new InvalidDataException("Map.dbc must contain both continent ids 0 and 1");

        return rows.OrderBy(row => row.Entry).ToArray();
    }

    private static void RequireLayout(DbcFile file)
    {
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
            throw new InvalidDataException($"Map.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}/{file.RecordSize}");
    }
}
