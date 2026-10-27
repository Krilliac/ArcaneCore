using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Transports;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Issue #1456: a walk leg before a boat that fails far from the port is the walk's failure, not the boat's. The boat's driver only walks
/// the last yards to its dock (<see cref="PlayerbotShip.BeginReachYards"/>); a failed walk thousands of yards out never begins a ship leg
/// and never reports <c>dock-unreachable</c>.
/// </summary>
public sealed class TravelFarHandOffTests(ITestOutputHelper output)
{
    /// <summary>3000 yards west of the port on map 0 (the live samples were 2150 to 3052 yards out).</summary>
    private static readonly Vector3 FarAway = ShipTestWorld.PortOnMap0.Position + new Vector3(-3000f, 0f, 0f);

    /// <summary>Every plan fails and ends at the bot's feet.</summary>
    private sealed class Refusing : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(PathType.Incomplete, [start, start]);
    }

    [Fact]
    public async Task AWalkToABoatPortThatFailsFarOut_FailsAsAWalk_BeginsNoShipLeg_AndReportsNoDockUnreachable()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, FarAway);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new Refusing()));
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        bool shipLegBegun = false;
        try
        {
            TravelRoute route = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out string? why), why);
                return PlayerbotTravel.JourneyOf(world.Player)!.Planned;
            });
            Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], route.Legs.Select(l => l.Means));

            JourneyState? state = await TravelJourneyShipTests.RunAsync(world, brain, 300_000,
                p => shipLegBegun |= PlayerbotShip.TripOf(p) is not null);
            TravelJourney journey = await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)!);
            output.WriteLine($"{journey.State} {journey.Failure}; last transit failure {brain.LastTransitFailure} ({brain.TransitFailureCount}); {capture.Describe()}");
            Assert.Equal(JourneyState.Failed, state);
            Assert.False(shipLegBegun, "a ship leg was begun thousands of yards from its port");
            Assert.Contains("Walk leg: walk:", journey.Failure);
            await world.OnWorldAsync(() =>
            {
                Assert.Null(PlayerbotShip.TripOf(world.Player));
                Assert.False(PlayerbotShip.IsBackedOff(world.Player, 20808, fromA: true, world.Host.World.NowMs)); // the dock did not fail
            });
            Assert.Equal(0, capture.Count(PlayerbotAnomalies.Key(PlayerbotShip.AnomalyArea, "dock-unreachable")));
            Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(PlayerbotShip.AnomalyArea)).Any(), capture.Describe());
            Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key(PlayerbotTravel.AnomalyArea, "leg-failed")));
        }
        finally
        {
            brain.Stop();
        }
    }

    [Fact]
    public async Task AShipLegBegunFarFromItsPort_IsRefused_Unreported_AndNoTripIsBegun()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, FarAway);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        var options = new PlayerbotOptions();

        (bool begun, string? why) = await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out string? w), w));

        Assert.False(begun);
        Assert.Contains("from the port", why);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(PlayerbotShip.TripOf(world.Player));
            Assert.False(PlayerbotShip.IsBackedOff(world.Player, boat.Entry, fromA: true, world.Host.World.NowMs));
        });
        Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(PlayerbotShip.AnomalyArea)).Any(), capture.Describe());
    }
}
