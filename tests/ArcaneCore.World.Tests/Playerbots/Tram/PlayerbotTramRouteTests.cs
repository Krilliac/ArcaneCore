using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// A managed bot rides the Deeprun Tram (<see cref="PlayerbotTram"/>, docs/areas/playbots.md "Travel by the Deeprun Tram"), driven by its
/// brain with the classic-db trigger and car rows (<see cref="TramTestData"/>), a synthetic car animation and flat floors: through the
/// Stormwind entrance, to the side of a car that will stop, aboard (MOVEFLAG_ONTRANSPORT, the car's GUID), through the tunnel, off at the
/// Ironforge platform and out by its exit. A bot on the tram map never walks anywhere but that route; one that logs in there leaves; a
/// car that never comes fails the trip closed. Every wait is bounded by game time on the manual clock.
/// </summary>
public sealed class PlayerbotTramRouteTests(ITestOutputHelper output)
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f); // the Commons, by the Ironforge exit

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    /// <summary>The trip from the Stormwind tram exit to Ironforge, through the bot's brain, with the anticheat judging the bot as a client.</summary>
    [Fact]
    public async Task ABot_RidesTheTram_FromStormwindToIronforge_AsAClientWould()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        var findings = new ConcurrentQueue<AntiCheatFinding>();
        AntiCheatFeature anticheat = world.Host.WorldServices.GetRequiredService<AntiCheatFeature>();
        anticheat.FindingRecorded += (_, finding, _, _) => findings.Enqueue(finding);
        await world.OnWorldAsync(() => anticheat.ApplyOptions(new AntiCheatOptions
        {
            ExemptManagedBots = false, TerrainChecks = false, BaselineGapMs = 600_000, SpeedClock = { Enabled = false },
        }));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "the trip did not begin");

        var stages = new List<TramStage>();
        float southmost = float.MaxValue;
        bool sawAboard = false;
        ulong? boardedCar = null;
        for (int step = 0; step < 4_000; step++)
        {
            (TramStage? stage, bool aboard, ulong car, float y, uint map) = await world.TickAsync(() =>
            {
                brain.Update(100);
                Player p = world.Player;
                return (PlayerbotTram.TripOf(p)?.Stage, (p.Movement.Flags & MovementFlags.OnTransport) != 0, p.Movement.TransportGuid, p.Y, p.MapId);
            });
            if (stage is { } s && (stages.Count == 0 || stages[^1] != s)) stages.Add(s);
            if (aboard)
            {
                sawAboard = true;
                boardedCar ??= car;
                Assert.Equal(boardedCar, car);
                Assert.Equal(TramTestData.Tram, map);
                southmost = MathF.Min(southmost, y);
            }

            if (stage is TramStage.Done or TramStage.Failed) break;
        }

        output.WriteLine("stages: " + string.Join(" > ", stages));
        Assert.Equal(TramStage.Done, stages[^1]);
        Assert.Equal([TramStage.ToEntrance, TramStage.ToPlatform, TramStage.Waiting, TramStage.Aboard, TramStage.ToExit, TramStage.Done],
            stages.Distinct());
        Assert.True(sawAboard, "never reported itself aboard");
        Assert.True(southmost < 100f, $"rode only to y={southmost:F0}: not through the tunnel");
        (uint mapId, Vector3 at, bool onTransport, uint? consent, bool mayWalkTram) = await world.OnWorldAsync(() => (
            world.Player.MapId, new Vector3(world.Player.X, world.Player.Y, world.Player.Z),
            (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0, PlayerbotAreaTriggers.TeleportTargetOf(world.Player),
            PlayerbotTram.IsTraveling(world.Player)));
        Assert.Equal(0u, mapId);
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(TramTestData.IronforgeExitLanding.X, TramTestData.IronforgeExitLanding.Y)) < 5f,
            $"out at {at}, not at the Ironforge exit");
        Assert.False(onTransport);
        Assert.Null(consent); // the entrance consent is gone with the trip
        Assert.False(mayWalkTram);
        Assert.DoesNotContain(findings, f => f.Type is AntiCheatViolation.Flag or AntiCheatViolation.Speed or AntiCheatViolation.Teleport);
    }

    /// <summary>
    /// The live stall (2026-10-11: dwarf and gnome bots with Ironforge, Dun Morogh and Loch Modan turn-ins stood at the Stormwind tram
    /// entrance): a destination of <see cref="PlayerbotWorldDestinations"/> on the Ironforge side, seen from Stormwind, begins the trip
    /// instead of a walk; once the bot is out at Ironforge the same destination is an ordinary walk, and no second trip begins.
    /// </summary>
    [Fact]
    public async Task ADestinationInIronforge_SeenFromStormwind_BeginsTheTrip_AndIsWalkedToAfterIt()
    {
        var content = new CreatureContent([new CreatureTemplate { Entry = 6, Name = "ender", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 30001, Entry = 6, MapId = 0, X = IronforgeGoal.X, Y = IronforgeGoal.Y, Z = TramTestData.IronforgeFloor }], [], [], []);
        await using TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<ICreatureDataStore>(new CreatureStore(content)));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        var destinations = new PlayerbotWorldDestinations(world.Session, options) { TramZoneAt = TramTestData.ZoneAt };
        Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 6, 100)));
        Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
        Assert.Equal(6u, destinations.TargetEntry);

        var brain = new PlayerbotBrain(world.Session, options);
        TramStage? stage = null;
        for (int step = 0; step < 4_000 && stage is not (TramStage.Done or TramStage.Failed); step++)
            stage = await world.TickAsync(() => { brain.Update(100); return PlayerbotTram.TripOf(world.Player)?.Stage; });
        Assert.Equal(TramStage.Done, stage);

        (bool walking, TramStage? after, uint map) = await world.TickAsync(() =>
            (destinations.Update(world.Player, 6, 100), PlayerbotTram.TripOf(world.Player)?.Stage, world.Player.MapId));
        Assert.True(walking, "no walk to the destination after the ride");
        Assert.Equal(TramStage.Done, after);
        Assert.Equal(0u, map);
        Assert.True(await world.OnWorldAsync(() => PlayerbotMotion.IsActive(world.Player)), "not walking towards the destination");
    }

    private sealed class CreatureStore(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    /// <summary><see cref="PlayerbotTram.TryBegin"/> (the seam the destinations call) refuses a destination on this side.</summary>
    [Fact]
    public async Task TheTrip_BeginsOnlyForADestinationOnTheOtherSide()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, new Vector3(-9460f, 60f, 56f), TramTestData.ZoneAt))); // Goldshire
        Assert.Null(await world.OnWorldAsync(() => PlayerbotAreaTriggers.TeleportTargetOf(world.Player)));
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        Assert.Equal(TramTestData.Tram, await world.OnWorldAsync(() => PlayerbotAreaTriggers.TeleportTargetOf(world.Player)));
    }

    [Fact]
    public async Task HordeBot_DoesNotBeginAnAllianceCityTramTrip()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(race: 2);
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.Equal(Team.Horde, await world.OnWorldAsync(() => world.Player.Team));
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player,
            IronforgeGoal, TramTestData.ZoneAt)));
        Assert.Null(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
    }

    /// <summary>
    /// Without a trip a bot on the tram map plans nothing there (the map is off AllowedMaps), and its brain takes it out by the nearest
    /// platform's exit: a bot saved on the way logs back in there and leaves, and never wanders the platforms or the tunnel.
    /// </summary>
    [Fact]
    public async Task ABotOnTheTramMapWithoutATrip_PlansNothingThere_AndLeavesByTheNearestExit()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform with { X = 40f });
        PlayerbotOptions options = Options();
        Assert.False(await world.OnWorldAsync(() => PlayerbotNavigation.TryPlan(world.Player, new Vector3(-20f, 2480f, TramTestData.PlatformFloor), options, out _)),
            "a bot without a trip planned a walk on the tram map");
        Assert.True(await world.OnWorldAsync(() => PlayerbotMapPolicy.MayStayOnMap(world.Player, options)), "the login gate refuses the tram map");

        var brain = new PlayerbotBrain(world.Session, options);
        var destinations = new List<Vector3>();
        for (int step = 0; step < 1_000; step++)
        {
            (uint map, Vector3? routeEnd) = await world.TickAsync(() =>
            {
                brain.Update(100);
                return (world.Player.MapId, PlayerbotTram.TripOf(world.Player)?.Route is { } r ? r.Points[^1] : (Vector3?)null);
            });
            if (routeEnd is { } end && map == TramTestData.Tram) destinations.Add(end);
            if (map == 0) break;
        }

        (uint mapId, Vector3 at) = await world.OnWorldAsync(() => (world.Player.MapId, new Vector3(world.Player.X, world.Player.Y, world.Player.Z)));
        Assert.Equal(0u, mapId);
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(TramTestData.StormwindExitLanding.X, TramTestData.StormwindExitLanding.Y)) < 5f,
            $"out at {at}, not at the Stormwind exit");
        // Every walk on the tram map went to the exit trigger (2171 at 80.2, 2490.9), nowhere else.
        Assert.NotEmpty(destinations);
        Assert.All(destinations, end => Assert.True(Vector2.Distance(new(end.X, end.Y), new(80.16f, 2490.88f)) < 3f, $"walked to {end}"));
    }

    /// <summary>
    /// No car runs (an animation without the six cars): the trip fails closed at the platform with a stall reason, the bot leaves by the
    /// exit it came in by, and that entrance is not tried again before its backoff ends (<see cref="PlayerbotTransitBackoff"/>).
    /// </summary>
    [Fact]
    public async Task WithoutACar_TheTripFailsClosed_TheBotGoesBackOut_AndDoesNotRetrySoon()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(animation: new TransportAnimationCatalog([(4170u, new TransportAnimationNode(0, 0, 0, 0))]));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        string? stall = null;
        for (int step = 0; step < 2_000 && stall is null; step++)
        {
            stall = await world.TickAsync(() =>
            {
                int before = brain.TransitFailureCount;
                brain.Update(100);
                return brain.TransitFailureCount != before ? brain.LastTransitFailure : null;
            });
        }

        output.WriteLine(stall ?? "(none)");
        Assert.NotNull(stall);
        Assert.StartsWith("tram: no tram car runs", stall);
        (uint mapId, Vector3 at, TramStage? stage) = await world.OnWorldAsync(() => (world.Player.MapId,
            new Vector3(world.Player.X, world.Player.Y, world.Player.Z), PlayerbotTram.TripOf(world.Player)?.Stage));
        Assert.Equal(0u, mapId);
        Assert.Equal(TramStage.Failed, stage);
        Assert.True(Vector2.Distance(new(at.X, at.Y), new(TramTestData.StormwindExitLanding.X, TramTestData.StormwindExitLanding.Y)) < 5f,
            $"out at {at}, not back at the Stormwind exit");
        Assert.False(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "retried at once");
        Assert.Null(await world.OnWorldAsync(() => PlayerbotAreaTriggers.TeleportTargetOf(world.Player)));
    }

    /// <summary>
    /// On the tram map with no tram in the data (no triggers) and a bind point the server refuses: the failure is reported once as a stall
    /// with its reason, and the bot does not loop on it silently (no further report every think, the stall watch not reset forever).
    /// With a bind point that works, the bot is sent there.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTheTramMap_WithoutTramData_TheBotFailsClosedOnce(bool bindWorks)
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(configure: services => services.AddSingleton<IMapDataStore>(new NoTriggers()));
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        await world.OnWorldAsync(() => world.Player.Home = bindWorks
            ? new HomeBind(0, 1519, TramTestData.StormwindExitLanding.X, TramTestData.StormwindExitLanding.Y, TramTestData.StormwindExitLanding.Z)
            : new HomeBind(9999, 0, 0, 0, 0));
        var brain = new PlayerbotBrain(world.Session, Options());
        var reports = new List<string>();
        for (int step = 0; step < 100; step++)
        {
            string? report = await world.TickAsync(() =>
            {
                int before = brain.TransitFailureCount;
                brain.Update(100);
                return brain.TransitFailureCount != before ? brain.LastTransitFailure : null;
            });
            if (report is not null) reports.Add(report);
        }

        output.WriteLine(string.Join(" | ", reports));
        Assert.Single(reports);
        Assert.Contains(bindWorks ? "sent to its bind point" : "the bind-point teleport was refused", reports[0]);
        Assert.Equal(bindWorks ? 0u : TramTestData.Tram, await world.OnWorldAsync(() => world.Player.MapId));
    }

    private sealed class NoTriggers : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(TramTestData.Maps, [], [], [], []));
    }

    /// <summary>The entrance is taken only while a trip heads there: a bot walking through it without one stays on the continent.</summary>
    [Fact]
    public async Task WithoutATrip_ABotWalksThroughTheEntrance_AndStaysOnTheContinent()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        Vector3 beyond = new(-8338f, 504f, TramTestData.StormwindFloor); // through the entrance sphere (2173)
        PlayerbotRoute? route = await world.OnWorldAsync(() => PlayerbotNavigation.TryPlan(world.Player, beyond, options, out PlayerbotRoute? r) ? r : null);
        Assert.NotNull(route);
        for (int step = 0; step < 200; step++)
        {
            bool moving = await world.TickAsync(() => PlayerbotNavigation.TryAdvance(world.Session, route!, options, 100, world.Host.World.NowMs));
            if (!moving) break;
        }

        (uint mapId, float left) = await world.OnWorldAsync(() => (world.Player.MapId, Vector2.Distance(new(world.Player.X, world.Player.Y), new(beyond.X, beyond.Y))));
        Assert.Equal(0u, mapId);
        Assert.True(left < 2f, $"stopped {left:F1} yards short");
    }

    /// <summary>The cars of the fixture world are the map-wide ones the bot rides (a guard for the fixture itself).</summary>
    [Fact]
    public async Task TheFixtureCars_AreMapWide_WithTheirAnimation()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
        (int count, uint cycle) = await world.OnWorldAsync(() =>
        {
            GameObjectMapSystem objects = world.TramObjects()!;
            return (objects.MapWideElevators.Count, objects.MapWideElevators.Max(objects.ElevatorCycle));
        });
        Assert.Equal(6, count);
        Assert.Equal(120_000u, cycle);
    }
}
