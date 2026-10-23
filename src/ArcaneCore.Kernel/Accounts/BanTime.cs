using System.Globalization;
using System.Numerics;
using System.Text;
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
    public static string SecsToTimeString(ulong timeInSecs) => FormatDuration(timeInSecs, shortText: true);

    /// <summary>
    /// vmangos secsToTimeString(secs, shortText, hoursOnly) (shared/Util.cpp:197-250), the one copy every module
    /// forwards to. Long form: "1 Day 2 Hours 3 Minutes 4 Seconds." (the word forms, trailing spaces and the full stop
    /// after "Seconds" are the reference's own; "Hour" and "Second" are singular for 0 and 1). Short form: "1d2h3m4s".
    /// Negative input is not clamped: each unit is computed with truncating division and printed as is.
    /// </summary>
    public static string FormatDuration(long timeInSecs, bool shortText = false, bool hoursOnly = false)
        => FormatCore(timeInSecs, shortText, hoursOnly);

    /// <summary><see cref="FormatDuration(long, bool, bool)"/> for the full unsigned range (no wrap above <see cref="long.MaxValue"/>).</summary>
    public static string FormatDuration(ulong timeInSecs, bool shortText = false, bool hoursOnly = false)
        => FormatCore(timeInSecs, shortText, hoursOnly);

    private static string FormatCore<T>(T timeInSecs, bool shortText, bool hoursOnly)
        where T : IBinaryInteger<T>
    {
        T one = T.One;
        T secs = timeInSecs % T.CreateChecked(60);
        T minutes = timeInSecs % T.CreateChecked(3600) / T.CreateChecked(60);
        T hours = timeInSecs % T.CreateChecked(86400) / T.CreateChecked(3600);
        T days = timeInSecs / T.CreateChecked(86400);

        var ss = new StringBuilder();
        if (days != T.Zero)
        {
            ss.Append(days.ToString(null, CultureInfo.InvariantCulture));
            ss.Append(shortText ? "d" : days == one ? " Day " : " Days ");
        }

        if (hours != T.Zero || hoursOnly)
        {
            ss.Append(hours.ToString(null, CultureInfo.InvariantCulture));
            ss.Append(shortText ? "h" : hours <= one ? " Hour " : " Hours ");
        }

        if (!hoursOnly)
        {
            if (minutes != T.Zero)
            {
                ss.Append(minutes.ToString(null, CultureInfo.InvariantCulture));
                ss.Append(shortText ? "m" : minutes == one ? " Minute " : " Minutes ");
            }

            if (secs != T.Zero || (days == T.Zero && hours == T.Zero && minutes == T.Zero))
            {
                ss.Append(secs.ToString(null, CultureInfo.InvariantCulture));
                ss.Append(shortText ? "s" : secs <= one ? " Second." : " Seconds.");
            }
        }

        return ss.ToString();
    }
}
