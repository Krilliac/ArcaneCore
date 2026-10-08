using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One ZoneMusic.dbc row: a named music set and the SoundEntries ids it plays by day and by night.</summary>
/// <param name="Id">The set id (field 0), what AreaTable's zone music column points at.</param>
/// <param name="SetName">The set name (field 1).</param>
/// <param name="DaySound">Sounds[0] (field 6), a SoundEntries id (0 = none).</param>
/// <param name="NightSound">Sounds[1] (field 7), a SoundEntries id (0 = none).</param>
public sealed record ZoneMusicEntry(uint Id, string SetName, uint DaySound, uint NightSound);

/// <summary>
/// Reads the developer's build-5875 ZoneMusic.dbc: 8 four-byte fields (mangoszero DBCStructure_reference.h ZoneMusicEntry:
/// id 0, set name 1, silence interval min 2-3 and max 4-5, sounds 6-7). Another layout, a zero or repeated id is refused.
/// </summary>
public static class ZoneMusicDbcReader
{
    public const string FileName = "ZoneMusic.dbc";

    public const int FieldCount = 8;

    public static DbcTable<ZoneMusicEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<ZoneMusicEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<ZoneMusicEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new ZoneMusicEntry(file.GetUInt32(row, 0), file.GetString(row, 1), file.GetUInt32(row, 6), file.GetUInt32(row, 7)));
        }

        return new DbcTable<ZoneMusicEntry>(FileName, rows, r => r.Id);
    }
}
