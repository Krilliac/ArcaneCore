using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Data.Reputation;

/// <summary>
/// Decode a developer-supplied build-5875 Faction.dbc (never downloaded by the daemon) through
/// the shared <see cref="DbcFile"/> WDBC reader. Layout: cmangos/mangos-classic DBCfmt.h
/// FactionEntryfmt "niiiiiiiiiiiiiiiiiissssssssxxxxxxxxxx" (37 fields, 148-byte records):
/// 0 id, 1 reputation list index, 2-5 race masks, 6-9 class masks, 10-13 base values,
/// 14-17 base flags, 18 parent faction, 19-26 names, 27 name flags, 28-36 descriptions/flags.
/// </summary>
public static class FactionDbcReader
{
    public const int FieldCount = 37;

    public static FactionCatalog Load(string path) => Read(DbcFile.Load(path));

    public static FactionCatalog Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount || file.RecordSize != FieldCount * 4)
        {
            throw new InvalidDataException("build-5875 Faction.dbc requires thirty-seven four-byte fields");
        }

        var rows = new List<FactionRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new FactionRecord(
                file.GetUInt32(row, 0),
                file.GetInt32(row, 1),
                [file.GetUInt32(row, 2), file.GetUInt32(row, 3), file.GetUInt32(row, 4), file.GetUInt32(row, 5)],
                [file.GetUInt32(row, 6), file.GetUInt32(row, 7), file.GetUInt32(row, 8), file.GetUInt32(row, 9)],
                [file.GetInt32(row, 10), file.GetInt32(row, 11), file.GetInt32(row, 12), file.GetInt32(row, 13)],
                [file.GetUInt32(row, 14), file.GetUInt32(row, 15), file.GetUInt32(row, 16), file.GetUInt32(row, 17)],
                file.GetUInt32(row, 18),
                file.GetString(row, 19)));
        }

        try
        {
            return new FactionCatalog(rows);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("invalid Faction.dbc: " + error.Message, error);
        }
    }
}
