using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One SpellVisualEffectName.dbc row: a named model a SpellVisualKit attaches.</summary>
/// <param name="Id">The effect id (field 0).</param>
/// <param name="Name">The effect name (field 1).</param>
/// <param name="FileName">The model file (field 2).</param>
public sealed record SpellVisualEffectName(uint Id, string Name, string FileName);

/// <summary>
/// Reads the developer's build-5875 SpellVisualEffectName.dbc: 5 four-byte fields (mangoszero DBCStructure_reference.h
/// SpellVisualEffectNameEntry: id 0, name 1, file name 2, special attach point 3, area effect size 4). Another layout, a
/// zero or repeated id is refused.
/// </summary>
public static class SpellVisualEffectNameDbcReader
{
    public const string FileName = "SpellVisualEffectName.dbc";

    public const int FieldCount = 5;

    public static DbcTable<SpellVisualEffectName> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<SpellVisualEffectName> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<SpellVisualEffectName>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new SpellVisualEffectName(file.GetUInt32(row, 0), file.GetString(row, 1), file.GetString(row, 2)));
        }

        return new DbcTable<SpellVisualEffectName>(FileName, rows, r => r.Id);
    }
}
