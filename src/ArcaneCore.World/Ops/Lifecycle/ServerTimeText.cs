using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// vmangos secsToTimeString (Util.cpp:197-243), long form: "1 Day 2 Hours 3 Minutes 4 Seconds."
/// Reproduced quirks: the day, hour and minute words end in a space, the seconds word ends in a
/// full stop, "Hour" is singular for 0 and 1, "Second" for 0 and 1, and seconds print only when
/// non-zero or when nothing else printed.
/// </summary>
public static class ServerTimeText
{
    public static string Format(ulong totalSeconds) => BanTime.FormatDuration(totalSeconds);
}
