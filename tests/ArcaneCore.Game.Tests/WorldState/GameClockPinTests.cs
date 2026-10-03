using ArcaneCore.Game.WorldState.Time;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>Characterization of the packed game time around a daylight-saving change: the wall-clock fields are packed, so an hour that does not exist never appears.</summary>
public sealed class GameClockPinTests
{
    private static (int Hour, int Minute) Fields(uint packed) => ((int)((packed >> 6) & 0x1F), (int)(packed & 0x3F));

    [Fact]
    public void Pack_AroundTheSpringForwardEdge_SkipsTheMissingHour()
    {
        // GameEventCalendarTests.DstZone: UTC+1, +2 from March 25th 02:00 local to October 25th 03:00 local
        var before = new FixedGameTime(new DateTimeOffset(2026, 3, 25, 0, 59, 0, TimeSpan.Zero), GameEventCalendarTests.DstZone);
        var after = new FixedGameTime(new DateTimeOffset(2026, 3, 25, 1, 0, 0, TimeSpan.Zero), GameEventCalendarTests.DstZone);

        Assert.Equal((1, 59), Fields(GameTimePacker.Pack(before.LocalNow())));
        Assert.Equal((3, 0), Fields(GameTimePacker.Pack(after.LocalNow())));
    }

    [Fact]
    public void Pack_AroundTheFallBackEdge_RepeatsTheHourOnTheWallClock()
    {
        var first = new FixedGameTime(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), GameEventCalendarTests.DstZone); // 02:30 at +2
        var second = new FixedGameTime(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), GameEventCalendarTests.DstZone); // 02:30 at +1

        Assert.Equal((2, 30), Fields(GameTimePacker.Pack(first.LocalNow())));
        Assert.Equal((2, 30), Fields(GameTimePacker.Pack(second.LocalNow())));
    }
}
