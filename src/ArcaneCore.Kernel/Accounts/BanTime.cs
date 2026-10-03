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
    /// returns 0 (the ban commands treat 0 as a PERMANENT ban); digits without a unit contribute nothing.
    /// <para>
    /// Unlike the original, whose 32-bit unsigned arithmetic silently wraps (<c>50000d</c> became a ban of about
    /// 25,000 seconds, and a larger value could wrap to 0, i.e. permanent), the sum is computed with checked
    /// arithmetic. A duration that does not fit in 32 bits is reported by <see cref="TryTimeStringToSecs"/> as
    /// false so a caller can refuse it; this convenience form saturates to <see cref="uint.MaxValue"/> (about 136
    /// years) and never wraps. Ban entry points must use <see cref="TryTimeStringToSecs"/> and refuse on false.
    /// </para>
    /// </summary>
    public static uint TimeStringToSecs(string timeString)
        => TryTimeStringToSecs(timeString, out uint secs) ? secs : uint.MaxValue;

    /// <summary>
    /// <see cref="TimeStringToSecs"/> with the overflow reported. False when the text names a duration that does
    /// not fit in an unsigned 32-bit number of seconds (a digit run or the sum overflows); <paramref name="secs"/>
    /// is then 0 and MUST NOT be used (0 means permanent). True otherwise, with the retail value (0 for a bad format).
    /// </summary>
    public static bool TryTimeStringToSecs(string timeString, out uint secs)
    {
        ArgumentNullException.ThrowIfNull(timeString);
        ulong total = 0;
        ulong buffer = 0;
        foreach (char c in timeString)
        {
            if (c is >= '0' and <= '9')
            {
                buffer = (buffer * 10) + (uint)(c - '0'); // buffer <= uint.MaxValue before this, so no ulong overflow
                if (buffer > uint.MaxValue)
                {
                    secs = 0;
                    return false;
                }

                continue;
            }

            ulong multiplier = c switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                's' => 1,
                _ => 0,
            };
            if (multiplier == 0)
            {
                secs = 0; // bad format: retail returns 0
                return true;
            }

            total += buffer * multiplier; // <= 2^32 * 86400 + a sum we cap below: far from ulong overflow
            if (total > uint.MaxValue)
            {
                secs = 0;
                return false;
            }

            buffer = 0;
        }

        secs = (uint)total;
        return true;
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
