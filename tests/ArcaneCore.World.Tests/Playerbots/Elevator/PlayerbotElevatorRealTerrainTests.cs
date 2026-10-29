using System.Numerics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// Needs the live server's terrain extraction (<c>ARCANECORE_TEST_TERRAIN_DIR</c>: maps/) and the client's build-5875 DBCs
/// (<c>ARCANECORE_TEST_DBC_DIR</c>: AreaTable.dbc and TransportAnimation.dbc). Neither is committed; without them the test is reported
/// Skipped, never a silent pass.
/// </summary>
internal sealed class RealElevatorFactAttribute : FactAttribute
{
    public const string TerrainVariable = "ARCANECORE_TEST_TERRAIN_DIR", DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public RealElevatorFactAttribute()
    {
        string? terrain = Environment.GetEnvironmentVariable(TerrainVariable);
        string? dbc = Environment.GetEnvironmentVariable(DbcVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !Directory.Exists(Path.Combine(terrain, "maps")))
            Skip = $"{TerrainVariable} is not set to a terrain data root with maps/: the real lift planning did NOT run.";
        else if (string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "AreaTable.dbc"))
            || !File.Exists(Path.Combine(dbc, "TransportAnimation.dbc")))
            Skip = $"{DbcVariable} is not set to a directory with AreaTable.dbc and TransportAnimation.dbc: the real lift planning did NOT run.";
    }
}

/// <summary>
/// The lift planning (<see cref="ElevatorNetwork"/>) on the real data: the classic-db lift rows (<see cref="ElevatorTestData"/>), the
/// client's TransportAnimation.dbc for their paths, and the Kalimdor terrain and AreaTable.dbc for the regions round each lift. The live
/// failure (issue #186): bots in Thousand Needles with quest 1111 complete walked north towards Wharfmaster Dizzywig in Ratchet and stalled
/// against the canyon wall, for the Great Lift is the only way up to The Barrens.
/// </summary>
public sealed class PlayerbotElevatorRealTerrainTests(ITestOutputHelper output)
{
    private const uint TheBarrens = 17, ThousandNeedles = 400, FreewindPost = 484, Mulgore = 215, ThunderBluff = 1638;

    private const int MaxTileWaitAttempts = 200; // x 50 ms = 10 s for the pool to read a nav tile

    private static readonly Vector3 Bloodhoof = new(-2300f, -380f, -9f);
    private static readonly Vector3 ThunderBluffMesa = new(-1280f, 120f, 131f);

