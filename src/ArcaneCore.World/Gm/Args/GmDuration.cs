using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Gm.Args;

/// <summary>
/// The vmangos time helpers GM commands use for durations (ban length, shutdown delay, uptime):
/// <c>TimeStringToSecs</c> (vmangos@0e3ff01 src/shared/Util.cpp:252-282) and
/// <c>secsToTimeString</c> (Util.cpp:197-250).
/// </summary>
public static class GmDuration
{
    private const uint Minute = 60;
    private const uint Hour = 60 * Minute;
    private const uint Day = 24 * Hour;

    /// <summary>
    /// "1d2h30m10s" -> seconds. Digits accumulate and each d/h/m/s suffix closes a group;
    /// any other character returns 0 (vmangos "bad format"). Trailing digits without a
    /// suffix are ignored, and the arithmetic wraps at 32 bits as the uint32 original does.
    /// </summary>
    public static uint TimeStringToSecs(string text)
    {
        uint secs = 0;
        uint buffer = 0;
        unchecked
        {
            foreach (char c in text)
            {
                if (c is >= '0' and <= '9')
                {
                    buffer = (buffer * 10) + (uint)(c - '0');
                    continue;
                }

                uint multiplier = c switch
                {
                    'd' => Day,
                    'h' => Hour,
                    'm' => Minute,
                    's' => 1,
                    _ => 0,
                };
                if (multiplier == 0)
                {
                    return 0;
                }

                secs += buffer * multiplier;
                buffer = 0;
            }
        }

        return secs;
    }

    /// <summary>Seconds -> "1 Day 2 Hours 30 Minutes 10 Seconds." (long) or "1d2h30m10s" (short), Util.cpp:197-250.</summary>
    public static string SecsToTimeString(long timeInSecs, bool shortText = false, bool hoursOnly = false)
        => BanTime.FormatDuration(timeInSecs, shortText, hoursOnly);
}
