using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Data.Social;

/// <summary>
/// Reads the developer's own 1.12.1 ChatChannels.dbc (no client data is shipped): 21 four-byte fields per row, build 5875
/// (vmangos DBCfmt.h ChatChannelsEntryfmt "nixssssssssxxxxxxxxxx": the id, the flags, the faction group, the eight locale
/// name patterns; the pattern mask and the shortcut names are not read). A file of another client build has another field
/// count (2.4.3 has 37) and is refused. Rows are kept in file order, which decides which pattern wins (vmangos
/// GetChannelEntryFor walks the store from row 0).
/// </summary>
public static class ChatChannelsDbcReader
{
    public const int FieldCount = 21;

    private const int FirstPattern = 3;

    private const int Locales = 8;

    public static IReadOnlyList<ChatChannelRow> Load(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyList<ChatChannelRow> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"ChatChannels.dbc of build 5875 has {FieldCount} fields, this file has {file.FieldCount}");
        }

        var rows = new List<ChatChannelRow>(file.RecordCount);
        var ids = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (id == 0 || !ids.Add(id))
            {
                throw new InvalidDataException($"ChatChannels.dbc has a zero or duplicate channel id {id}");
            }

            var patterns = new string[Locales];
            for (int locale = 0; locale < Locales; locale++)
            {
                patterns[locale] = file.GetString(row, FirstPattern + locale);
            }

            rows.Add(new ChatChannelRow(id, file.GetUInt32(row, 1), file.GetUInt32(row, 2), patterns));
        }

        return rows;
    }
}
