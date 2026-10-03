using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Data.Graveyards;

/// <summary>
/// Decode the developer-supplied build-5875 WorldSafeLocs.dbc (never downloaded by the daemon). Layout: vmangos
/// DBCfmt.h:90 <c>"nifffxxxxxxxxx"</c> and DBCStructure.h:770-778 WorldSafeLocsEntry: ID 0, continent (map) 1, x 2, y 3,
/// z 4, eight localized names 5-12 (the first is enUS), string flags 13; fourteen four-byte fields. The facing is not in
/// the DBC (vmangos keeps it in <c>world_safe_locs_facing</c>), so <see cref="WorldSafeLoc.Orientation"/> is 0 here.
/// </summary>
public static class WorldSafeLocsDbcReader
{
    public const int FieldCount = 14;

    public static IReadOnlyList<WorldSafeLoc> Load(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<WorldSafeLoc> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"build-5875 WorldSafeLocs.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}");
        }

        var rows = new Dictionary<uint, WorldSafeLoc>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            var loc = new WorldSafeLoc(id, file.GetUInt32(row, 1), file.GetFloat(row, 2), file.GetFloat(row, 3), file.GetFloat(row, 4), 0f, file.GetString(row, 5));
            if (!rows.TryAdd(id, loc))
            {
                throw new InvalidDataException($"duplicate safe location id {id} in WorldSafeLocs.dbc");
            }
        }

        return [.. rows.Values.OrderBy(r => r.Id)];
    }
}