    private static (ElevatorNetwork Network, ElevatorSurvey Survey) Load()
    {
        string terrain = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable)!;
        string dbc = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable)!;
        var manager = new TerrainManager(terrain)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        TerrainInfo kalimdor = manager.For(ElevatorTestData.Kalimdor);
        TransportAnimationCatalog animation = TransportAnimationDbcReader.Load(Path.Combine(dbc, "TransportAnimation.dbc"));
        var network = ElevatorNetwork.Build(ElevatorTestData.Content().Spawns, ElevatorTestData.Content().FindTemplate, animation);
        return (network, new ElevatorSurvey(at => kalimdor.GetZoneAndAreaId(at.X, at.Y, at.Z)));
    }

    /// <summary>
    /// The data gives four lifts on Kalimdor, two cars each, with the levels of the client's paths; every one has a row of
    /// <see cref="ElevatorRegions.Known"/>, and the rows name the zones and areas the terrain puts the places on either level in. The terrain
    /// alone cannot tell the levels apart beside a lift: the Great Lift's top landing lies in Thousand Needles' "The Great Lift" area.
    /// </summary>
    [RealElevatorFact]
    public void TheRealData_GivesFourLifts_AndTheRegionTableMatchesTheTerrain()
    {
        (ElevatorNetwork network, ElevatorSurvey survey) = Load();
        foreach (ElevatorLift lift in network.Lifts)
            output.WriteLine($"{lift.Name}: cars {string.Join(", ", lift.Cars.Select(c => c.SpawnGuid))}; {ElevatorNetwork.RegionsOf(lift)}");

        Assert.Equal(4, network.Lifts.Count);
        Assert.All(network.Lifts, lift => Assert.Equal(2, lift.Cars.Count));
        Assert.All(network.Lifts, lift => Assert.NotNull(ElevatorNetwork.RegionsOf(lift)));
        ElevatorLift great = Lift(network, -4668f, -1839f);
        Assert.InRange(great.LowZ, -45f, -43f);
        Assert.InRange(great.HighZ, 85f, 86f);

        Assert.Equal(TheBarrens, survey.RegionAt(ElevatorTestData.Dizzywig).Zone);
        Assert.Equal(ThousandNeedles, survey.RegionAt(ElevatorTestData.ShimmeringFlatsStall).Zone);
        Assert.Equal(ThousandNeedles, survey.RegionAt(ElevatorTestData.UnderTheLiftStall).Zone);
        Assert.Equal((ThousandNeedles, FreewindPost), survey.RegionAt(ElevatorTestData.CliffwatcherLonghorn));
        Assert.Equal(Mulgore, survey.RegionAt(Bloodhoof).Zone);
        Assert.Equal(ThunderBluff, survey.RegionAt(ThunderBluffMesa).Zone);
        Assert.DoesNotContain(survey.RegionAt(ElevatorTestData.NatakaLonghorn).Zone, (uint[])[TheBarrens, ThousandNeedles, Mulgore, ThunderBluff]);
        // 40 yards east of the Great Lift, on the plateau at 84: still Thousand Needles ("The Great Lift", area 485).
        Assert.Equal((ThousandNeedles, 485u), survey.RegionAt(new Vector3(-4628f, -1839f, 84f)));
    }

    /// <summary>
    /// Issue #186: from where the bots stalled (the Shimmering Flats edge, under the cliff), the way to Wharfmaster Dizzywig in Ratchet is the
    /// Great Lift, boarded at its foot; back from Ratchet it is boarded at its top.
    /// </summary>
    [RealElevatorFact]
    public void FromThousandNeedles_TheWayToRatchet_IsTheGreatLift_FromItsFoot()
    {
        (ElevatorNetwork network, ElevatorSurvey survey) = Load();
        ElevatorLift great = Lift(network, -4668f, -1839f);
        foreach (Vector3 stall in (Vector3[])[ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.UnderTheLiftStall])
        {
            (ElevatorLift Lift, ElevatorLevel From)? choice = network.Choose(ElevatorTestData.Kalimdor, stall, ElevatorTestData.Dizzywig, survey);
            output.WriteLine($"from {stall}: {choice?.Lift.Name} from {choice?.From}");
            Assert.Equal((great, ElevatorLevel.Low), choice);
        }

        Assert.Equal((great, ElevatorLevel.High), network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.Dizzywig,
            ElevatorTestData.ShimmeringFlatsStall, survey));
    }

    /// <summary>
    /// Quest 5361 is taken on Freewind Post's mesa and ends in Desolace, in neither of the Freewind lift's regions: a mesa is left only by its
    /// lift, so the way is that lift down. From Ratchet to Freewind Post it is the Great Lift down first.
    /// </summary>
    [RealElevatorFact]
    public void OnFreewindPost_TheWayToDesolace_IsTheLiftDown()
    {
        (ElevatorNetwork network, ElevatorSurvey survey) = Load();
        ElevatorLift freewind = Lift(network, -5391f, -2497f);
        Assert.Equal((freewind, ElevatorLevel.High), network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.CliffwatcherLonghorn,
            ElevatorTestData.NatakaLonghorn, survey));
        Assert.Equal((Lift(network, -4668f, -1839f), ElevatorLevel.High), network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.Dizzywig,
            ElevatorTestData.CliffwatcherLonghorn, survey));
        Assert.Equal((freewind, ElevatorLevel.Low), network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.ShimmeringFlatsStall,
            ElevatorTestData.CliffwatcherLonghorn, survey));
    }

    /// <summary>
    /// From Mulgore (Bloodhoof Village) to Thunder Bluff's mesa: a Thunder Bluff lift up; down again for Ratchet. From the Crossroads to
    /// Bloodhoof (walkable): no lift.
    /// </summary>
    [RealElevatorFact]
    public void FromMulgore_ToThunderBluff_IsAThunderBluffLift()
    {
        (ElevatorNetwork network, ElevatorSurvey survey) = Load();
        (ElevatorLift Lift, ElevatorLevel From)? up = network.Choose(ElevatorTestData.Kalimdor, Bloodhoof, ThunderBluffMesa, survey);
        output.WriteLine($"{up?.Lift.Name} from {up?.From}");
        Assert.NotNull(up);
        Assert.Equal(ElevatorLevel.Low, up.Value.From);
        Assert.Contains(up.Value.Lift.Cars, c => c.Entry is 4171 or 47296);
        (ElevatorLift Lift, ElevatorLevel From)? down = network.Choose(ElevatorTestData.Kalimdor, ThunderBluffMesa, ElevatorTestData.Dizzywig, survey);
        Assert.NotNull(down);
        Assert.Equal(ElevatorLevel.High, down.Value.From);
        Assert.Contains(down.Value.Lift.Cars, c => c.Entry is 4170 or 47297);
        Assert.Null(network.Choose(ElevatorTestData.Kalimdor, new Vector3(-450f, -2650f, 96f), Bloodhoof, survey));
    }

    [Fact]
    public void UndercityVaultLift_ClassifiesSurfaceAboveVaultMaxZAsHigh_EvenWhen2DZoneReportsUndercity()
    {
        var undercityCar = new ElevatorCar(0, 20649, 20657, new Vector3(1596.22f, 291.798f, -40.76f), new Vector3(1596.22f, 291.798f, 55.65f), 16_667);
        var lift = new ElevatorLift(0, [undercityCar]);
        ElevatorRegions regions = ElevatorNetwork.RegionsOf(lift)!;
        Assert.NotNull(regions);
        Assert.True(regions.VaultLow);

        // Even when the 2D ADT cell directly above Undercity reports zone 1497 (Undercity), a bot on the Ruins of Lordaeron surface
        // at Z = +61.7 (> VaultMaxZ = -5) is on ElevatorLevel.High, while a bot underground at Z = -23.0 is on ElevatorLevel.Low.
        var survey = new ElevatorSurvey(_ => (1497u, 1497u));
        Assert.Equal(ElevatorLevel.High, ElevatorNetwork.LevelOf(lift, regions, survey, new Vector3(1582.9f, 316.8f, 61.7f)));
        Assert.Equal(ElevatorLevel.High, ElevatorNetwork.LevelOf(lift, regions, survey, new Vector3(1720.0f, 239.0f, 62.0f)));
        Assert.Equal(ElevatorLevel.Low, ElevatorNetwork.LevelOf(lift, regions, survey, new Vector3(1582.9f, 316.8f, -23.0f)));
        Assert.Equal(ElevatorLevel.Low, ElevatorNetwork.LevelOf(lift, regions, survey, new Vector3(1720.0f, 239.0f, -40.0f)));
        Assert.All(PlayerbotElevator.VaultLandingRadii, r => Assert.InRange(r, 11.5f, 14.0f));
    }

    [Fact]
    public void UndercityVaultLift_NearUpperEntrance_PrefersThatLiftOverOtherVaultLifts()
    {
        var lift189 = new ElevatorLift(0, [new ElevatorCar(0, 20648, 20656, new Vector3(1595.24f, 189.647f, -40.76f), new Vector3(1595.24f, 189.647f, 55.65f), 16_667)]);
        var lift292 = new ElevatorLift(0, [new ElevatorCar(0, 20649, 20657, new Vector3(1596.22f, 291.798f, -40.76f), new Vector3(1596.22f, 291.798f, 55.65f), 16_667)]);
        var network = new ElevatorNetwork([lift189, lift292]);
        var survey = new ElevatorSurvey(_ => (1497u, 1497u));

        // Bot at 1574.7, 308.5, 61.7 (inside the upper entrance room of lift 1596, 292) heading to a goal near 1595, 189 underground:
        // takes the lift it is already standing beside (1596, 292) rather than trying to walk through Ruins walls to 1595, 189.
        (ElevatorLift Lift, ElevatorLevel From)? choice = network.Choose(0, new Vector3(1574.7f, 308.5f, 61.7f), new Vector3(1595.0f, 189.0f, -43.0f), survey);
        Assert.Equal((lift292, ElevatorLevel.High), choice);
    }

    [RealElevatorFact]
    public async Task RealTerrain_ThunderBluffAndUndercityLifts_HaveWalkableAndReachableLandings()
    {
        string terrain = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable)!;
        string dbc = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable)!;
        TransportAnimationCatalog animation = TransportAnimationDbcReader.Load(Path.Combine(dbc, "TransportAnimation.dbc"));
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartRealAsync(terrain, animation);
        (ElevatorNetwork network, _) = Load();
        ElevatorLift tbLift = Lift(network, -1297f, 188f);
        var options = new PlayerbotOptions();

        // Two spots on the mesa top, 42 and 61 yards from the top landing, each with a mesh route to it: a landing is reachable. And the
        // two spots the test used to start from, (-1284.7, 250.3, 129.0) and (-1254.0, 224.2, 130.8): they lie beyond the mesa's western
        // edge (+Y is west), in the air over Mulgore (ground at 23.5 and 49.7, the nearest walkable polygon 75 yards below). Their only
        // "route" was a first leg dropping straight down to that polygon, which #1501 refuses on purpose (PathResult.StartFar and a
        // fall-height first leg): no landing is reachable from there.
        foreach ((Vector3 botPos, bool onMesa) in (ValueTuple<Vector3, bool>[])
        [
            (new(-1252.0f, 155.5f, 133.5f), true),
            (new(-1222.0f, 175.5f, 133.8f), true),
            (new(-1284.7f, 250.3f, 129.0f), false),
            (new(-1254.0f, 224.2f, 130.8f), false),
        ])
        {
            await world.PlaceAsync(botPos);
            // The nav tiles round the lift are read on the pool (NonBlockingTiles): a query that needs one still being read answers
            // "pending" (PathSearchCounters.TilesPending), which LandingBesideReachable reports as "no reachable landing". That is no
            // verdict, so ask again on a later think, as a bot does, until a call runs with no tile pending (bounded: a read that never
            // lands still fails the test, on the pending assertion below).
            (Vector3? reachable, bool onMesh, bool canPlan, long pendingTiles) = (null, false, false, 0L);
            int attempts = 0;
            do
            {
                if (attempts > 0) await Task.Delay(50);
                attempts++;
                (reachable, onMesh, canPlan, pendingTiles) = await world.OnWorldAsync(() =>
                {
                    long mark = PathSearchCounters.TilesPending;
                    Map kalimdor = world.Host.World.GetMap(1);
                    Player player = world.Player;
                    Vector3? found = null;
                    foreach (ElevatorCar car in tbLift.Cars)
                    {
                        found = PlayerbotElevator.LandingBesideReachable(player, kalimdor, car.At(ElevatorLevel.High), botPos, options);
                        if (found is not null) break;
                    }

                    bool mesh = found is not null && PlayerbotNavigation.OnWalkableMesh(kalimdor, found.Value);
                    bool plan = found is not null && PlayerbotNavigation.TryPlanToward(player, found.Value, options, out _);
                    return (found, mesh, plan, PathSearchCounters.TilesPending - mark);
                });
            }
            while (pendingTiles > 0 && attempts < MaxTileWaitAttempts);

            output.WriteLine($"TB from {botPos} -> landing {reachable}; attempts {attempts}, tiles pending in the last call {pendingTiles}");
            Assert.Equal(0, pendingTiles);
            if (!onMesa)
            {
                Assert.Null(reachable);
                Assert.False(await world.OnWorldAsync(() => PlayerbotNavigation.OnWalkableMesh(world.Host.World.GetMap(1), botPos)));
                continue;
            }

            Assert.NotNull(reachable);
            Assert.True(onMesh, $"Landing {reachable} is not on walkable mesh");
            Assert.True(canPlan, $"Cannot plan from {botPos} to {reachable}");
        }

        foreach ((Vector3 stop, Vector3 toward) in (ValueTuple<Vector3, Vector3>[])
        [
            (new(1595.24f, 189.647f, 55.65f), new(3200.7f, 341.0f, 32.1f)),
            (new(1596.22f, 291.798f, 55.65f), new(1574.7f, 308.5f, 61.7f)),
        ])
        {
            (Vector3? landing, bool onMesh) = await world.OnWorldAsync(() =>
            {
                Map ek = world.Host.World.GetMap(0);
                Vector3? found = PlayerbotElevator.LandingBeside(ek, stop, toward);
                return (found, found is not null && PlayerbotNavigation.OnWalkableMesh(ek, found.Value));
            });

            output.WriteLine($"UC stop {stop} toward {toward} -> landing {landing}");
            Assert.NotNull(landing);
            Assert.True(onMesh, $"UC landing {landing} for stop {stop} is not on walkable mesh");
        }

        // Drodouma (1574.7, 149.3, 61.7) and Zaugiog (1574.9, 308.5, 61.7) stand in the enclosed outer moat/garden ring outside the
        // Ruins of Lordaeron keep walls, which is disconnected from the inner courtyard where the upper lift doors open:
        // LandingBesideReachable must not fall back to a short route ending outside the wall, and TryBegin must refuse the lift ride.
        foreach ((Vector3 disconnectedPos, Vector3 stop) in (ValueTuple<Vector3, Vector3>[])
        [
            (new(1574.7f, 149.3f, 61.7f), new(1595.24f, 189.647f, 55.65f)),
            (new(1574.9f, 308.5f, 61.7f), new(1596.22f, 291.798f, 55.65f)),
        ])
        {
            await world.OnWorldAsync(() => world.Teleports.TeleportTo(world.Player, 0, disconnectedPos.X, disconnectedPos.Y, disconnectedPos.Z, 0));
            for (int i = 0; i < 200; i++)
            {
                bool arrived = await world.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(world.Session, world.Player);
                    return world.Player.IsInWorld && world.Teleports.StageOf(world.Player) is null && world.Player.MapId == 0
                        && Vector2.Distance(new(world.Player.X, world.Player.Y), new(disconnectedPos.X, disconnectedPos.Y)) < 1f;
                });
                if (arrived) break;
                await world.AdvanceAsync(50);
            }

            Vector3? reachable = null;
            bool begun = false;
            long pendingTiles = 0;
            for (int attempt = 0; attempt < MaxTileWaitAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(50);
                (reachable, begun, pendingTiles) = await world.OnWorldAsync(() =>
                {
                    long mark = PathSearchCounters.TilesPending;
                    Map ek = world.Host.World.GetMap(0);
                    Vector3? found = PlayerbotElevator.LandingBesideReachable(world.Player, ek, stop, disconnectedPos, options);
                    bool canBegin = PlayerbotElevator.TryBegin(world.Session, world.Player, new Vector3(1595.0f, 189.0f, -43.0f), options, out _);
                    if (canBegin) PlayerbotElevator.Cancel(world.Player);
                    return (found, canBegin, PathSearchCounters.TilesPending - mark);
                });
                if (pendingTiles == 0) break;
            }

            Assert.Equal(0, pendingTiles);
            Assert.Null(reachable);
            Assert.False(begun);
        }
    }

    private static ElevatorLift Lift(ElevatorNetwork network, float x, float y)
        => network.Lifts.Single(l => Vector2.Distance(l.Centre, new Vector2(x, y)) < 15f);
}
