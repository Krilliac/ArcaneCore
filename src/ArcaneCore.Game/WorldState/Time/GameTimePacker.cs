namespace ArcaneCore.Game.WorldState.Time;

/// <summary>
/// The client's calendar bit field of SMSG_LOGIN_SETTIMESPEED (vmangos
/// <c>secsToTimeBitFields</c>, Server/Packets/Misc.cpp:924-933): minute | hour&lt;&lt;6 |
/// weekday&lt;&lt;11 | (day-1)&lt;&lt;14 | month0&lt;&lt;20 | (year-2000)&lt;&lt;24, from LOCAL time fields
/// (<c>localtime_r</c>), Sunday = 0. Verified against the gtker test vector 0x1673320A
/// (2022-08-13, Saturday 08:10; smsg_login_settimespeed.wowm).
/// </summary>
public static class GameTimePacker
{
    /// <summary>vmangos sends 1/60 game minute per second (Player.cpp:19141-19145).</summary>
    public const float GameSpeedMinutesPerSecond = 1.0f / 60.0f;

    /// <summary>Pack the calendar fields of <paramref name="local"/> (they are read as given, no conversion).</summary>
    public static uint Pack(DateTimeOffset local)
    {
        int weekday = (int)local.DayOfWeek;
        return (uint)(local.Minute
            | (local.Hour << 6)
            | (weekday << 11)
            | ((local.Day - 1) << 14)
            | ((local.Month - 1) << 20)
            | ((local.Year - 2000) << 24));
    }
}
