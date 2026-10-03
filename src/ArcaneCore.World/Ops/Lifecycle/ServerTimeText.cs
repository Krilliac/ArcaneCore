using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// vmangos secsToTimeString (Util.cpp:197-243), long form: "1 Day 2 Hours 3 Minutes 4 Seconds."
/// Reproduced quirks: the day, hour and minute words end in a space, the seconds word ends in a
/// full stop, "Hour" is singular for 0 and 1, "Second" for 0 and 1, and seconds print only when
/// non-zero or when nothing else printed.
/// </summary>
public static class ServerTimeText
{
    public static string Format(ulong totalSeconds)
    {
        ulong secs = totalSeconds % 60;
        ulong minutes = totalSeconds % 3600 / 60;
        ulong hours = totalSeconds % 86400 / 3600;
        ulong days = totalSeconds / 86400;

        var text = new StringBuilder();
        if (days != 0)
        {
            text.Append(days.ToString(CultureInfo.InvariantCulture)).Append(days == 1 ? " Day " : " Days ");
        }

        if (hours != 0)
        {
            text.Append(hours.ToString(CultureInfo.InvariantCulture)).Append(hours <= 1 ? " Hour " : " Hours ");
        }

        if (minutes != 0)
        {
            text.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append(minutes == 1 ? " Minute " : " Minutes ");
        }

        if (secs != 0 || (days == 0 && hours == 0 && minutes == 0))
        {
            text.Append(secs.ToString(CultureInfo.InvariantCulture)).Append(secs <= 1 ? " Second." : " Seconds.");
        }

        return text.ToString();
    }
}
