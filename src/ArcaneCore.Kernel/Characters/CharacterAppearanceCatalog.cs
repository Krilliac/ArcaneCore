using System.Collections.Frozen;

namespace ArcaneCore.Kernel.Characters;

/// <summary>vmangos CharSectionType (DBCStructure.h:100-107): the BaseSection column of CharSections.dbc.</summary>
public enum CharSectionType : byte
{
    Skin = 0,
    Face = 1,
    FacialHair = 2,
    Hair = 3,
    Underwear = 4,
}

/// <summary>
/// One CharSections.dbc row as vmangos keeps it (DBCStructure.h:109-120 CharSectionsEntry, DBCfmt.h:29 "diiiiixxxi"): race, gender, the section,
/// the variation and colour indexes and the flags (bit 0 = SECTION_FLAG_UNAVAILABLE). The texture names are client-only and not kept.
/// </summary>
public readonly record struct CharSectionRecord(byte Race, byte Gender, CharSectionType Section, byte Variation, byte Color, uint Flags)
{
    /// <summary>vmangos SECTION_FLAG_UNAVAILABLE (DBCStructure.h:97): the row is never a valid choice.</summary>
    public const uint UnavailableFlag = 0x01;

    public bool Unavailable => (Flags & UnavailableFlag) != 0;
}

/// <summary>One CharacterFacialHairStyles.dbc row (vmangos DBCStructure.h:87-93, DBCfmt.h:30 "iiixxxxxx"): race, gender, variation.</summary>
public readonly record struct CharFacialHairStyleRecord(byte Race, byte Gender, byte Variation);

/// <summary>The five appearance bytes of CMSG_CHAR_CREATE after the gender (skin, face, hair style, hair colour, facial hair).</summary>
public readonly record struct CharacterAppearance(byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialHair);

/// <summary>
/// The appearance choices the 1.12.1 client offers, from CharSections.dbc and CharacterFacialHairStyles.dbc, and vmangos
/// <c>Player::ValidateAppearance</c> (Player.cpp:326-357) over them. Immutable once built; <see cref="Empty"/> (no files configured) means
/// appearance is not checked, which callers test through <see cref="IsEmpty"/>.
/// </summary>
public sealed class CharacterAppearanceCatalog
{
    /// <summary>vmangos RACEMASK_ALL_PLAYABLE (SharedDefines.h): races 1-8. Rows of other races are ignored at load, as vmangos does.</summary>
    public const byte MaxPlayableRace = 8;

    private const byte RaceTauren = 6;
    private const byte RaceNightElf = 4;
    private const byte RaceUndead = 5;
    private const byte GenderFemale = 1;

    private readonly FrozenSet<(byte Race, byte Gender, CharSectionType Section, byte Variation, byte Color)> _sections;
    private readonly FrozenSet<(byte Race, byte Gender, byte Variation)> _facialHair;

    public CharacterAppearanceCatalog(IEnumerable<CharSectionRecord> sections, IEnumerable<CharFacialHairStyleRecord> facialHairStyles)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(facialHairStyles);
        // GetCharSectionEntry (DBCStores.cpp:497-508) skips an unavailable row and keeps looking, so only the available rows matter.
        _sections = sections
            .Where(s => IsPlayable(s.Race) && !s.Unavailable)
            .Select(s => (s.Race, s.Gender, s.Section, s.Variation, s.Color))
            .ToFrozenSet();
        _facialHair = facialHairStyles.Where(f => IsPlayable(f.Race)).Select(f => (f.Race, f.Gender, f.Variation)).ToFrozenSet();
    }

    public static CharacterAppearanceCatalog Empty { get; } = new([], []);

    /// <summary>The available CharSections rows of the playable races.</summary>
    public int SectionCount => _sections.Count;

    /// <summary>The CharacterFacialHairStyles rows of the playable races.</summary>
    public int FacialHairStyleCount => _facialHair.Count;

    /// <summary>No data: the caller does not check appearance.</summary>
    public bool IsEmpty => _sections.Count == 0 && _facialHair.Count == 0;

    /// <summary>vmangos GetCharSectionEntry (DBCStores.cpp:497-508): an available row for the race, section, gender, variation and colour.</summary>
    public bool HasSection(byte race, CharSectionType section, byte gender, byte variation, byte color)
        => _sections.Contains((race, gender, section, variation, color));

    /// <summary>vmangos GetCharFacialHairEntry (DBCStores.cpp:488-495).</summary>
    public bool HasFacialHairStyle(byte race, byte gender, byte variation) => _facialHair.Contains((race, gender, variation));

    /// <summary>
    /// vmangos Player::ValidateAppearance (Player.cpp:326-357), in its order: skin (variation 0, colour = skin), face (variation = face,
    /// colour = skin), hair (variation = style, colour = hair colour), facial hair as a section (colour = hair colour) except for Tauren and for
    /// women other than Night Elves and Undead, which have no such rows, and finally the facial hair style row itself.
    /// </summary>
    public bool IsValid(byte race, byte gender, CharacterAppearance appearance)
    {
        if (!HasSection(race, CharSectionType.Skin, gender, 0, appearance.Skin)
            || !HasSection(race, CharSectionType.Face, gender, appearance.Face, appearance.Skin)
            || !HasSection(race, CharSectionType.Hair, gender, appearance.HairStyle, appearance.HairColor))
        {
            return false;
        }

        bool excludeFacialHairSection = race == RaceTauren || (gender == GenderFemale && race != RaceNightElf && race != RaceUndead);
        if (!excludeFacialHairSection && !HasSection(race, CharSectionType.FacialHair, gender, appearance.FacialHair, appearance.HairColor))
        {
            return false;
        }

        return HasFacialHairStyle(race, gender, appearance.FacialHair);
    }

    private static bool IsPlayable(byte race) => race is >= 1 and <= MaxPlayableRace;
}
