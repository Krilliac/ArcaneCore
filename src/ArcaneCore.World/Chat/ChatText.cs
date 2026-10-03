using System.Text;

namespace ArcaneCore.World.Chat;

/// <summary>Chat text helpers that reproduce the reference formatting exactly.</summary>
public static class ChatText
{
    /// <summary>
    /// vmangos <c>secsToTimeString(timeInSecs)</c> with the default arguments (shared/Util.cpp:197-248):
    /// "1 Day 2 Hours 3 Minutes 4 Seconds." — the word forms, the trailing spaces and the full stop
    /// after "Seconds" are the reference's own.
    /// It is what the mute notice (mangos_string 705, "You must wait %s before speaking again.") shows.
    /// </summary>
    public static string SecsToTimeString(long timeInSecs)
    {
        long secs = timeInSecs % 60;
        long minutes = timeInSecs % 3600 / 60;
        long hours = timeInSecs % 86400 / 3600;
        long days = timeInSecs / 86400;

        var text = new StringBuilder();
        if (days != 0)
        {
            text.Append(days).Append(days == 1 ? " Day " : " Days ");
        }

        if (hours != 0)
        {
            text.Append(hours).Append(hours <= 1 ? " Hour " : " Hours ");
        }

        if (minutes != 0)
        {
            text.Append(minutes).Append(minutes == 1 ? " Minute " : " Minutes ");
        }

        if (secs != 0 || (days == 0 && hours == 0 && minutes == 0))
        {
            text.Append(secs).Append(secs <= 1 ? " Second." : " Seconds.");
        }

        return text.ToString();
    }
}
