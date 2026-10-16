using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Transports;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// End to end: a managed bot with a running <see cref="PlayerbotBrain"/> is given a place on the other map through
/// <see cref="IPlayerbotTravel.TryBegin"/> and, with nothing but brain updates and world ticks (the motion pump, as the managed-bot feature
/// runs it), walks to the dock, boards, crosses, steps off and walks to the goal on map 1 (<see cref="ShipTestWorld"/>).
/// </summary>
public sealed class TravelJourneyShipTests(ITestOutputHelper output)
{
    /// <summary>Where the journeys end: 40 yards east and 25 north of the port on map 1.</summary>
    internal static readonly Vector3 Goal = new(TransportWorldContent.FarX + 40f, 25f, TransportWorldContent.StartZ);

    private static readonly Dictionary<uint, ShipClass> s_zeppelin = new()
    {
        [TransportWorldContent.Crossing] = new(TravelMeans.Zeppelin, TravelAccess.Horde, "synthetic zeppelin"),
        [TransportWorldContent.Ferry] = new(TravelMeans.Boat, TravelAccess.Alliance, "synthetic ferry"),
    };

    internal static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };

    /// <summary>
    /// Brain updates every 50 ms tick (a think every 100 ms) and the motion pumped after each tick, until the journey ends or
    /// <paramref name="maxMs"/> of game time passed. The journey's state at the end.
    /// </summary>
    internal static async Task<JourneyState?> RunAsync(ShipTestWorld world, PlayerbotBrain brain, uint maxMs, Action<Player>? observe = null)
    {
        for (uint elapsed = 0; elapsed < maxMs; elapsed += 50)
        {
            JourneyState? state = await world.OnWorldAsync(() =>
            {
                brain.Update(50);
                return PlayerbotTravel.JourneyOf(world.Player)?.State;
            });
            if (state is not JourneyState.Active) return state;
            await world.AdvanceAsync(50);
            await world.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(world.Session, world.Player, world.Host.World.NowMs);
                observe?.Invoke(world.Player);
            });
        }

        return await world.OnWorldAsync(() => PlayerbotTravel.JourneyOf(world.Player)?.State);
    }

    private async Task TraceAsync(ShipTestWorld world)
        => output.WriteLine(await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            TravelJourney? j = PlayerbotTravel.JourneyOf(p);
            ShipTrip? trip = PlayerbotShip.TripOf(p);
            return $"at {world.Host.World.NowMs} ms: map {p.MapId} ({p.X:F1},{p.Y:F1},{p.Z:F1}); journey {j?.State} leg {j?.LegIndex}/{j?.Legs.Count} "
                + $"failure {j?.Failure}; ship stage {trip?.Stage} ({trip?.Failure})";
        }));

    private static void AssertNoTravelAnomaly(AnomalyCapture capture)
    {
        foreach (string area in new[] { PlayerbotTravel.AnomalyArea, PlayerbotShip.AnomalyArea })
            Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(area)).Any(), $"{area} anomaly: {capture.Describe()}");
    }

    [Fact]
    public async Task AnAllianceBot_WalksToTheDock_TakesTheBoat_AndArrivesOnMap1_WithOnlyItsBrainDriving()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            TravelRoute route = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.CanReach(world.Session, world.Player, 1, Goal));
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, Goal, out string? why), why);
                Assert.True(travel.IsTraveling(world.Player));
                return PlayerbotTravel.JourneyOf(world.Player)!.Planned;
            });
            Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], route.Legs.Select(l => l.Means));
            Assert.Equal(20808u, route.Legs[1].TransportEntry);

            bool aboard = false;
            JourneyState? state = await RunAsync(world, brain, 600_000, p => aboard |= p.Transport?.Entry == 20808);
            await TraceAsync(world);
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(aboard, "the bot was never aboard the boat");
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(1u, world.Player.MapId);
                Assert.Null(world.Player.Transport);
                Assert.True(Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), Goal) <= PlayerbotTravel.ArriveYards,
                    $"the bot stands at {world.Player.X},{world.Player.Y}");
                Assert.False(travel.IsTraveling(world.Player));
                Assert.Equal(ShipStage.Done, PlayerbotShip.TripOf(world.Player)!.Stage);
                Assert.Equal(0, brain.TransitFailureCount);
                Assert.Equal(0, brain.StallCount);
            });
            AssertNoTravelAnomaly(capture);
        }
        finally
        {
            brain.Stop();
        }
    }

    [Fact]
    public async Task AHordeBot_TakesTheZeppelin_AndArrivesOnMap1_WithOnlyItsBrainDriving()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(race: 2);
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        var travel = new PlayerbotTravel(options) { ShipRoutes = _ => PlayerbotShipNetwork.Routes(world.System, s_zeppelin) };
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        try
        {
            TravelRoute route = await world.OnWorldAsync(() =>
            {
                Assert.True(travel.TryBegin(world.Session, world.Player, 1, Goal, out string? why), why);
                return PlayerbotTravel.JourneyOf(world.Player)!.Planned;
            });
            Assert.Equal([TravelMeans.Walk, TravelMeans.Zeppelin, TravelMeans.Walk], route.Legs.Select(l => l.Means));
            Assert.Equal(TransportWorldContent.Crossing, route.Legs[1].TransportEntry);

            bool aboard = false;
            JourneyState? state = await RunAsync(world, brain, 600_000, p => aboard |= p.Transport?.Entry == TransportWorldContent.Crossing);
            await TraceAsync(world);
            Assert.Equal(JourneyState.Arrived, state);
            Assert.True(aboard, "the bot was never aboard the zeppelin");
            await world.OnWorldAsync(() =>
            {
                Assert.Equal(1u, world.Player.MapId);
                Assert.True(Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), Goal) <= PlayerbotTravel.ArriveYards);
                Assert.Equal(0, brain.TransitFailureCount);
            });
            AssertNoTravelAnomaly(capture);
        }
        finally
        {
            brain.Stop();
        }
    }
}
