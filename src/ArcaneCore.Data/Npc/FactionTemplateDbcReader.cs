using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Data.Npc;

/// <summary>
/// Decode developer-supplied build-5875 FactionTemplate.dbc, never downloaded by the daemon.
/// Layout verified at vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0:
/// Database/DBCStructure.h FactionTemplateEntry and Database/DBCfmt.h FactionTemplateEntryfmt.
/// </summary>
public static class FactionTemplateDbcReader
{
    public static FactionTemplateCatalog Load(string path) => Read(DbcFile.Load(path));

    public static FactionTemplateCatalog Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != 14 || file.RecordSize != 56)
        {
            throw new InvalidDataException("build-5875 FactionTemplate.dbc requires fourteen four-byte fields");
        }

        var rows = new List<FactionTemplateRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new FactionTemplateRecord(
                file.GetUInt32(row, 0), file.GetUInt32(row, 1), file.GetUInt32(row, 2),
                file.GetUInt32(row, 3), file.GetUInt32(row, 4), file.GetUInt32(row, 5),
                file.GetUInt32(row, 6), file.GetUInt32(row, 7), file.GetUInt32(row, 8), file.GetUInt32(row, 9),
                file.GetUInt32(row, 10), file.GetUInt32(row, 11), file.GetUInt32(row, 12), file.GetUInt32(row, 13)));
        }

        try
        {
            return new FactionTemplateCatalog(rows);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("invalid or duplicate FactionTemplate.dbc id", error);
        }
    }
}
