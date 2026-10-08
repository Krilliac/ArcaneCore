using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// Decode the developer-supplied build-5875 AreaTrigger.dbc into <c>areatrigger_template</c> rows (never downloaded by the daemon).
/// Layout: vmangos DBCfmt.h:26 <c>"niffffffff"</c> and DBCStructure.h AreaTriggerEntry: ID 0, map 1, x 2, y 3, z 4, radius 5,
/// box length 6, box width 7, box height 8, box orientation 9; ten four-byte fields. The DBC has no name (the column is left empty).
/// The client's patch archives replace the file whole, so the operator passes the effective copy (patch-2 over patch over base).
/// </summary>
public static class AreaTriggerDbcReader
{
    public const int FieldCount = 10;

    public static IReadOnlyList<AreaTriggerTemplateRow> Load(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<AreaTriggerTemplateRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
        {
            throw new InvalidDataException($"build-5875 AreaTrigger.dbc requires {FieldCount} four-byte fields, found {file.FieldCount} ({file.RecordSize}-byte records)");
        }

        var rows = new Dictionary<uint, AreaTriggerTemplateRow>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            var trigger = new AreaTriggerTemplateRow
            {
                Id = id,
                Name = string.Empty,
                MapId = file.GetUInt32(row, 1),
                X = Finite(file.GetFloat(row, 2), id),
                Y = Finite(file.GetFloat(row, 3), id),
                Z = Finite(file.GetFloat(row, 4), id),
                Radius = Finite(file.GetFloat(row, 5), id),
                BoxX = Finite(file.GetFloat(row, 6), id),
                BoxY = Finite(file.GetFloat(row, 7), id),
                BoxZ = Finite(file.GetFloat(row, 8), id),
                BoxOrientation = Finite(file.GetFloat(row, 9), id),
            };
            if (!rows.TryAdd(id, trigger))
            {
                throw new InvalidDataException($"duplicate area trigger id {id} in AreaTrigger.dbc");
            }
        }

        return [.. rows.Values.OrderBy(r => r.Id)];
    }

    private static float Finite(float value, uint id)
        => float.IsFinite(value) ? value : throw new InvalidDataException($"area trigger {id} has a non-finite coordinate");
}
