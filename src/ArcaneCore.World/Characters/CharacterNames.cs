using ArcaneCore.Protocol;

namespace ArcaneCore.World.Characters;

/// <summary>
/// Player-name rules, following vmangos: normalizePlayerName (first letter upper case, the
/// rest lower case) and ObjectMgr::CheckPlayerName with its defaults — 2 to 12 characters
/// (MinPlayerName = 2, MAX_PLAYER_NAME = 12) and, with StrictPlayerNames = 0, letters from
/// exactly one script: extended Latin, Cyrillic or East Asian (Util.h isValidString). Names
/// reserved by the realm (vmangos reserved_name) are not supported yet.
/// </summary>
public static class CharacterNames
{
    public const int MinLength = 2;
    public const int MaxLength = 12;

    /// <summary>vmangos MAX_INTERNAL_PLAYER_NAME: normalizePlayerName rejects anything longer.</summary>
    private const int MaxInternalLength = 15;

    /// <summary>
    /// Upper-case the first character and lower-case the rest, as vmangos normalizePlayerName
    /// does before every lookup and on creation. Returns the input unchanged when empty.
    /// </summary>
    public static string Normalize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        return string.Create(name.Length, name, static (span, source) =>
        {
            span[0] = char.ToUpperInvariant(source[0]);
            for (int i = 1; i < source.Length; i++)
            {
                span[i] = char.ToLowerInvariant(source[i]);
            }
        });
    }

    /// <summary>
    /// Check a (normalized) name for character creation. Returns null when it is acceptable,
    /// otherwise the CHAR_NAME_* code SMSG_CHAR_CREATE reports (vmangos HandleCharCreateOpcode).
    /// </summary>
    public static CharResult? Validate(string name)
    {
        // normalizePlayerName fails for an empty or over-long name → CHAR_NAME_NO_NAME.
        if (name.Length is 0 or > MaxInternalLength)
        {
            return CharResult.CharNameNoName;
        }

        if (name.Length > MaxLength)
        {
            return CharResult.CharNameTooLong;
        }

        if (name.Length < MinLength)
        {
            return CharResult.CharNameTooShort;
        }

        if (All(name, IsExtendedLatin) || All(name, IsCyrillic) || All(name, IsEastAsian))
        {
            return null;
        }

        return CharResult.CharNameMixedLanguages;
    }

    private static bool All(string name, Func<char, bool> predicate)
    {
        foreach (char c in name)
        {
            if (!predicate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>vmangos Util.h isExtendedLatinCharacter.</summary>
    private static bool IsExtendedLatin(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
        or (>= 'À' and <= 'Ö')
        or (>= 'Ø' and <= 'ß')
        or (>= 'à' and <= 'ö')
        or (>= 'ø' and <= 'þ')
        or (>= 'Ā' and <= 'į')
        or 'ẞ';

    /// <summary>vmangos Util.h isCyrillicCharacter.</summary>
    private static bool IsCyrillic(char c) => c is (>= 'А' and <= 'я') or 'Ё' or 'ё';

    /// <summary>vmangos Util.h isEastAsianCharacter.</summary>
    private static bool IsEastAsian(char c) =>
        c is (>= 'ᄀ' and <= 'ᇹ')
        or (>= 'ぁ' and <= 'ヿ')
        or (>= 'ㄱ' and <= 'ㆎ')
        or (>= 'ㇰ' and <= 'ㇿ')
        or (>= '㐀' and <= '䶵')
        or (>= '一' and <= '鿃')
        or (>= '가' and <= '힣')
        or (>= '！' and <= '￮');
}
