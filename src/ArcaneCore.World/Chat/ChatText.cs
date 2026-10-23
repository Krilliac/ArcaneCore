using ArcaneCore.Kernel.Accounts;

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
    public static string SecsToTimeString(long timeInSecs) => BanTime.FormatDuration(timeInSecs);
}
