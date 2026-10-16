using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>The per-tick pump re-chooses a car (a list, LINQ and a path query per candidate) at most once per <c>PlanRetryMs</c>.</summary>
public sealed class PlayerbotElevatorPumpChooseTests
{
    [Fact]
    public async Task ThePump_ChoosesACarAgain_OnlyOncePerPlanRetryWindow()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.PlaceAsync(new System.Numerics.Vector3(lift.Centre.X - 12f, lift.Centre.Y, ElevatorTestWorld.BottomFloor)); // inside ApproachYards
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet));
        await world.AdvanceAsync(500);

        (bool first, bool tooSoon, bool afterWindow) = await world.OnWorldAsync(() =>
        {
            ElevatorTrip trip = PlayerbotElevator.TripOf(world.Player)!;
            Assert.Equal(ElevatorStage.ToStop, trip.Stage);
            trip.Options ??= new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 };
            float dist = System.Numerics.Vector2.Distance(new System.Numerics.Vector2(world.Player.X, world.Player.Y), trip.Lift.Centre);
            Assert.True(dist <= PlayerbotElevator.ApproachYards, $"dist {dist} map {world.Player.MapId} lift {trip.Lift.MapId} inworld {world.Player.IsInWorld}");
            uint now = unchecked(trip.LastDriveMs + 1_000);

            trip.Car = null;
            trip.CarObject = null;
            PlayerbotMotion.Pump(world.Session, world.Player, now);
            bool chosenFirst = trip.Car is not null;

            trip.Car = null;
            trip.CarObject = null;
            PlayerbotMotion.Pump(world.Session, world.Player, unchecked(now + 300));
            bool chosenSoon = trip.Car is not null;

            PlayerbotMotion.Pump(world.Session, world.Player, unchecked(now + PlayerbotElevator.PlanRetryMs + 300));
            return (chosenFirst, chosenSoon, trip.Car is not null);
        });

        Assert.True(first, "the pump never chose a car");
        Assert.False(tooSoon, "the pump chose a car again inside the PlanRetryMs window");
        Assert.True(afterWindow, "the pump never chose a car once the window had passed");
    }
}
