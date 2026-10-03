using System.Buffers;
using System.Text;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Characters.Creation;

/// <summary>
/// The configurable part of vmangos ObjectMgr::CheckPlayerName (ObjectMgr.cpp:9507-9598):
/// MinPlayerName, StrictPlayerNames and the RealmZone alphabet.
/// </summary>
/// <param name="MinLength">MinPlayerName (2..12).</param>
/// <param name="StrictMask">StrictPlayerNames: 0 = any one script, bit 1 = basic Latin, bit 2 = realm zone script.</param>
/// <param name="RealmZone">RealmZone (RealmZone.h).</param>
/// <param name="Create">True when checking a name being created (a tournament realm zone then accepts only basic Latin).</param>
public readonly record struct NameRuleSettings(int MinLength, uint StrictMask, int RealmZone, bool Create)
{
    /// <summary>The vmangos defaults: MinPlayerName 2, StrictPlayerNames 0, RealmZone 1.</summary>
    public static NameRuleSettings Default { get; } = new(CharacterNames.MinLength, 0, 1, true);

    public static NameRuleSettings From(CharacterCreationOptions options, bool create)
        => new(options.EffectiveMinPlayerName, options.StrictPlayerNames, options.RealmZone, create);
}

/// <summary>Parameterised player-name checks (vmangos CheckPlayerName + isValidString).</summary>
public static class CharacterNameRules
{
    private const int MaxInternalLength = 15;

    // vmangos LanguageType (ObjectMgr.cpp:9507-9513).
    private const int LtBasicLatin = 0x0000;
    private const int LtExtendedLatin = 0x0001;
    private const int LtCyrillic = 0x0002;
    private const int LtEastAsia = 0x0004;
    private const int LtAny = 0xFFFF;

    /// <summary>
    /// Check a normalized name. Returns null when acceptable, else the CHAR_NAME_* code:
    /// empty or longer than 15 code points → NO_NAME (normalizePlayerName fails), an undecodable
    /// name → INVALID_CHARACTER, above 12 → TOO_LONG, below MinPlayerName → TOO_SHORT, a script
    /// the realm does not accept → MIXED_LANGUAGES.
    /// </summary>
    public static CharResult? Check(string name, in NameRuleSettings settings)
    {
        if (name.Length == 0)
        {
            return CharResult.CharNameNoName;
        }

        if (!IsWellFormed(name))
        {
            return CharResult.CharNameInvalidCharacter; // ObjectMgr.cpp:9582
        }

        int length = CodePointCount(name);
        if (length > MaxInternalLength)
        {
            return CharResult.CharNameNoName; // ObjectMgr.cpp:64-80, max_len
        }

        if (length > CharacterNames.MaxLength)
        {
            return CharResult.CharNameTooLong;
        }

        if (length < settings.MinLength)
        {
            return CharResult.CharNameTooShort;
        }

        return IsValidString(name, settings) ? null : CharResult.CharNameMixedLanguages;
    }

    /// <summary>vmangos GetRealmLanguageType (ObjectMgr.cpp:9515-9541).</summary>
    internal static int RealmLanguageType(int realmZone, bool create)
    {
        switch (realmZone)
        {
            case 0 or 1 or 26 or 28:
                return LtAny;
            case 2 or 3 or 4 or 8 or 9 or 10 or 11:
                return LtExtendedLatin;
            case 6 or 14 or 16:
                return LtEastAsia;
            case 12:
                return LtCyrillic;
            default:
                return create ? LtBasicLatin : LtAny; // basic Latin at create, any at login
        }
    }

    /// <summary>vmangos isValidString with numericOrSpace = false (ObjectMgr.cpp:9543-9578).</summary>
    internal static bool IsValidString(string name, in NameRuleSettings settings)
    {
        if (settings.StrictMask == 0)
        {
            return All(name, CharacterNames.IsExtendedLatin) || All(name, CharacterNames.IsCyrillic) || All(name, CharacterNames.IsEastAsian);
        }

        if ((settings.StrictMask & 0x2) != 0)
        {
            int lt = RealmLanguageType(settings.RealmZone, settings.Create);
            if ((lt & LtExtendedLatin) != 0 && All(name, CharacterNames.IsExtendedLatin))
            {
                return true;
            }

            if ((lt & LtCyrillic) != 0 && All(name, CharacterNames.IsCyrillic))
            {
                return true;
            }

            if ((lt & LtEastAsia) != 0 && All(name, CharacterNames.IsEastAsian))
            {
                return true;
            }
        }

        return (settings.StrictMask & 0x1) != 0 && All(name, CharacterNames.IsBasicLatin);
    }

    internal static int CodePointCount(string text)
    {
        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    internal static bool IsWellFormed(string text)
    {
        ReadOnlySpan<char> span = text;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out _, out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            span = span[consumed..];
        }

        return true;
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
}
