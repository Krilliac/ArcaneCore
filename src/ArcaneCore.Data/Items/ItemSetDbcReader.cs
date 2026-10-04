using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Data.Items;

/// <summary>
/// Decoder for the developer-supplied build-5875 ItemSet.dbc (never downloaded by the daemon). Layout: mangos DBCStructure.h ItemSetEntry
/// and DBCfmt.h ItemSetEntryfmt, 45 fields: 0 id, 1-8 locale names (enUS first) and 9 their flags, 10-26 item ids (unused by the server),
/// 27-34 set spell ids, 35-42 piece thresholds, 43 required skill, 44 required skill rank. The field count is strict; a file with another
/// layout is refused rather than guessed at.
/// </summary>
public static class ItemSetDbcReader
{
    public const int FieldCount = 45;

    /// <summary>Field 1 is the enUS name (the first of the 8 locale strings).</summary>
    private const int NameField = 1;

    private const int SpellField = 27;
    private const int ThresholdField = 35;
    private const int RequiredSkillField = 43;
    private const int RequiredSkillRankField = 44;

    public static ItemSetCatalog Load(string path) => Build(DbcFile.Load(path));

    public static ItemSetCatalog Build(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"ItemSet.dbc has {file.FieldCount} fields, expected {FieldCount}");
        }

        var rows = new List<ItemSetRecord>(file.RecordCount);
        HashSet<uint> seen = [];
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (!seen.Add(id))
            {
                throw new InvalidDataException($"ItemSet.dbc lists set {id} twice");
            }

            uint[] spells = new uint[ItemSetRecord.BonusSlots];
            uint[] thresholds = new uint[ItemSetRecord.BonusSlots];
            for (int i = 0; i < ItemSetRecord.BonusSlots; i++)
            {
                spells[i] = file.GetUInt32(row, SpellField + i);
                thresholds[i] = file.GetUInt32(row, ThresholdField + i);
            }

            rows.Add(new ItemSetRecord(
                id, file.GetString(row, NameField), spells, thresholds,
                file.GetUInt32(row, RequiredSkillField), file.GetUInt32(row, RequiredSkillRankField)));
        }

        return new ItemSetCatalog(rows);
    }
}
