using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One SoundEntries.dbc row: the id the client plays (SMSG_PLAY_SOUND / SMSG_PLAY_MUSIC), its type, name and files.</summary>
/// <param name="Id">The sound id (field 0).</param>
/// <param name="SoundType">m_soundType (field 1).</param>
/// <param name="Name">m_name, the internal name (field 2).</param>
/// <param name="Files">The non-empty m_File[10] names (fields 3-12), in field order.</param>
/// <param name="Directory">m_DirectoryBase (field 23), the folder the files are in.</param>
public sealed record SoundEntry(uint Id, int SoundType, string Name, IReadOnlyList<string> Files, string Directory);

/// <summary>
/// Reads the developer's build-5875 SoundEntries.dbc (no client data is shipped): 29 four-byte fields (mangos-classic
/// DBCStructure.h SoundEntriesEntry and mangoszero DBCfmt.h SoundEntriesfmt: id 0, type 1, name 2, files 3-12,
/// frequencies 13-22, directory 23, volume 24, flags 25, min distance 26, distance cutoff 27, EAX 28). Another layout is
/// refused, as is a zero or repeated id.
/// </summary>
public static class SoundEntriesDbcReader
{
    public const string FileName = "SoundEntries.dbc";

    public const int FieldCount = 29;

    private const int FirstFile = 3;

    private const int Files = 10;

    private const int DirectoryField = 23;

    public static DbcTable<SoundEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<SoundEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<SoundEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            var files = new List<string>();
            for (int index = 0; index < Files; index++)
            {
                string name = file.GetString(row, FirstFile + index);
                if (name.Length > 0)
                {
                    files.Add(name);
                }
            }

            rows.Add(new SoundEntry(file.GetUInt32(row, 0), file.GetInt32(row, 1), file.GetString(row, 2), files, file.GetString(row, DirectoryField)));
        }

        return new DbcTable<SoundEntry>(FileName, rows, r => r.Id);
    }
}
