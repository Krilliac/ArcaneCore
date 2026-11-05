using ArcaneCore.Data.ClientData;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One SpellVisual.dbc row: the visual a spell names (Spell.dbc SpellVisual) and its channel kit.</summary>
/// <param name="Id">The visual id (field 0).</param>
/// <param name="ChannelKit">ChannelKit (field 5), a SpellVisualKit id (0 = none).</param>
public sealed record SpellVisualEntry(uint Id, uint ChannelKit);

/// <summary>
/// Reads the developer's build-5875 SpellVisual.dbc: 16 four-byte fields (vmangos SpellVisualEntry: id 0, precast kit 1, cast kit 2, impact kit 3,
/// state kit 4, channel kit 5, ...). The server only needs the channel kit: SMSG_SPELL_UPDATE_CHAIN_TARGETS is sent for a channel whose visual has one.
/// Another layout, a zero or repeated id is refused.
/// </summary>
public static class SpellVisualDbcReader
{
    public const string FileName = "SpellVisual.dbc";

    public const int FieldCount = 16;

    private const int ChannelKitField = 5;

    public static DbcTable<SpellVisualEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<SpellVisualEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<SpellVisualEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            int kit = file.GetInt32(row, ChannelKitField);
            rows.Add(new SpellVisualEntry(file.GetUInt32(row, 0), kit > 0 ? (uint)kit : 0));
        }

        return new DbcTable<SpellVisualEntry>(FileName, rows, r => r.Id);
    }
}
