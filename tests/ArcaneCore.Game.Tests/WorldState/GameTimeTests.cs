using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>The login game time: vmangos packs the server's LOCAL time (Misc.cpp:924-933).</summary>
public sealed class GameTimeTests
{
    [Fact]
    public void Pack_MatchesTheGtkerVector()
    {
        // wow_messages smsg_login_settimespeed.wowm: 2022-08-13 (a Saturday) 08:10 == 0x1673320A.
        Assert.Equal(0x1673320Au, GameTimePacker.Pack(new DateTimeOffset(2022, 8, 13, 8, 10, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Pack_FieldsAreMinuteHourWeekdayDayMonthYear()
    {
        // Sunday 2000-01-02 00:00: weekday 0, day-1 = 1, month 0, year 0
        Assert.Equal(1u << 14, GameTimePacker.Pack(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        // the fields are read as given (no conversion to UTC)
        Assert.Equal(GameTimePacker.Pack(new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero)),
            GameTimePacker.Pack(new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.FromHours(9))));
    }

    private static readonly TimeZoneInfo UtcMinus7 = TimeZoneInfo.CreateCustomTimeZone("test-7", TimeSpan.FromHours(-7), "test", "test");

    [Fact]
    public void LocalNow_UsesTheServerZone_ByDefault_AndUtcWhenConfigured()
    {
        WorldRuntime_Hooks(out WorldStateHooks hooks);
        hooks.Time = new FixedGameTime(new DateTimeOffset(2022, 8, 13, 8, 10, 0, TimeSpan.Zero), UtcMinus7);

        Assert.True(hooks.TimeSettings.UseServerLocalTime);
        Assert.Equal(1, hooks.LocalNow().Hour);               // 08:10 UTC is 01:10 at UTC-7
        Assert.Equal(GameTimePacker.Pack(new DateTimeOffset(2022, 8, 13, 1, 10, 0, TimeSpan.FromHours(-7))), GameTimePacker.Pack(hooks.LocalNow()));

        hooks.TimeSettings.UseServerLocalTime = false;
        Assert.Equal(8, hooks.LocalNow().Hour);
    }

    [Fact]
    public void LocalNow_HonoursAnExplicitZoneId()
    {
        WorldRuntime_Hooks(out WorldStateHooks hooks);
        hooks.Time = new FixedGameTime(new DateTimeOffset(2022, 1, 1, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        string? id = new[] { "Pacific Standard Time", "America/Los_Angeles" }.FirstOrDefault(i => TimeZoneInfo.TryFindSystemTimeZoneById(i, out _));
        Assert.NotNull(id);
        hooks.TimeSettings.TimeZoneId = id;
        Assert.Equal(4, hooks.LocalNow().Hour); // PST in January

        hooks.TimeSettings.UseServerLocalTime = false; // the id is only read while local time is used
        Assert.Equal(12, hooks.LocalNow().Hour);
    }

    private static void WorldRuntime_Hooks(out WorldStateHooks hooks)
        => hooks = WorldStateHooks.For(TestWorld.CreateRuntime());
}
