using ArcaneCore.Game.WorldState.Events;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// The computed schedules of the mangos-classic event dialect (GameEventMgr.cpp:1162-1324): Easter, yearly rebasing, the
/// lunar new year, and reading a zone-less date. All time is an argument; nothing reads the clock.
/// </summary>
public sealed class GameEventCalendarTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    /// <summary>UTC+1 with daylight saving (+2) from March 25th 02:00 to October 25th 03:00, fixed dates so the tests do not depend on a machine zone database.</summary>
    internal static readonly TimeZoneInfo DstZone = TimeZoneInfo.CreateCustomTimeZone(
        "test-dst",
        TimeSpan.FromHours(1),
        "test dst",
        "test standard",
        "test daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 25),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25)),
        ]);

    /// <summary>The anonymous Gregorian algorithm (Meeus/Jones/Butcher): the true Easter, an oracle independent of the Gauss port.</summary>
    private static (int Month, int Day) TrueEaster(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31, day = (((h + l - (7 * m) + 114) % 31) + 1);
        return (month, day);
    }

    [Fact]
    public void GaussEaster_MatchesTheTrueEaster_For2006To2099()
    {
        for (int year = 2006; year <= 2099; year++)
        {
            Assert.True(TrueEaster(year) == GameEventCalendar.GaussEaster(year), $"Easter {year}: expected {TrueEaster(year)}, got {GameEventCalendar.GaussEaster(year)}");
        }
    }

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2049, 4, 18)] // the "D 28, E 6" corner case
    [InlineData(2076, 4, 19)] // the "D 29, E 6" corner case
    public void GaussEaster_KnownDates_IncludingTheTwoCornerCases(int year, int month, int day)
        => Assert.Equal((month, day), GameEventCalendar.GaussEaster(year));

    [Fact]
    public void GaussEaster_IsOneDayOffIn2100_AsTheOriginalIs()
    {
        // The Gauss formula without the century corrections used here is right until 2099; 2100 is a known miss.
        Assert.NotEqual(TrueEaster(2100), GameEventCalendar.GaussEaster(2100));
    }

    [Fact]
    public void Easter_StartsAtLocalMidnightOfEasterSunday_AndLastsSevenDays()
    {
        var easter = new GameEventDefinition(9, Utc(1, 1, 1), Utc(1, 1, 1), 524160, 7200, 181, "Noblegarden") { ScheduleType = GameEventScheduleType.Easter };
        (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(easter, Utc(2026, 10, 3), TimeZoneInfo.Utc);
        Assert.Equal(Utc(2026, 4, 5), start);
        Assert.Equal(Utc(2026, 4, 12), end);

        // in a zone the date is the local midnight: Easter 2026 is April 5th, in DST (+2) that midnight is 22:00 UTC of the 4th
        (DateTimeOffset zonedStart, DateTimeOffset zonedEnd) = GameEventCalendar.Compute(easter, Utc(2026, 10, 3), DstZone);
        Assert.Equal(Utc(2026, 4, 4, 22), zonedStart);
        Assert.Equal(Utc(2026, 4, 11, 22), zonedEnd);
    }

    [Fact]
    public void Easter_TakesItsYearFromTheUtcClock()
    {
        var easter = new GameEventDefinition(9, Utc(1, 1, 1), Utc(1, 1, 1), 524160, 7200, 181, "Noblegarden") { ScheduleType = GameEventScheduleType.Easter };
        // 2027-01-01 00:30 in UTC+1 is still 2026-12-31 23:30 UTC: gmtime says 2026
        (DateTimeOffset start, _) = GameEventCalendar.Compute(easter, Utc(2026, 12, 31, 23, 30), TimeZoneInfo.Utc);
        Assert.Equal(2026, start.Year);
        (DateTimeOffset next, _) = GameEventCalendar.Compute(easter, Utc(2027, 1, 1, 0, 30), TimeZoneInfo.Utc);
        Assert.Equal(2027, next.Year);
    }

    // Feast of Winter Veil in the data: starts Dec 16 23:00 (2020), table end 2030-12-31 22:59:59, 27360 minutes (19 days).
    private static GameEventDefinition WinterVeil { get; } = new(2, Utc(2020, 12, 16, 23), Utc(2030, 12, 31, 22, 59, 59), 525600, 27360, 141, "Feast of Winter Veil")
    {
        ScheduleType = GameEventScheduleType.Yearly,
    };

    [Fact]
    public void Yearly_MovesStartAndEndToTheCurrentYear()
    {
        (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(WinterVeil, Utc(2026, 10, 3), TimeZoneInfo.Utc);
        Assert.Equal(Utc(2026, 12, 16, 23), start);
        // the table's end (Dec 31st 22:59:59) is before the window's own end: the window is not cut
        Assert.Equal(Utc(2027, 1, 4, 23), end); // 19 days after Dec 16 23:00
    }

    [Fact]
    public void Yearly_MangosLiteral_CutsTheWindowAtTheTablesEndDate()
    {
        (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(WinterVeil, Utc(2026, 10, 3), TimeZoneInfo.Utc, YearlyRebaseMode.MangosLiteral);
        Assert.Equal(Utc(2026, 12, 16, 23), start);
        Assert.Equal(Utc(2026, 12, 31, 22, 59, 59), end);
    }

    [Fact]
    public void Yearly_InJanuary_SpanNewYear_UsesLastYearsWindowWhileItIsOpen()
    {
        // the year rolls over on a FixedGameTime-style instant: Dec 31 and Jan 2 are the same holiday
        (DateTimeOffset dStart, DateTimeOffset dEnd) = GameEventCalendar.Compute(WinterVeil, Utc(2026, 12, 31, 12), TimeZoneInfo.Utc);
        (DateTimeOffset jStart, DateTimeOffset jEnd) = GameEventCalendar.Compute(WinterVeil, Utc(2027, 1, 2, 12), TimeZoneInfo.Utc);
        Assert.Equal((Utc(2026, 12, 16, 23), Utc(2027, 1, 4, 23)), (dStart, dEnd));
        Assert.Equal((dStart, dEnd), (jStart, jEnd));
        Assert.True(GameEventSchedule.IsActive(WinterVeil with { Start = jStart, End = jEnd }, Utc(2027, 1, 2, 12)));

        // after the window it moves on to next December
        (DateTimeOffset nStart, DateTimeOffset nEnd) = GameEventCalendar.Compute(WinterVeil, Utc(2027, 1, 5), TimeZoneInfo.Utc);
        Assert.Equal(Utc(2027, 12, 16, 23), nStart);
        Assert.Equal(Utc(2028, 1, 4, 23), nEnd);
    }

    [Fact]
    public void Yearly_InJanuary_MangosLiteral_HasNoWindowYet()
    {
        (DateTimeOffset start, _) = GameEventCalendar.Compute(WinterVeil, Utc(2027, 1, 2, 12), TimeZoneInfo.Utc, YearlyRebaseMode.MangosLiteral);
        Assert.Equal(Utc(2027, 12, 16, 23), start);
    }

    [Fact]
    public void Yearly_February29thStart_BecomesMarch1stInACommonYear()
    {
        var leapDay = new GameEventDefinition(5, Utc(2020, 2, 29, 8), Utc(2030, 12, 31), 525600, 1440, 0, "leap") { ScheduleType = GameEventScheduleType.Yearly };
        Assert.Equal(Utc(2026, 3, 1, 8), GameEventCalendar.Compute(leapDay, Utc(2026, 6, 1), TimeZoneInfo.Utc).Start);
        Assert.Equal(Utc(2028, 2, 29, 8), GameEventCalendar.Compute(leapDay, Utc(2028, 6, 1), TimeZoneInfo.Utc).Start);
    }

    [Fact]
    public void Yearly_KeepsTheLocalTimeOfDay_AcrossDaylightSaving()
    {
        // Midsummer in the zone: 2020-06-21 20:00 local (+2 in summer) = 18:00 UTC; the same wall time in winter is +1
        var event1 = new GameEventDefinition(1, Utc(2020, 6, 21, 18), Utc(2030, 12, 31), 525600, 20160, 341, "Midsummer") { ScheduleType = GameEventScheduleType.Yearly };
        Assert.Equal(Utc(2026, 6, 21, 18), GameEventCalendar.Compute(event1, Utc(2026, 1, 10), DstZone).Start);
        var winter = new GameEventDefinition(8, Utc(2020, 2, 8, 21), Utc(2030, 12, 31), 525600, 5760, 335, "Love") { ScheduleType = GameEventScheduleType.Yearly };
        Assert.Equal(Utc(2026, 2, 8, 21), GameEventCalendar.Compute(winter, Utc(2026, 1, 10), DstZone).Start);
    }

    [Fact]
    public void LunarNewYear_StartsOnTheChineseNewYear_AndLastsTwentyOneDays()
    {
        var lunar = new GameEventDefinition(7, Utc(1, 1, 1), Utc(1, 1, 1), 525600, 28800, 327, "Lunar Festival") { ScheduleType = GameEventScheduleType.LunarNewYear };
        (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(lunar, Utc(2026, 10, 3), TimeZoneInfo.Utc);
        Assert.Equal(Utc(2026, 2, 17), start);
        Assert.Equal(Utc(2026, 3, 10), end);
        // the year comes from the local calendar: in UTC+1 the first minutes of 2027 already belong to 2027
        Assert.Equal(Utc(2027, 2, 6).AddHours(-1), GameEventCalendar.Compute(lunar, Utc(2026, 12, 31, 23, 30), DstZone).Start);
    }

    [Fact]
    public void Serverside_AndDateEvents_KeepTheirTimes()
    {
        var date = new GameEventDefinition(400, Utc(2006, 1, 1, 7), Utc(2035, 12, 30, 20), 1440, 780, 0, "DayTime");
        Assert.Equal((date.Start, date.End), GameEventCalendar.Compute(date, Utc(2026, 10, 3), TimeZoneInfo.Utc));
    }

    [Fact]
    public void DarkmoonSchedules_AreNotSupported_AndSaySo()
    {
        var dmf = new GameEventDefinition(21, Utc(2020, 1, 1), Utc(2030, 1, 1), 86400, 10080, 0, "DMF") { ScheduleType = GameEventScheduleType.DarkmoonFaire1 };
        Assert.False(GameEventCalendar.IsSupported(GameEventScheduleType.DarkmoonFaire1));
        Assert.Throws<NotSupportedException>(() => GameEventCalendar.Compute(dmf, Utc(2026, 10, 3), TimeZoneInfo.Utc));
        Assert.All(
            new[] { GameEventScheduleType.Serverside, GameEventScheduleType.Date, GameEventScheduleType.Yearly, GameEventScheduleType.LunarNewYear, GameEventScheduleType.Easter },
            t => Assert.True(GameEventCalendar.IsSupported(t)));
    }

    [Fact]
    public void ReadWallTime_WallAndStandardTime_DifferByExactlyOneHourInDaylightSaving()
    {
        var summer = new DateTime(2026, 7, 1, 7, 0, 0, DateTimeKind.Unspecified);
        DateTimeOffset wall = GameEventCalendar.ReadWallTime(summer, DstZone, GameEventDateTimeInterpretation.Wall);
        DateTimeOffset standard = GameEventCalendar.ReadWallTime(summer, DstZone, GameEventDateTimeInterpretation.StandardTime);
        Assert.Equal(Utc(2026, 7, 1, 5), wall);      // 07:00 at +2
        Assert.Equal(Utc(2026, 7, 1, 6), standard);  // 07:00 at +1, no daylight saving
        Assert.Equal(TimeSpan.FromHours(1), standard - wall);

        // in winter they agree
        var winter = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(
            GameEventCalendar.ReadWallTime(winter, DstZone, GameEventDateTimeInterpretation.Wall),
            GameEventCalendar.ReadWallTime(winter, DstZone, GameEventDateTimeInterpretation.StandardTime));
    }

    [Fact]
    public void LocalToInstant_MovesAForwardGapTimeIntoTheHourAfterIt()
    {
        // 2026-03-25 02:30 does not exist in the zone (02:00 -> 03:00): it becomes 03:30 local = 01:30 UTC
        Assert.Equal(Utc(2026, 3, 25, 1, 30), GameEventCalendar.LocalToInstant(new DateTime(2026, 3, 25, 2, 30, 0), DstZone));
    }

    [Fact]
    public void DailyEvent_FollowsElapsedTimeFromItsStart_SoItShiftsAnHourWithDaylightSaving()
    {
        // event 400 shape (DayTime 7AM to 8PM): start 2006-01-01 07:00 local, every 1440 minutes, 780 long
        DateTimeOffset start = GameEventCalendar.ReadWallTime(new DateTime(2006, 1, 1, 7, 0, 0), DstZone, GameEventDateTimeInterpretation.Wall);
        DateTimeOffset end = GameEventCalendar.ReadWallTime(new DateTime(2035, 12, 30, 20, 0, 0), DstZone, GameEventDateTimeInterpretation.Wall);
        var day = new GameEventDefinition(400, start, end, 1440, 780, 0, "DayTime 7AM to 8PM");

        // Winter (+1): 07:00 local = 06:00 UTC, 20:00 local = 19:00 UTC.
        Assert.True(GameEventSchedule.IsActive(day, Utc(2026, 1, 10, 6, 0, 1)));
        Assert.True(GameEventSchedule.IsActive(day, Utc(2026, 1, 10, 18, 59)));
        Assert.False(GameEventSchedule.IsActive(day, Utc(2026, 1, 10, 19, 1)));
        Assert.False(GameEventSchedule.IsActive(day, Utc(2026, 1, 10, 5, 59)));

        // The recurrence counts elapsed seconds from the start (both references), so in July (+2) the same window is
        // 08:00 to 21:00 local: it does not follow the wall clock across a daylight-saving change.
        Assert.True(GameEventSchedule.IsActive(day, Utc(2026, 7, 10, 6, 0, 1)));
        Assert.False(GameEventSchedule.IsActive(day, Utc(2026, 7, 10, 5, 59)));
        Assert.False(GameEventSchedule.IsActive(day, Utc(2026, 7, 10, 19, 1)));
    }
}
