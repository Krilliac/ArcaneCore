using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The live fresh launch (2026-10-12) put about 76 level 1 Dwarf, Gnome and Human bots on the Deeprun Tram, going for Stormwind quests:
/// with no home a bot considers the nearest quest givers anywhere. A bot with no home below <c>Population:TramMinLevel</c> (10) begins
/// no tram or lift trip; one with a home, or at the level, still does.
/// </summary>
public sealed class PlayerbotTramLevelGuardTests
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f); // the Commons, by the Ironforge exit

    private static async Task<(TramTestWorld World, PlayerbotWorldDestinations Destinations)> StartAsync(byte level, PlayerbotHomeArea? home, PlayerbotOptions options)
    {
        var content = new CreatureContent([new CreatureTemplate { Entry = 6, Name = "ender", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 30001, Entry = 6, MapId = 0, X = IronforgeGoal.X, Y = IronforgeGoal.Y, Z = TramTestData.IronforgeFloor }], [], [], []);
        TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<ICreatureDataStore>(new CreatureStore(content)));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        await world.OnWorldAsync(() => world.Player.Level = level);
        return (world, new PlayerbotWorldDestinations(world.Session, options) { TramZoneAt = TramTestData.ZoneAt, Home = home });
    }

    private static PlayerbotOptions Options(int tramMinLevel = 10) => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = tramMinLevel } };

    [Fact]
    public async Task AHomelessLevelOneBot_BeginsNoTramTrip()
    {
        (TramTestWorld world, PlayerbotWorldDestinations destinations) = await StartAsync(1, null, Options());
        await using (world)
        {
            await world.OnWorldAsync(() => destinations.Update(world.Player, 6, 100));
            Assert.Null(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
        }
    }

    [Fact]
    public async Task ABotAtTheTramLevel_BeginsTheTrip()
    {
        (TramTestWorld world, PlayerbotWorldDestinations destinations) = await StartAsync(10, null, Options());
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 6, 100)));
            Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
        }
    }

    [Fact]
    public async Task ALevelOneBotWithAHome_IsNotHeldBackByTheGuard_AndZeroTurnsTheGuardOff()
    {
        var home = new PlayerbotHomeArea(0, TramTestData.StormwindExitLanding, 5000f, "start:northshire");
        (TramTestWorld homed, PlayerbotWorldDestinations withHome) = await StartAsync(1, home, Options());
        await using (homed)
            Assert.True(await homed.OnWorldAsync(() => withHome.Update(homed.Player, 6, 100)));

        (TramTestWorld unlimited, PlayerbotWorldDestinations noLimit) = await StartAsync(1, null, Options(tramMinLevel: 0));
        await using (unlimited)
            Assert.True(await unlimited.OnWorldAsync(() => noLimit.Update(unlimited.Player, 6, 100)));
    }

    [Fact]
    public void TransitAllowed_IsTheRule()
    {
        var population = new ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions();
        var home = new PlayerbotHomeArea(0, Vector3.Zero, 100f, null);
        Assert.False(PlayerbotWorldDestinations.TransitAllowed(null, 9, population));
        Assert.True(PlayerbotWorldDestinations.TransitAllowed(null, 10, population));
        Assert.True(PlayerbotWorldDestinations.TransitAllowed(home, 1, population));
        population.TramMinLevel = 0;
        Assert.True(PlayerbotWorldDestinations.TransitAllowed(null, 1, population));
    }

    private sealed class CreatureStore(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
