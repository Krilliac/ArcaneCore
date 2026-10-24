using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.WorldState;
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

    /// <summary>Event 19 (Call to Arms: Warsong Gulch, holiday 284) runs 12:00 to 14:00 every day.</summary>
    private static GameEventContent CallToArmsWarsong()
        => new(
            [new GameEventRecord(19, 1, 1440, 120, 284, 0, "Call to Arms: Warsong Gulch")],
            [new GameEventTimeRecord(19, "2026-10-03 12:00:00", "2030-12-31 22:59:59")],
            [], [], [], [], []);

    private static async Task<int> NewWarsongCaptureReputationAsync(WorldTestHost host)
        => await host.OnWorldAsync(() =>
        {
            BattlegroundManager manager = host.WorldServices.GetRequiredService<BattlegroundFeature>().Manager;
            if (manager.TemplateOf(BattlegroundType.WarsongGulch) is null)
            {
                // The test world has no graveyard rows, so the feature registered no built-in template.
                manager.RegisterTemplate(new BattlegroundTemplate
                {
                    Type = BattlegroundType.WarsongGulch,
                    MapId = BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch),
                    Name = "Warsong Gulch",
                    MinPlayersPerTeam = 5,
                    MaxPlayersPerTeam = 10,
                    MinLevel = 10,
                    MaxLevel = 60,
                    AllianceStart = new BattlegroundStartLocation(1f, 2f, 3f, 0f),
                    HordeStart = new BattlegroundStartLocation(4f, 5f, 6f, 0f),
                });
            }

            Battleground bg = manager.CreateNewBattleground(BattlegroundType.WarsongGulch, 0)
                ?? throw new InvalidOperationException("no Warsong Gulch template");
            return Assert.IsType<WarsongGulch>(bg).CaptureReputation;
        });

    [Fact]
    public async Task ALiveCallToArmsEvent_GivesANewWarsongMatchTheWeekendValues_AndWithoutItTheNormalOnes()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        var clock = new FixedGameTime(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero));
        await host.OnWorldAsync(() =>
        {
            WorldStateHooks.For(host.World).Time = clock;
            events.UseContent(CallToArmsWarsong(), new HashSet<ushort>());
        });

        // 15:00: the event is over, so a new match gets the normal values.
        await Task.Delay(300);
        Assert.False(await host.OnWorldAsync(() => events.IsActiveHoliday(284)));
        Assert.Equal(35, await NewWarsongCaptureReputationAsync(host));

        // 12:30: the event runs, so the next new match gets the weekend values.
        clock.UtcNow = new DateTimeOffset(2026, 10, 3, 12, 30, 0, TimeSpan.Zero);
        await host.OnWorldAsync(() => events.UseContent(CallToArmsWarsong(), new HashSet<ushort>()));
        await host.WaitForWorldAsync(() => events.IsActiveHoliday(284), "the Call to Arms event to start");
        Assert.Equal(45, await NewWarsongCaptureReputationAsync(host));
    }
}
