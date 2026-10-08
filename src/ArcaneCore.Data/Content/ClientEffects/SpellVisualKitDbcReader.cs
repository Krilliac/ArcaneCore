using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One SpellVisualKit.dbc row: the kit SMSG_PLAY_SPELL_VISUAL plays, with its animation, effects and sound.</summary>
/// <param name="Id">The kit id (field 0).</param>
/// <param name="KitType">KitType (field 1).</param>
/// <param name="AnimId">AnimID (field 2), an AnimationData id.</param>
/// <param name="Effects">
/// The positive SpellVisualEffectName ids of the head, chest, base, left hand, right hand, breath, three special and world
/// effect columns (fields 3-12), in field order.
/// </param>
/// <param name="SoundId">SoundID (field 13), a SoundEntries id (0 = none).</param>
public sealed record SpellVisualKitEntry(uint Id, int KitType, int AnimId, IReadOnlyList<uint> Effects, uint SoundId);

/// <summary>
/// Reads the developer's build-5875 SpellVisualKit.dbc: 35 four-byte fields (mangoszero DBCStructure_reference.h
/// SpellVisualKitEntry: id 0, kit type 1, anim 2, effects 3-12, sound 13, shake 14, char procs 15-18, char params 19-34).
/// Another layout, a zero or repeated id is refused.
/// </summary>
public static class SpellVisualKitDbcReader
{
    public const string FileName = "SpellVisualKit.dbc";

    public const int FieldCount = 35;

    private const int FirstEffect = 3;

    private const int LastEffect = 12;

    private const int SoundField = 13;

    public static DbcTable<SpellVisualKitEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<SpellVisualKitEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<SpellVisualKitEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            var effects = new List<uint>();
            for (int field = FirstEffect; field <= LastEffect; field++)
            {
                int effect = file.GetInt32(row, field);
                if (effect > 0)
                {
                    effects.Add((uint)effect);
                }
            }

            int sound = file.GetInt32(row, SoundField);
            rows.Add(new SpellVisualKitEntry(file.GetUInt32(row, 0), file.GetInt32(row, 1), file.GetInt32(row, 2), effects, sound > 0 ? (uint)sound : 0));
        }

        return new DbcTable<SpellVisualKitEntry>(FileName, rows, r => r.Id);
    }
}
