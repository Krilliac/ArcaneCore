using ArcaneCore.Data.ClientData;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// Reads all build-5875 WMOAreaTable.dbc rows (20 four-byte fields; vmangos <c>WMOAreaTableEntryfmt</c> "niiixxxxxiixxxxxxxxx", DBCStructure.h):
/// id, WMOID (root id), NameSetID (ADT id), WMOGroupID, Flags (field 9) and AreaTableID (field 10). The sound columns and the localized
/// area name are not kept.
/// </summary>
public static class WmoAreaTableDbcReader
{
    public const int FieldCount = 20;

    public static IReadOnlyList<WmoAreaTemplateRow> Read(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<WmoAreaTemplateRow> Load(string path) => Read(path);

    public static IReadOnlyList<WmoAreaTemplateRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.RequireFields(FieldCount, static table => $"WMOAreaTable.dbc requires {FieldCount} four-byte fields, found {table.FieldCount}/{table.RecordSize}");

        var rows = new List<WmoAreaTemplateRow>(file.RecordCount);
        var ids = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (!ids.Add(id))
                throw new InvalidDataException("WMOAreaTable.dbc contains a duplicate row id");

            rows.Add(new WmoAreaTemplateRow
            {
                Id = id,
                RootId = file.GetInt32(row, 1),
                AdtId = file.GetInt32(row, 2),
                GroupId = file.GetInt32(row, 3),
                Flags = file.GetUInt32(row, 9),
                AreaId = file.GetUInt32(row, 10),
            });
        }

        return rows;
    }
}
