using System.Numerics;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.EventScenarios;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Groups;
using ArcaneCore.World.Tests.WorldState;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.EventScenarios;

/// <summary>
/// The scenario feature on the scenario world (real handlers, the manual clock): a serverside game event started with the bots already
/// there, or added afterwards, brings the posts or a siege up, and stopping the event takes them down again.
/// </summary>
public sealed class EventScenarioWorldTests
{
    private const uint EventId = 901;
    private const uint Objective = 990803;
    private static readonly Vector3 Origin = new(-8913f, 550f, StartZ);
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixtureStore(GameEventPlayerbotContent content) : IGameEventPlayerbotStore
    {
        public Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static GameEventPlayerbotGroupRow Group(byte role, int count) => new()
    {
        Event = EventId, GroupId = 1, NamePrefix = "Sg", Count = count, Team = 0, ClassSpec = "1:1", Level = 10, Provision = false,
        StartMap = 0, StartX = Origin.X, StartY = Origin.Y, StartZ = Origin.Z, StartO = 0, Role = role,
    };

    private static async Task<GroupTestWorld> StartAsync(GameEventPlayerbotContent content)
    {
        WorldTestHost.GameTime.Value = new FixedGameTime(Now, TimeZoneInfo.Local);
        GameEventTestStore.Current.Value = new GameEventContent([new GameEventRecord(EventId, 0, 60, 60, 0, 0, "Scenario test")], [], [], [], [], [], []);
        try
        {
            return await GroupTestWorld.StartAsync([], null,
                services: collection => collection.AddScoped<IGameEventPlayerbotStore>(_ => new FixtureStore(content)),
                extraCreatures: [(
                    new CreatureTemplate
                    {
                        Entry = Objective, Name = "Scenario Objective", Faction = 14, CreatureType = 7, MinLevel = 10, MaxLevel = 10, DisplayIds = [903],
                        MinLevelHealth = 120, MaxLevelHealth = 120, MinMeleeDamage = 1, MaxMeleeDamage = 1, MeleeBaseAttackTime = 2000, Rank = 0,
                    },
                    new CreatureSpawn
                    {
                        Guid = Objective, Entry = Objective, MapId = 0, X = Origin.X + 70f, Y = Origin.Y, Z = Origin.Z, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600,
                    })]);
        }
        finally
        {
            GameEventTestStore.Current.Value = null;
            WorldTestHost.GameTime.Value = null;
        }
    }

    private static Task<bool> SetEvent(GroupTestWorld world, bool on)
    {
        GameEventFeature events = world.World.Services.GetRequiredService<GameEventFeature>();
        return world.OnWorldAsync(() => on ? events.Service!.StartEvent((ushort)EventId) : events.Service!.StopEvent((ushort)EventId));
    }

    [Fact]
    public async Task ADefendGroup_StartedBeforeItsBotsExist_PostsThemWhenTheyAppear_AndClearsThePostsWhenTheEventStops()
    {
        var content = new GameEventPlayerbotContent(
            [Group(role: 1, count: 2)],
            [new GameEventPlayerbotPostRow { Event = EventId, GroupId = 1, PostId = 1, Map = 0, X = Origin.X, Y = Origin.Y, Z = Origin.Z, Radius = 40 }],
            [], []);
        await using GroupTestWorld world = await StartAsync(content);
        Assert.True(await SetEvent(world, true));
        await world.RunAsync(6_000); // the event is on and no bot matches yet

        (Guid Id, string Name)[] bots =
        [
            await world.AddBotAsync("Sgaone", 1, 1, 10, []),
            await world.AddBotAsync("Sgbtwo", 1, 1, 10, []),
        ];

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => PlayerbotDefend.PostOf(world.Player(b.Id)) is { PostId: > 0x40000000 })),
            string.Join(Environment.NewLine, world.World.Services.GetRequiredService<PlayerbotEventScenarioFeature>().StatusLines(null)));

        Assert.True(await SetEvent(world, false));
        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => PlayerbotDefend.PostOf(world.Player(b.Id)) is null)));
    }

    [Fact]
    public async Task ASiegeGroup_StartsTheSiegeWhenTheEventStarts_AndStopsItWithTheEvent()
    {
        var content = new GameEventPlayerbotContent(
            [Group(role: 2, count: 6)],
            [],
            [new GameEventPlayerbotSiegeRow { Event = EventId, GroupId = 1, ObjectiveEntry = Objective, Raids = 2, MinReadyPct = 100 }],
            [
                new GameEventPlayerbotWaypointRow { Event = EventId, GroupId = 1, Seq = 1, Map = 0, X = Origin.X + 10f, Y = Origin.Y, Z = Origin.Z },
                new GameEventPlayerbotWaypointRow { Event = EventId, GroupId = 1, Seq = 2, Map = 0, X = Origin.X + 20f, Y = Origin.Y, Z = Origin.Z },
            ]);
        await using GroupTestWorld world = await StartAsync(content);
        (Guid, string)[] bots =
        [
            await world.AddBotAsync("Sgatank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgbtank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgaheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgbheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgamage", 1, 8, 10, []),
            await world.AddBotAsync("Sgbmage", 1, 8, 10, []),
        ];
        Assert.Equal(6, bots.Length);
        await world.RunAsync(2_000);
        Assert.Null(await world.OnWorldAsync(() => world.Coordinator.Siege)); // nothing before the event

        Assert.True(await SetEvent(world, true));
        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.Siege is { Raids.Count: 2 }),
            string.Join(Environment.NewLine, world.World.Services.GetRequiredService<PlayerbotEventScenarioFeature>().StatusLines(null)));

        Assert.True(await SetEvent(world, false));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Siege is null));
        Assert.Contains("event stopped", await world.OnWorldAsync(() => world.Coordinator.SiegeOutcome ?? string.Empty), StringComparison.Ordinal);
    }
}
