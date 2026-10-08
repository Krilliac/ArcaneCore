using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Data.Characters;

/// <summary>
/// Decoders for the developer-supplied build-5875 CharSections.dbc and CharacterFacialHairStyles.dbc (never downloaded, never copied into the
/// repository), the data of vmangos <c>Player::ValidateAppearance</c>. Layouts, strict on the field count (a file with another layout is
/// refused rather than guessed at):
/// <list type="bullet">
/// <item>CharSections.dbc, vmangos DBCfmt.h:29 <c>"diiiiixxxi"</c>, 10 fields: 0 id, 1 race, 2 gender, 3 base section, 4 variation index,
/// 5 colour index, 6-8 texture names, 9 flags.</item>
/// <item>CharacterFacialHairStyles.dbc, DBCfmt.h:30 <c>"iiixxxxxx"</c>, 9 fields: 0 race, 1 gender, 2 variation, 3-8 geosets.</item>
/// </list>
/// Rows of a race outside 1-8 are skipped (vmangos ignores non-playable races at load, DBCStores.cpp:204-213), and so are rows whose
/// variation or colour does not fit the byte the client sends, since no request can name them.
/// </summary>
public static class CharacterAppearanceDbcReader
{
    public const int CharSectionsFieldCount = 10;

    public const int FacialHairStylesFieldCount = 9;

    public static CharacterAppearanceCatalog Load(string charSectionsPath, string facialHairStylesPath)
        => Build(DbcFile.Load(charSectionsPath), DbcFile.Load(facialHairStylesPath));

    public static CharacterAppearanceCatalog Build(DbcFile charSections, DbcFile facialHairStyles)
        => new(ReadSections(charSections), ReadFacialHairStyles(facialHairStyles));

    public static IReadOnlyList<CharSectionRecord> ReadSections(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != CharSectionsFieldCount)
        {
            throw new InvalidDataException($"CharSections.dbc has {file.FieldCount} fields, expected {CharSectionsFieldCount}");
        }

        var rows = new List<CharSectionRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint race = file.GetUInt32(row, 1);
            uint gender = file.GetUInt32(row, 2);
            uint section = file.GetUInt32(row, 3);
            uint variation = file.GetUInt32(row, 4);
            uint color = file.GetUInt32(row, 5);
            if (race is 0 or > CharacterAppearanceCatalog.MaxPlayableRace || gender > byte.MaxValue || section > byte.MaxValue
                || variation > byte.MaxValue || color > byte.MaxValue)
            {
                continue;
            }

            rows.Add(new CharSectionRecord((byte)race, (byte)gender, (CharSectionType)section, (byte)variation, (byte)color, file.GetUInt32(row, 9)));
        }

        return rows;
    }

    public static IReadOnlyList<CharFacialHairStyleRecord> ReadFacialHairStyles(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FacialHairStylesFieldCount)
        {
            throw new InvalidDataException($"CharacterFacialHairStyles.dbc has {file.FieldCount} fields, expected {FacialHairStylesFieldCount}");
        }

        var rows = new List<CharFacialHairStyleRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint race = file.GetUInt32(row, 0);
            uint gender = file.GetUInt32(row, 1);
            uint variation = file.GetUInt32(row, 2);
            if (race is 0 or > CharacterAppearanceCatalog.MaxPlayableRace || gender > byte.MaxValue || variation > byte.MaxValue)
            {
                continue;
            }

            rows.Add(new CharFacialHairStyleRecord((byte)race, (byte)gender, (byte)variation));
        }

        return rows;
    }
}
