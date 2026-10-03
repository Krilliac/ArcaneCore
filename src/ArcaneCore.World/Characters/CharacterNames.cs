using System.Text;
using ArcaneCore.Protocol;

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

        return CodePointCount(decoded) > MaxInternalLength ? null : Normalize(decoded);
    }

    /// <summary>
    /// The whole creation-time name check over the raw bytes: normalize, then
    /// <see cref="Validate"/>. Returns the CHAR_NAME_* code, or null with the normalized name.
    /// </summary>
    public static CharResult? ValidateUtf8(ReadOnlySpan<byte> raw, out string? normalized)
    {
        normalized = NormalizeUtf8(raw);
        return normalized is null ? CharResult.CharNameNoName : Validate(normalized);
    }

    /// <summary>
    /// Check a (normalized) name for character creation. Returns null when it is acceptable,
    /// otherwise the CHAR_NAME_* code SMSG_CHAR_CREATE reports (vmangos HandleCharCreateOpcode).
    /// </summary>
    public static CharResult? Validate(string name)
    {
        if (name.Length == 0)
        {
            return CharResult.CharNameNoName;
        }

        // CheckPlayerName: a name that cannot be converted to wide characters is
        // CHAR_NAME_INVALID_CHARACTER (ObjectMgr.cpp:9582); a lone surrogate is that case here.
        if (!IsWellFormed(name))
        {
            return CharResult.CharNameInvalidCharacter;
        }

        int length = CodePointCount(name);

        // normalizePlayerName fails for an over-long name → CHAR_NAME_NO_NAME.
        if (length > MaxInternalLength)
        {
            return CharResult.CharNameNoName;
        }

        if (length > MaxLength)
        {
            return CharResult.CharNameTooLong;
        }

        if (length < MinLength)
        {
            return CharResult.CharNameTooShort;
        }

        if (All(name, IsExtendedLatin) || All(name, IsCyrillic) || All(name, IsEastAsian))
        {
            return null;
        }

        return CharResult.CharNameMixedLanguages;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int CodePointCount(string text)
    {
        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static bool IsWellFormed(string text)
    {
        ReadOnlySpan<char> span = text;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out _, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            span = span[consumed..];
        }

        return true;
    }

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
