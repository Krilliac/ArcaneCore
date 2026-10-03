using System.Globalization;
using System.Text.RegularExpressions;

namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// The retail ban-duration helpers shared by the in-game ban commands and the account tool: ports of
/// vmangos shared/Util.cpp TimeStringToSecs (:252-275) and secsToTimeString (:197-250). Behaviour is
/// re-implemented, not copied.
/// </summary>
public static partial class BanTime
{
    [GeneratedRegex(@"^(\d+[dhms])+$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormed();

    /// <summary>
    /// vmangos TimeStringToSecs: digits accumulate, a unit letter d/h/m/s multiplies and adds, ANY other character
    /// returns 0 (the ban commands treat 0 as a PERMANENT ban); digits without a unit contribute nothing. The
    /// arithmetic is 32-bit unsigned and wraps, like the original.
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

    /// <summary>Whether the text is a clean <c>1d2h3m4s</c> string (every digit run has a unit); retail does not ask.</summary>
    public static bool IsWellFormed(string timeString) => WellFormed().IsMatch(timeString);

    /// <summary>vmangos secsToTimeString(secs, shortText = true): "1d", "2h3m", "45s", "0s".</summary>
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
}
