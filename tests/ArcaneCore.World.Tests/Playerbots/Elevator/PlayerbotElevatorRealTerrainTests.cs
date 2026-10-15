using System.Numerics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Transports;
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

    private static ElevatorLift Lift(ElevatorNetwork network, float x, float y)
        => network.Lifts.Single(l => Vector2.Distance(l.Centre, new Vector2(x, y)) < 15f);
}
