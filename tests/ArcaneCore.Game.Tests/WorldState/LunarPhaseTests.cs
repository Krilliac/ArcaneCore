using ArcaneCore.Game.WorldState.Events;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// The lunar new year: the oracle is the real calendar (published new-moon times and Chinese new year dates), not a second
/// port of the same code.
/// </summary>
public sealed class LunarPhaseTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 2000, 1, 6, 18, 14)]
    [InlineData(308, 2024, 12, 1, 6, 21)]
    public void NewMoon_MatchesPublishedTimes_WithinTenMinutes(int k, int y, int mo, int d, int h, int mi)
    {
        // k = 0 is the new moon of 2000-01-06 18:14 UT; k = 308 is 2024-12-01 06:21 UT
        TimeSpan error = (LunarPhase.NewMoon(k) - Utc(y, mo, d, h, mi)).Duration();
        Assert.True(error < TimeSpan.FromMinutes(10), $"k={k}: off by {error}");
    }

    [Theory]
    [InlineData(2024, 2, 9, 22, 59)]
    [InlineData(2025, 1, 29, 12, 36)]
    [InlineData(2026, 2, 17, 12, 1)]
    [InlineData(2027, 2, 6, 15, 56)]
    public void NextNewMoon_FindsTheNewMoonOfTheLunarNewYear(int year, int mo, int d, int h, int mi)
    {
        // The first new moon on or after January 20th of those years is the Chinese new year's.
        DateTimeOffset moon = LunarPhase.NextNewMoon(Utc(year, 1, 20));
        TimeSpan error = (moon - Utc(year, mo, d, h, mi)).Duration();
        Assert.True(error < TimeSpan.FromMinutes(10), $"{year}: {moon:u} is off by {error}");
    }

    public static TheoryData<int, int, int> KnownNewYears => new()
    {
        { 2000, 2, 5 }, { 2001, 1, 24 }, { 2002, 2, 12 }, { 2003, 2, 1 }, { 2004, 1, 22 }, { 2005, 2, 9 }, { 2006, 1, 29 },
        { 2007, 2, 18 }, { 2008, 2, 7 }, { 2009, 1, 26 }, { 2010, 2, 14 }, { 2011, 2, 3 }, { 2012, 1, 23 }, { 2013, 2, 10 },
        { 2014, 1, 31 }, { 2015, 2, 19 }, { 2016, 2, 8 }, { 2017, 1, 28 }, { 2018, 2, 16 }, { 2019, 2, 5 }, { 2020, 1, 25 },
        { 2021, 2, 12 }, { 2022, 2, 1 }, { 2023, 1, 22 }, { 2024, 2, 10 }, { 2025, 1, 29 }, { 2026, 2, 17 }, { 2027, 2, 6 },
        { 2028, 1, 26 }, { 2029, 2, 13 }, { 2030, 2, 3 }, { 2031, 1, 23 }, { 2032, 2, 11 }, { 2033, 1, 31 }, { 2034, 2, 19 },
        { 2035, 2, 8 }, { 2036, 1, 28 }, { 2037, 2, 15 }, { 2038, 2, 4 }, { 2039, 1, 24 }, { 2040, 2, 12 },
    };

    [Theory]
    [MemberData(nameof(KnownNewYears))]
    public void LunarNewYear_IsTheRealChineseNewYearDate(int year, int month, int day)
        => Assert.Equal(new DateOnly(year, month, day), LunarPhase.LunarNewYear(year));

    [Fact]
    public void NewMoons_AreOneSynodicMonthApart()
    {
        for (int k = -50; k < 50; k++)
        {
            double days = (LunarPhase.NewMoon(k + 1) - LunarPhase.NewMoon(k)).TotalDays;
            Assert.InRange(days, 29.2, 29.9); // the true spread of a lunation is about 29.27 to 29.83 days
        }
    }
}
