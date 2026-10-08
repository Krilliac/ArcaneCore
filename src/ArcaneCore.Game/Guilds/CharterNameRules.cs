using System.Text;

namespace ArcaneCore.Game.Guilds;

/// <summary>Reserved (blacklisted) names; vmangos ObjectMgr::IsReservedName (ObjectMgr.cpp:9496-9505). The default set is empty (classic-db reserved_name has no rows).</summary>
public interface ICharterNameBlacklist
{
    /// <summary>Whether the lower-cased name is reserved.</summary>
    bool IsReserved(string lowercaseName);
}

/// <summary>
/// vmangos ObjectMgr::IsValidCharterName (ObjectMgr.cpp:9600-9616): at most 24 and at least the
/// configured minimum characters, and every character from ONE script (extended Latin, Cyrillic
/// or East Asian; digits and spaces allowed in any), ObjectMgr.cpp:9532-9566 over the predicates
/// in Util.h:115-231. vmangos does not trim; neither does this. The antispam filter of the
/// reference has no counterpart here (documented limit).
/// </summary>
public static class CharterNameRules
{
    public static bool IsValid(string name, GuildOptions options, ICharterNameBlacklist? blacklist)
    {
        // Utf8toWStr fails on invalid UTF-8; lone surrogates are the managed equivalent.
        var points = new List<int>(name.Length);
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (rune.Value == 0xFFFD)
            {
                // A replacement character is also what a lone surrogate enumerates as; neither is in any script.
                return false;
            }

            points.Add(rune.Value);
        }

        if (points.Count > GuildOptions.MaxCharterNameLength || points.Count < options.EffectiveMinCharterNameLength)
        {
            return false;
        }

        if (!IsValidString(points, options.StrictCharterNames, options.RealmZone))
        {
            return false;
        }

        // vmangos checks the reserved list only in the buy handler, after this validation (PetitionsHandler.cpp:81-96).
        return blacklist is null || !blacklist.IsReserved(name.ToLowerInvariant());
    }

    /// <summary>The number of code points, which is how vmangos measures a wide string (and the 24-character client limit).</summary>
    public static int CodePointCount(string text)
    {
        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>vmangos isValidString(wstr, strictMask, numericOrSpace: true, create: false) (ObjectMgr.cpp:9543-9578).</summary>
    private static bool IsValidString(List<int> points, int strictMask, int realmZone)
    {
        if (strictMask == 0)
        {
            return All(points, IsExtendedLatin) || All(points, IsCyrillic) || All(points, IsEastAsian);
        }

        if ((strictMask & 0x2) != 0)
        {
            int languages = RealmLanguageType(realmZone);
            if (((languages & LanguageExtendedLatin) != 0 && All(points, IsExtendedLatin))
                || ((languages & LanguageCyrillic) != 0 && All(points, IsCyrillic))
                || ((languages & LanguageEastAsia) != 0 && All(points, IsEastAsian)))
            {
                return true;
            }
        }

        return (strictMask & 0x1) != 0 && All(points, IsBasicLatin);
    }

    // vmangos LanguageType (ObjectMgr.cpp:9507-9513).
    private const int LanguageExtendedLatin = 0x0001;
    private const int LanguageCyrillic = 0x0002;
    private const int LanguageEastAsia = 0x0004;
    private const int LanguageAny = 0xFFFF;

    /// <summary>
    /// vmangos GetRealmLanguageType(create: false) (ObjectMgr.cpp:9515-9541): development, test and QA realms (and the unknown zone 0) take any
    /// language; the United States, Oceanic, Latin America, English, German, French and Spanish zones extended Latin; Korea, Taiwan and China
    /// East Asian; Russian Cyrillic; any other zone any language (basic Latin only at character creation, which a charter is not).
    /// </summary>
    private static int RealmLanguageType(int realmZone) => realmZone switch
    {
        0 or 1 or 26 or 28 => LanguageAny,
        2 or 3 or 4 or 8 or 9 or 10 or 11 => LanguageExtendedLatin,
        6 or 14 or 16 => LanguageEastAsia,
        12 => LanguageCyrillic,
        _ => LanguageAny,
    };

    private static bool All(List<int> points, Func<int, bool> script)
    {
        foreach (int c in points)
        {
            if (!script(c) && !IsNumericOrSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumericOrSpace(int c) => c is >= '0' and <= '9' or ' ';

    private static bool IsBasicLatin(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsExtendedLatin(int c)
        => IsBasicLatin(c)
        || c is >= 0x00C0 and <= 0x00D6
        || c is >= 0x00D8 and <= 0x00DF
        || c is >= 0x00E0 and <= 0x00F6
        || c is >= 0x00F8 and <= 0x00FE
        || c is >= 0x0100 and <= 0x012F
        || c == 0x1E9E;

    private static bool IsCyrillic(int c) => c is >= 0x0410 and <= 0x044F || c == 0x0401 || c == 0x0451;

    private static bool IsEastAsian(int c)
        => c is >= 0x1100 and <= 0x11F9
        || c is >= 0x3041 and <= 0x30FF
        || c is >= 0x3131 and <= 0x318E
        || c is >= 0x31F0 and <= 0x31FF
        || c is >= 0x3400 and <= 0x4DB5
        || c is >= 0x4E00 and <= 0x9FC3
        || c is >= 0xAC00 and <= 0xD7A3
        || c is >= 0xFF01 and <= 0xFFEE;
}
