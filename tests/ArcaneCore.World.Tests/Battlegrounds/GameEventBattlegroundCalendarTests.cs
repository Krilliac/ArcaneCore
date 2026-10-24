using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Battlegrounds;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>The battleground weekend flag follows the Call to Arms game events (vmangos BattleGroundMgr.cpp:1613).</summary>
public sealed class GameEventBattlegroundCalendarTests
{
    private sealed class FakeEvents(params uint[] holidays) : IGameEventState
    {
        public bool IsActiveEvent(ushort eventId) => false;

        public bool IsActiveHoliday(uint holidayId) => holidays.Contains(holidayId);

        public IReadOnlyCollection<ushort> ActiveEvents => [];
    }

    [Theory]
    [InlineData(283u, BattlegroundType.AlteracValley)]
    [InlineData(284u, BattlegroundType.WarsongGulch)]
    [InlineData(285u, BattlegroundType.ArathiBasin)]
    public void EachTypeIsOnlyOnItsOwnCallToArmsHoliday(uint holiday, BattlegroundType on)
    {
        var calendar = new GameEventBattlegroundCalendar(() => new FakeEvents(holiday));
        foreach (BattlegroundType type in new[] { BattlegroundType.AlteracValley, BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin })
        {
            Assert.Equal(type == on, calendar.IsBattlegroundWeekend(type));
        }

        Assert.False(calendar.IsBattlegroundWeekend(BattlegroundType.None));
    }

    [Fact]
    public void NoGameEventStateMeansNoWeekend_AndTheStateIsReadOnEveryCall()
    {
        IGameEventState? state = null;
        var calendar = new GameEventBattlegroundCalendar(() => state);
        Assert.False(calendar.IsBattlegroundWeekend(BattlegroundType.WarsongGulch));

        state = new FakeEvents(284);
        Assert.True(calendar.IsBattlegroundWeekend(BattlegroundType.WarsongGulch));

        state = new FakeEvents();
        Assert.False(calendar.IsBattlegroundWeekend(BattlegroundType.WarsongGulch));
    }

    [Fact]
    public async Task TheWorldHostsBattlegroundPortsUseTheGameEventCalendar()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        IBattlegroundCalendar calendar = await host.OnWorldAsync(
            () => new BattlegroundWorldHost(host.WorldServices.GetRequiredService<BattlegroundFeature>()).BasePorts().Calendar);

        Assert.IsType<GameEventBattlegroundCalendar>(calendar);
    }
}
