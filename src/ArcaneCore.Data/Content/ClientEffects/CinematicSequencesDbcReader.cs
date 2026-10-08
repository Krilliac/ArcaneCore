using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One CinematicSequences.dbc row: the id SMSG_TRIGGER_CINEMATIC starts, its sound and its cameras.</summary>
/// <param name="Id">The cinematic id (field 0).</param>
/// <param name="SoundId">m_soundID (field 1), a SoundEntries id (0 = none).</param>
/// <param name="Cameras">The non-zero m_camera[8] CinematicCamera ids (fields 2-9).</param>
public sealed record CinematicSequence(uint Id, uint SoundId, IReadOnlyList<uint> Cameras);

/// <summary>
/// Reads the developer's build-5875 CinematicSequences.dbc: 10 four-byte fields (vmangos DBCfmt.h
/// CinematicSequencesEntryfmt "nxxxxxxxxx"; mangoszero DBCStructure.h: id 0, sound 1, cameras 2-9). Another layout, a zero
/// or repeated id is refused.
/// </summary>
public static class CinematicSequencesDbcReader
{
    public const string FileName = "CinematicSequences.dbc";

    public const int FieldCount = 10;

    public static DbcTable<CinematicSequence> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<CinematicSequence> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<CinematicSequence>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            var cameras = new List<uint>();
            for (int field = 2; field < FieldCount; field++)
            {
                uint camera = file.GetUInt32(row, field);
                if (camera != 0)
                {
                    cameras.Add(camera);
                }
            }

            rows.Add(new CinematicSequence(file.GetUInt32(row, 0), file.GetUInt32(row, 1), cameras));
        }

        return new DbcTable<CinematicSequence>(FileName, rows, r => r.Id);
    }
}
