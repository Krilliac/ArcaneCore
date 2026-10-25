using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>Same-map area-trigger legs (<see cref="TravelMeans.AreaTrigger"/>): the planner's edge and the journey's driver.</summary>
public sealed class TravelAreaTriggerTests
{
    private static TravelPlace P(float x, float y, float z, uint map = 1) => new(map, new Vector3(x, y, z));

    /// <summary>
    /// Teldrassil: from the tree top, trigger 527 (map 1, 9947.5 2630 1318.6) leads to Rut'theran (8785.8 967 30.2), where the boat 176244
    /// sails to Auberdine; the top does not walk down, and Rut'theran does not walk to Darkshore.
    /// </summary>
    [Fact]
    public void FromTheTreeTop_ToADarkshoreGoal_IsWalkTriggerWalkBoatWalk()
    {
        TravelPlace top = P(9053.6f, 1599f, 1335.4f), goal = P(6500f, 800f, 5f);
        TravelPlace trigger527 = P(9947.5f, 2630f, 1318.6f), rutheran = P(8785.8f, 967f, 30.2f);
        TravelPlace trigger542 = P(8799.4f, 969.8f, 30.2f), treeTop = P(9946.3f, 2613f, 1316.5f);
        TravelPlace rutheranPort = P(8800f, 985f, 0f), auberdinePort = P(6406.2f, 823.1f, 0f);
        var facts = new TravelFacts
        {
            Alliance = true, Level = 60, Money = 100_000, SpendCap = 10_000,
            Ships = [new ShipRoute(176244, TravelMeans.Boat, TravelAccess.Alliance, rutheranPort, auberdinePort, 300f)],
            Triggers = [new TravelTrigger(527, trigger527, rutheran), new TravelTrigger(542, trigger542, treeTop)],
            Walkable = (a, b) => (a.Position.X > 7500f) == (b.Position.X > 7500f) && (a.Position.Z > 500f) == (b.Position.Z > 500f),
        };
        TravelRoute? route = PlayerbotTravelPlanner.Plan(facts, top, goal);
        Assert.NotNull(route);
        Assert.Equal([TravelMeans.Walk, TravelMeans.AreaTrigger, TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], route.Legs.Select(l => l.Means));
        Assert.Equal(527u, route.Legs[1].TriggerId);
        Assert.Equal(trigger527, route.Legs[1].From);
        Assert.Equal(rutheran, route.Legs[1].To);
        Assert.Equal(176244u, route.Legs[3].TransportEntry);
        Assert.Contains("AreaTrigger 527", PlayerbotTravel.Describe(route));
    }

    private const uint Trigger = 527;

    /// <summary>A synthetic same-map trigger 527 on map 0, 60 yards east of the bot's start, leading 3 000 yards east.</summary>
    private sealed class TriggerMaps(Vector3 from, Vector3 to) : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(InMemoryMapDataStore.Content with
        {
            AreaTriggers = [.. InMemoryMapDataStore.Content.AreaTriggers, new AreaTriggerTemplate(Trigger, 0, from.X, from.Y, from.Z, 8f, 0, 0, 0, 0, "Test same-map trigger")],
            AreaTriggerTeleports = [.. InMemoryMapDataStore.Content.AreaTriggerTeleports, new AreaTriggerTeleport(Trigger, "Test same-map trigger", "", 0, 0, to.X, to.Y, to.Z, 0f)],
        });
    }

    /// <summary>
    /// A journey whose walk is 3 000 yards takes the same-map trigger 60 yards away: the bot walks into its volume, the server's teleport
    /// carries it, and the leg is done when it stands within 30 yards of the target; the walk after it ends at the goal.
    /// </summary>
    [Fact]
    public async Task AJourneyTakesASameMapTrigger_AndArrivesBeyondIt()
    {
        Vector3 start = ShipTestWorld.Start;
        var from = new Vector3(start.X + 60f, start.Y, start.Z);
        var to = new Vector3(start.X + 3000f, start.Y, start.Z);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IMapDataStore>();
            services.AddSingleton<IMapDataStore>(new TriggerMaps(from, to));
        });
        await world.PlaceAsync(0, start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        try
        {
            var goal = new Vector3(to.X + 40f, to.Y, to.Z);
            await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 0, goal, out string? why), why);
                Assert.Contains(TravelMeans.AreaTrigger, PlayerbotTravel.JourneyOf(world.Player)!.Planned.Legs.Select(l => l.Means));
            });

            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 600_000);
            Assert.Equal(JourneyState.Arrived, state);
            await world.OnWorldAsync(() => Assert.True(Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), goal) <= PlayerbotTravel.ArriveYards));
        }
        finally
        {
            brain.Stop();
        }
    }
}
