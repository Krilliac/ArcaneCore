using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>Reads all build-5875 AreaTable.dbc rows without applying world-domain policy.</summary>
public static class AreaTableDbcReader
{
    public const int FieldCount = 25;

    public static IReadOnlyList<AreaTemplateRow> Read(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<AreaTemplateRow> Load(string path) => Read(path);

    public static IReadOnlyList<AreaTemplateRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
            throw new InvalidDataException($"AreaTable.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}/{file.RecordSize}");

        var rows = new List<AreaTemplateRow>(file.RecordCount);
        var ids = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint entry = file.GetUInt32(row, 0);
            if (!ids.Add(entry))
                throw new InvalidDataException("AreaTable.dbc contains a duplicate area id");

            string name = file.GetStringStrict(row, 11);
            if (name.Length > 128) throw new InvalidDataException($"Area {entry} name exceeds the mapped 128-character limit");
            rows.Add(new AreaTemplateRow
            {
                Entry = entry,
                MapId = file.GetUInt32(row, 1),
                ZoneId = file.GetUInt32(row, 2),
                ExploreFlag = file.GetUInt32(row, 3),
                Flags = file.GetUInt32(row, 4),
                AreaLevel = file.GetInt32(row, 10),
                Name = name,
                Team = file.GetUInt32(row, 20),
                LiquidTypeId = file.GetUInt32(row, 24),
            });
        }

        return rows;
    }
}
