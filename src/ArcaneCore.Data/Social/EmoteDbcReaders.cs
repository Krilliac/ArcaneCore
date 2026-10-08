using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Data.Social;

/// <summary>
/// Reads the developer's own 1.12.1 EmotesText.dbc and Emotes.dbc (no client data is shipped). Build 5875 layouts, vmangos
/// DBCfmt.h: EmotesTextEntryfmt "nxixxxxxxxxxxxxxxxx" (19 fields: id, name, emote id, sixteen EmotesTextData references) and
/// EmotesEntryfmt "nsxiiix" (7 fields: id, slash command, animation, flags, emote type, stand state, sound). Another field
/// count is another client build and is refused.
/// </summary>
public static class EmoteDbcReaders
{
    public const int TextFieldCount = 19;

    public const int EmoteFieldCount = 7;

    public static IReadOnlyList<TextEmoteRow> LoadText(string path) => ReadText(DbcFile.Load(path));

    public static IReadOnlyList<EmoteRow> LoadEmotes(string path) => ReadEmotes(DbcFile.Load(path));

    public static IReadOnlyList<TextEmoteRow> ReadText(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Require(file, TextFieldCount, "EmotesText.dbc");
        var rows = new List<TextEmoteRow>(file.RecordCount);
        var ids = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"EmotesText.dbc has a duplicate id {id}");
            }

            rows.Add(new TextEmoteRow(id, file.GetUInt32(row, 2)));
        }

        return rows;
    }

    public static IReadOnlyList<EmoteRow> ReadEmotes(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Require(file, EmoteFieldCount, "Emotes.dbc");
        var rows = new List<EmoteRow>(file.RecordCount);
        var ids = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Emotes.dbc has a duplicate id {id}");
            }

            rows.Add(new EmoteRow(id, file.GetUInt32(row, 3), file.GetUInt32(row, 4), file.GetUInt32(row, 5)));
        }

        return rows;
    }

    private static void Require(DbcFile file, int fields, string name)
    {
        if (file.FieldCount != fields)
        {
            throw new InvalidDataException($"{name} of build 5875 has {fields} fields, this file has {file.FieldCount}");
        }
    }
}
