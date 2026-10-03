using System.Globalization;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Bans;

/// <summary>
/// The retail argument, duration and text rules of the ban commands, as faithful ports of the vmangos helpers
/// (no code is copied: the behaviour is re-implemented and cited). Texts are the mangos_string rows 408-428
/// (D:\refs\classic-db Full_DB mangos_string; the multi-line IP entry follows mangos-classic sql/base/mangos.sql:3776,
/// because the classic-db dump lost its newlines).
/// </summary>
public static partial class BanCommandText
{
    // mangos_string 408-428.
    public const string YouBanned = "{0} is banned for {1}. Reason: {2}.";                       // 408
    public const string YouPermBanned = "{0} is banned permanently for {1}.";                    // 409
    public const string BanNotFound = "{0} {1} not found";                                       // 410
    public const string Unbanned = "{0} unbanned.";                                              // 411
    public const string UnbanError = "There was an error removing the ban on {0}.";              // 412
    public const string AccountNotExist = "Account not exist: {0}";                              // 413
    public const string BanInfoNoCharacter = "There is no such character.";                      // 414
    public const string BanInfoNoIp = "There is no such IP in banlist.";                         // 415
    public const string BanInfoNoAccountBan = "Account {0} has never been banned";               // 416
    public const string BanInfoHistory = "Ban history for account {0}:";                         // 417
    public const string BanInfoEntry = "Ban Date: {0} Bantime: {1} Still active: {2}  Reason: {3} Set by: {4}"; // 418
    public const string Infinite = "Inf.";                                                       // 419
    public const string Never = "Never";                                                         // 420
    public const string Yes = "Yes";                                                             // 421
    public const string No = "No";                                                               // 422
    public const string BanInfoIpEntry = "IP: {0}\nBan Date: {1}\nUnban Date: {2}\nRemaining: {3}\nReason: {4}\nSet by: {5}"; // 423
    public const string BanListNoIp = "There is no matching IPban.";                             // 424
    public const string BanListNoAccount = "There is no matching account.";                      // 425
    public const string BanListNoCharacter = "There is no banned account owning a character matching this part."; // 426
    public const string BanListMatchingIp = "The following IPs match your pattern:";             // 427
    public const string BanListMatchingAccount = "The following accounts match your query:";     // 428
    public const string PlayerNotFound = "Player not found!";                                    // 499

    [GeneratedRegex(@"^(\d+[dhms])+$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormedDuration();

    /// <summary>
    /// vmangos TimeStringToSecs (Util.cpp:252-275): digits accumulate, a unit letter d/h/m/s multiplies and adds,
    /// ANY other character returns 0 (which the ban commands treat as a permanent ban); digits without a unit
    /// contribute nothing. The arithmetic is 32-bit unsigned and wraps, like the original.
    /// </summary>
    public static uint TimeStringToSecs(string timeString)
    {
        ArgumentNullException.ThrowIfNull(timeString);
        uint secs = 0;
        uint buffer = 0;
        unchecked
        {
            foreach (char c in timeString)
            {
                if (c is >= '0' and <= '9')
                {
                    buffer *= 10;
                    buffer += (uint)(c - '0');
                    continue;
                }

                uint multiplier = c switch
                {
                    'd' => 86400,
                    'h' => 3600,
                    'm' => 60,
                    's' => 1,
                    _ => 0,
                };
                if (multiplier == 0)
                {
                    return 0; // bad format
                }

                buffer *= multiplier;
                secs += buffer;
                buffer = 0;
            }
        }

        return secs;
    }

    /// <summary>
    /// Whether the duration is a clean <c>1d2h3m4s</c> string (every digit run has a unit). Retail does not ask: an
    /// operator typo becomes a permanent ban. Used only when <c>Bans:RejectUnparseableDuration</c> is on.
    /// </summary>
    public static bool IsWellFormedDuration(string timeString) => WellFormedDuration().IsMatch(timeString);

    /// <summary>vmangos secsToTimeString(secs, shortText = true) (Util.cpp:197-250), e.g. "1d", "2h3m", "45s", "0s".</summary>
    public static string SecsToTimeString(ulong timeInSecs)
    {
        ulong secs = timeInSecs % 60;
        ulong minutes = timeInSecs % 3600 / 60;
        ulong hours = timeInSecs % 86400 / 3600;
        ulong days = timeInSecs / 86400;

        string text = string.Empty;
        if (days != 0)
        {
            text += days.ToString(CultureInfo.InvariantCulture) + "d";
        }

        if (hours != 0)
        {
            text += hours.ToString(CultureInfo.InvariantCulture) + "h";
        }

        if (minutes != 0)
        {
            text += minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (secs != 0 || (days == 0 && hours == 0 && minutes == 0))
        {
            text += secs.ToString(CultureInfo.InvariantCulture) + "s";
        }

        return text;
    }

    /// <summary>MySQL FROM_UNIXTIME shape in the server's local time zone (the baninfo date columns).</summary>
    public static string FromUnixTime(long seconds)
        => DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// vmangos ExtractQuotedOrLiteralArg (Chat.cpp:2890-2945): one token that is either quoted ('x', "x", [x]),
    /// where the closing quote must end the text or be followed by whitespace, or a literal up to the next space.
    /// Returns null when nothing usable is left; <paramref name="rest"/> is advanced past the token and the spaces.
    /// </summary>
    public static string? ExtractQuotedOrLiteralArg(ref string rest)
    {
        string? quoted = ExtractQuoted(ref rest, out bool attempted);
        return attempted ? quoted : ExtractLiteralArg(ref rest);
    }

    /// <summary>vmangos ExtractArg: quoted-or-literal (the link form is not supported; a |-link is rejected as retail does without a link context).</summary>
    public static string? ExtractArg(ref string rest) => ExtractQuotedOrLiteralArg(ref rest);

    /// <summary>vmangos ExtractLiteralArg (Chat.cpp:2818-2888, no literal): rejects quoted text and links, else the first space-delimited token.</summary>
    public static string? ExtractLiteralArg(ref string rest)
    {
        if (rest.Length == 0)
        {
            return null;
        }

        string head = rest;
        switch (head[0])
        {
            case '[':
            case '\'':
            case '"':
                return null;
            case '|':
                if (head.Length < 2 || head[1] != '|')
                {
                    return null;
                }

                head = head[1..]; // the client doubles '|'; skip one
                break;
        }

        int space = head.IndexOf(' ');
        string name = space < 0 ? head : head[..space];
        rest = space < 0 ? string.Empty : head[(space + 1)..].TrimStart(' ');
        return name.Length == 0 ? null : name;
    }

    private static string? ExtractQuoted(ref string rest, out bool attempted)
    {
        attempted = false;
        if (rest.Length == 0 || rest[0] is not ('\'' or '"' or '['))
        {
            return null;
        }

        char guard = rest[0] == '[' ? ']' : rest[0];
        int tail = rest.IndexOf(guard, 1);
        if (tail < 0 || (tail + 1 < rest.Length && !char.IsWhiteSpace(rest[tail + 1])))
        {
            return null; // retail: the quoted form fails, then the literal form rejects a quote-led token too
        }

        attempted = true;
        string value = rest[1..tail];
        rest = tail + 1 < rest.Length ? rest[(tail + 1)..].TrimStart() : string.Empty;
        return value;
    }
}
