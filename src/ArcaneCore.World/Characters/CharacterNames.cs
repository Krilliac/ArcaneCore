using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;

namespace ArcaneCore.World.Characters;

/// <summary>
/// Player-name rules, following vmangos: normalizePlayerName (first letter upper case, the
/// rest lower case, ObjectMgr.cpp:64-80, with the exact wcharToUpper/wcharToLower tables of
/// Util.h:268-316) and ObjectMgr::CheckPlayerName with its defaults — 2 to 12 characters
/// (MinPlayerName = 2, MAX_PLAYER_NAME = 12) and, with StrictPlayerNames = 0, letters from
/// exactly one script: extended Latin, Cyrillic or East Asian (Util.h isValidString). Lengths
/// are counted in code points, like vmangos' wchar_t strings on Linux. Names reserved by the
/// realm (vmangos reserved_name) are handled by the creation rules, not here.
/// </summary>
public static class CharacterNames
{
    public const int MinLength = 2;
    public const int MaxLength = 12;

    /// <summary>vmangos MAX_INTERNAL_PLAYER_NAME: normalizePlayerName rejects anything longer.</summary>
    private const int MaxInternalLength = 15;

    /// <summary>
    /// Upper-case the first character and lower-case the rest, as vmangos normalizePlayerName
    /// does before every lookup and on creation. Only the character ranges of Util.h wcharToUpper
    /// and wcharToLower are mapped (Basic and Extended Latin, sharp s, Cyrillic); every other
    /// character is left as it is. Returns the input unchanged when empty.
    /// </summary>
    public static string Normalize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        return string.Create(name.Length, name, static (span, source) =>
        {
            span[0] = ToUpper(source[0]);
            for (int i = 1; i < source.Length; i++)
            {
                span[i] = ToLower(source[i]);
            }
        });
    }

    /// <summary>
    /// normalizePlayerName over the raw bytes of CMSG_CHAR_CREATE: null when the name is empty,
    /// not valid UTF-8 (Utf8toWStr fails) or longer than 15 code points — the three cases the
    /// handler answers with CHAR_NAME_NO_NAME (CharacterHandler.cpp:249-254).
    /// </summary>
    public static string? NormalizeUtf8(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return CharacterNameRules.CodePointCount(decoded) > MaxInternalLength ? null : Normalize(decoded);
    }

    /// <summary><see cref="ValidateUtf8(ReadOnlySpan{byte}, in NameRuleSettings, out string?)"/> with the vmangos defaults.</summary>
    public static CharResult? ValidateUtf8(ReadOnlySpan<byte> raw, out string? normalized)
        => ValidateUtf8(raw, NameRuleSettings.Default, out normalized);

    /// <summary>
    /// The whole creation-time name check over the raw bytes: normalize, then
    /// <see cref="CharacterNameRules.Check"/>. Returns the CHAR_NAME_* code, or null with the normalized name.
    /// </summary>
    public static CharResult? ValidateUtf8(ReadOnlySpan<byte> raw, in NameRuleSettings settings, out string? normalized)
    {
        normalized = NormalizeUtf8(raw);
        return normalized is null ? CharResult.CharNameNoName : CharacterNameRules.Check(normalized, settings);
    }

    /// <summary>
    /// Check a (normalized) name for character creation with the vmangos defaults. Returns null
    /// when it is acceptable, otherwise the CHAR_NAME_* code SMSG_CHAR_CREATE reports
    /// (vmangos HandleCharCreateOpcode).
    /// </summary>
    public static CharResult? Validate(string name) => CharacterNameRules.Check(name, NameRuleSettings.Default);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);


    /// <summary>vmangos Util.h wcharToUpper (:268-292).</summary>
    private static char ToUpper(char c)
    {
        if (c is >= 'a' and <= 'z')
        {
            return (char)(c - 0x20);
        }

        if (c == 'ß')
        {
            return 'ẞ'; // LATIN SMALL LETTER SHARP S → CAPITAL SHARP S
        }

        if (c is (>= 'à' and <= 'ö') or (>= 'ø' and <= 'þ'))
        {
            return (char)(c - 0x20);
        }

        if (c is >= 'ā' and <= 'į' && c % 2 == 1)
        {
            return (char)(c - 1);
        }

        if (c is >= 'а' and <= 'я')
        {
            return (char)(c - 0x20);
        }

        return c == 'ё' ? 'Ё' : c;
    }

    /// <summary>vmangos Util.h wcharToLower (:294-316).</summary>
    private static char ToLower(char c)
    {
        if (c is >= 'A' and <= 'Z')
        {
            return (char)(c + 0x20);
        }

        if (c is (>= 'À' and <= 'Ö') or (>= 'Ø' and <= 'Þ'))
        {
            return (char)(c + 0x20);
        }

        if (c is >= 'Ā' and <= 'Į' && c % 2 == 0)
        {
            return (char)(c + 1);
        }

        if (c == 'ẞ')
        {
            return 'ß';
        }

        if (c == 'Ё')
        {
            return 'ё';
        }

        return c is >= 'А' and <= 'Я' ? (char)(c + 0x20) : c;
    }


    /// <summary>vmangos Util.h isBasicLatinCharacter.</summary>
    internal static bool IsBasicLatin(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    /// <summary>vmangos Util.h isExtendedLatinCharacter.</summary>
    internal static bool IsExtendedLatin(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
        or (>= 'À' and <= 'Ö')
        or (>= 'Ø' and <= 'ß')
        or (>= 'à' and <= 'ö')
        or (>= 'ø' and <= 'þ')
        or (>= 'Ā' and <= 'į')
        or 'ẞ';

    /// <summary>vmangos Util.h isCyrillicCharacter.</summary>
    internal static bool IsCyrillic(char c) => c is (>= 'А' and <= 'я') or 'Ё' or 'ё';

    /// <summary>vmangos Util.h isEastAsianCharacter.</summary>
    internal static bool IsEastAsian(char c) =>
        c is (>= 'ᄀ' and <= 'ᇹ')
        or (>= 'ぁ' and <= 'ヿ')
        or (>= 'ㄱ' and <= 'ㆎ')
        or (>= 'ㇰ' and <= 'ㇿ')
        or (>= '㐀' and <= '䶵')
        or (>= '一' and <= '鿃')
        or (>= '가' and <= '힣')
        or (>= '！' and <= '￮');
}
