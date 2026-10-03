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

        if (!IsValidString(points, options.StrictCharterNames))
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

    private static bool IsValidString(List<int> points, int strictMask)
    {
        if (strictMask == 0)
        {
            return All(points, IsExtendedLatin) || All(points, IsCyrillic) || All(points, IsEastAsian);
        }

        // Bit 0x2 (realm-zone language) is not supported; see GuildOptions.StrictCharterNames.
        return (strictMask & 0x1) != 0 && All(points, IsBasicLatin);
    }

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
