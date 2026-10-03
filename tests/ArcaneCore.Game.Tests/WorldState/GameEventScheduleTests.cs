using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>vmangos GameEventMgr::CheckOneGameEvent / NextCheck and the yearly leap-day adjustment (GameEventMgr.cpp:38-70, :241-251).</summary>
public sealed class GameEventScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    // A midsummer-style yearly holiday: starts 2020-06-21 20:00 UTC, recurs every 365 days (525600 min), lasts 14 days.
    private static readonly GameEventDefinition Yearly = new(1, Utc(2020, 6, 21, 20), Utc(2100, 1, 1), 525600, 20160, 341, "Midsummer");

    // A plain recurring event: starts 2024-01-01 00:00, every 3 days, 1 day long, ends 2024-02-01.
    private static readonly GameEventDefinition Short = new(2, Utc(2024, 1, 1), Utc(2024, 2, 1), 4320, 1440, 0, "short");

    [Fact]
    public void DateStable_YearlyEvent_StartsOnItsCalendarDate_InEveryYear()
    {
        // 2026 (vmangos' literal loop says INACTIVE at 20:01 on the anniversary, see the literal test below)
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2026, 6, 21, 19, 59)));
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2026, 6, 21, 20, 1)));
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2026, 7, 4, 20, 0)));   // 13 days in
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2026, 7, 5, 20, 1)));  // 14 days + 1 minute: over

        // 2021: no drift after the start year; 2025: one Feb 29th (2024) is accounted for
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2021, 6, 21, 20, 1)));
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2021, 6, 21, 19, 59)));
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2025, 6, 21, 20, 1)));
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2025, 6, 21, 19, 59)));
        // the leap year itself, before and after its Feb 29th
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2024, 6, 21, 20, 1)));
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2024, 6, 21, 19, 59)));
    }

    [Fact]
    public void LeapDays_CountOnlyFebruary29thsBetweenStartAndNow_InDateStableMode()
    {
        Assert.Equal(0, GameEventSchedule.LeapDays(Yearly, Utc(2021, 6, 21), LeapDayMode.DateStable));  // 2020's Feb 29th precedes the start
        Assert.Equal(0, GameEventSchedule.LeapDays(Yearly, Utc(2024, 2, 29), LeapDayMode.DateStable));  // not yet reached (< now)
        Assert.Equal(1, GameEventSchedule.LeapDays(Yearly, Utc(2024, 3, 1), LeapDayMode.DateStable));
        Assert.Equal(1, GameEventSchedule.LeapDays(Yearly, Utc(2026, 6, 21), LeapDayMode.DateStable));
        Assert.Equal(2, GameEventSchedule.LeapDays(Yearly, Utc(2028, 3, 1), LeapDayMode.DateStable));
        // a start before the Feb 29th of its own leap year does count that one
        var early = Yearly with { Start = Utc(2020, 1, 10) };
        Assert.Equal(1, GameEventSchedule.LeapDays(early, Utc(2020, 6, 1), LeapDayMode.DateStable));
        // only 365-day events shorter than a year are adjusted
        Assert.Equal(0, GameEventSchedule.LeapDays(Short, Utc(2024, 3, 1), LeapDayMode.DateStable));
        Assert.Equal(0, GameEventSchedule.LeapDays(Yearly with { LengthMinutes = 525600 }, Utc(2026, 6, 21), LeapDayMode.DateStable));
    }

    [Fact]
    public void VmangosLiteral_CountsTheStartYearsLeapDay_SoTheEventBeginsADayLate()
    {
        // The literal loop counts one leap day per leap YEAR in [2020, current): 2020 although its Feb 29th
        // precedes the 2020-06-21 start (worked by hand from GameEventMgr.cpp:241-251 and :38-44, not run against retail).
        Assert.Equal(1, GameEventSchedule.LeapDays(Yearly, Utc(2021, 6, 21), LeapDayMode.VmangosLiteral));
        Assert.Equal(2, GameEventSchedule.LeapDays(Yearly, Utc(2026, 6, 21), LeapDayMode.VmangosLiteral));
        Assert.Equal(0, GameEventSchedule.LeapDays(Yearly, Utc(2020, 12, 31), LeapDayMode.VmangosLiteral)); // current year not counted

        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2026, 6, 21, 20, 1), LeapDayMode.VmangosLiteral));
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2026, 6, 22, 20, 1), LeapDayMode.VmangosLiteral));
        Assert.False(GameEventSchedule.IsActive(Yearly, Utc(2021, 6, 21, 20, 1), LeapDayMode.VmangosLiteral));
        // 2024 after its Feb 29th happens to agree with the date-stable answer (2020 counted instead of 2024)
        Assert.True(GameEventSchedule.IsActive(Yearly, Utc(2024, 6, 21, 20, 1), LeapDayMode.VmangosLiteral));
    }

    [Fact]
    public void Active_IsInsideTheWindow_AndInsideStartAndEnd()
    {
        Assert.False(GameEventSchedule.IsActive(Short, Utc(2023, 12, 31, 23, 59, 59)));  // before the start
        Assert.True(GameEventSchedule.IsActive(Short, Utc(2024, 1, 1)));
        Assert.True(GameEventSchedule.IsActive(Short, Utc(2024, 1, 1, 23, 59, 59)));
        Assert.False(GameEventSchedule.IsActive(Short, Utc(2024, 1, 2)));                // 1 day long
        Assert.True(GameEventSchedule.IsActive(Short, Utc(2024, 1, 4)));                 // every 3 days
        Assert.False(GameEventSchedule.IsActive(Short, Utc(2024, 2, 1)));                // end is exclusive
        Assert.False(GameEventSchedule.IsActive(Short, Utc(2025, 1, 1)));
    }

    [Fact]
    public void NextCheck_FollowsTheVmangosBranches()
    {
        // before the start: the time until it
        Assert.Equal(3600u, GameEventSchedule.NextCheckSeconds(Short, Utc(2023, 12, 31, 23)));
        // inside an occurrence: the time until it ends
        Assert.Equal(86400u - 3600, GameEventSchedule.NextCheckSeconds(Short, Utc(2024, 1, 1, 1)));
        // between occurrences: the time until the next one
        Assert.Equal(86400u + (86400 - 3600), GameEventSchedule.NextCheckSeconds(Short, Utc(2024, 1, 2, 1)) - 0u);
        // clipped at the event's end
        Assert.Equal(3600u, GameEventSchedule.NextCheckSeconds(Short with { End = Utc(2024, 1, 2, 2) }, Utc(2024, 1, 2, 1)));
        // outdated: one day
        Assert.Equal(GameEventSchedule.MaxCheckDelaySeconds, GameEventSchedule.NextCheckSeconds(Short, Utc(2025, 1, 1)));
    }

    [Fact]
    public void Validity_ALengthOfZeroIsInvalid_AnOccurenceOfZeroNeverRuns()
    {
        Assert.True(Short.IsValid);
        Assert.False((Short with { LengthMinutes = 0 }).IsValid);

        GameEventDefinition broken = Short with { OccurenceMinutes = 0 }; // vmangos would divide by zero
        Assert.False(GameEventSchedule.IsActive(broken, Utc(2024, 1, 1, 12)));
        Assert.Equal(GameEventSchedule.MaxCheckDelaySeconds, GameEventSchedule.NextCheckSeconds(broken, Utc(2024, 1, 1, 12)));
    }

    [Fact]
    public void TheLeapDayModeDefaultsToDateStable()
        => Assert.Equal(LeapDayMode.DateStable, new GameEventOptions().LeapDayMode);
}
