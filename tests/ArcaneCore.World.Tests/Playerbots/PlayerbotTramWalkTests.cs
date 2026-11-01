using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Trainer, vendor and home walks begin Deeprun Tram and lift trips as quest destinations do (docs/areas/playbots.md, "Travel by the
/// Deeprun Tram"): the trainer walk of a bot in Stormwind to a class trainer in Ironforge takes the tram, and the Undercity vault lifts
/// are joined by the lift network (the three <c>playerbot_elevator_region</c> rows of map 0) for a walk from the Ruins of Lordaeron down.
/// </summary>
public sealed class PlayerbotTramWalkTests
{
    /// <summary>A class trainer in the Commons of Ironforge, by the tram's Ironforge exit (zone 1537 in <see cref="TramTestData.ZoneAt"/>).</summary>
    private static readonly Vector3 IronforgeTrainer = new(-4918.9f, -982.5f, TramTestData.IronforgeFloor);

    private static PlayerbotOptions Options(int tramMinLevel = 0) => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = tramMinLevel } };

    [Fact]
    public async Task ATrainerWalkFromStormwindToIronforge_TakesTheTram()
    {
        var fixture = new FarTrainerFixture { SpawnAt = IronforgeTrainer };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<ISpellContentStore>(fixture));
            await using (world)
            {
                await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
                bool moved = await world.OnWorldAsync(() =>
                {
                    world.Player.Money = 100;
                    var route = new PlayerbotTrainerDestinations(world.Session, Options()) { TramZoneAt = TramTestData.ZoneAt };
                    return route.Update(world.Player, 500) && route.TargetEntry == FarTrainerFixture.Entry;
                });
                Assert.True(moved);
                Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
            }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task ATrainerWalk_OfAHomelessLevelOneBot_BelowTheTramLevel_TakesNoTram()
    {
        var fixture = new FarTrainerFixture { SpawnAt = IronforgeTrainer };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<ISpellContentStore>(fixture));
            await using (world)
            {
                await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
                await world.OnWorldAsync(() =>
                {
                    world.Player.Money = 100;
                    world.Player.Level = 1;
                    var route = new PlayerbotTrainerDestinations(world.Session, Options(tramMinLevel: 10)) { TramZoneAt = TramTestData.ZoneAt };
                    route.Update(world.Player, 500);
                });
                Assert.Null(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
            }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public void TheUndercityLifts_AreJoinedByRegionRows_AndTheRuinsWalkTakesTheLiftDown()
    {
        var lift189 = new ElevatorLift(0, [new ElevatorCar(0, 20648, 20656, new Vector3(1595.24f, 189.647f, -40.76f), new Vector3(1595.24f, 189.647f, 55.65f), 16_667)]);
        var lift292 = new ElevatorLift(0, [new ElevatorCar(0, 20649, 20657, new Vector3(1596.22f, 291.798f, -40.76f), new Vector3(1596.22f, 291.798f, 55.65f), 16_667)]);
        var lift241 = new ElevatorLift(0, [new ElevatorCar(0, 20650, 20658, new Vector3(1544.0f, 241.0f, -40.76f), new Vector3(1544.0f, 241.0f, 55.65f), 16_667)]);
        foreach (ElevatorLift lift in new[] { lift189, lift292, lift241 })
        {
            ElevatorRegions? regions = ElevatorNetwork.RegionsOf(lift);
            Assert.NotNull(regions);
            Assert.True(regions!.VaultLow);
        }

        var network = new ElevatorNetwork([lift189, lift292, lift241]);
        var survey = new ElevatorSurvey(_ => (1497u, 1497u));
        // A bot on the Ruins of Lordaeron beside the 1596,292 lift, with a goal in the vault: the walk takes that lift from its upper level.
        (ElevatorLift Lift, ElevatorLevel From)? down = network.Choose(0, new Vector3(1574.7f, 308.5f, 61.7f), new Vector3(1595.0f, 189.0f, -43.0f), survey);
        Assert.Equal((lift292, ElevatorLevel.High), down);
    }
}
