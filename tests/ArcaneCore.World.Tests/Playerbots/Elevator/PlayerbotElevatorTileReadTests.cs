using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The lift choice never reads a terrain tile on the world thread. The live hang stacks of 2026-10-25/26 (five captures, 3.0-8.5 s frames
/// with no GC) had the world thread in <c>File.ReadAllBytes</c> under <c>ElevatorSurvey.RegionAt</c> for a bot's goal on a tile no map
/// held: <see cref="ElevatorNetwork.Choose(uint, Vector3, Vector3, ElevatorSurvey)"/> asks the region of the goal for every lift of the map.
/// </summary>
public sealed class PlayerbotElevatorTileReadTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-elevator-tiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    /// <summary>
    /// The survey of Kalimdor asked about Ratchet (Wharfmaster Dizzywig, quest 1111's ender), whose tile no grid holds: no tile file is
    /// read or cached on the calling thread, then and later; the region (The Barrens, Ratchet) comes from the background read, and it is
    /// the one the full lookup (which loads the tile) gives.
    /// </summary>
    [Fact]
    public void TheSurveyOfAGoalOnATileNoGridHolds_ReadsNoTileOnTheCallingThread()
    {
        Vector3 goal = ElevatorTestData.Dizzywig;
        (int tx, int ty) = TerrainTile.TileOf(goal.X, goal.Y)!.Value;
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(ElevatorTestData.Kalimdor, tx, ty)), new MapFileBuilder { GridArea = 500 }.Build());

        var options = new WorldRuntimeOptions { AutosaveIntervalMs = 0 };
        options.Maps.DataDirectory = _dataDir;
        using var world = new WorldRuntime(options, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        terrain.Areas = new AreaTable([new AreaTemplate(17, 1, 0, 501, 0, 1, "The Barrens", 0, 0),
            new AreaTemplate(392, 1, 17, 500, 0, 1, "Ratchet", 0, 0)], MapRegistry.Default);
        Map kalimdor = world.GetMap(ElevatorTestData.Kalimdor);
        ElevatorSurvey survey = ElevatorSurvey.Of(kalimdor);

        (uint Zone, uint Area) region = survey.RegionAt(goal);
        Assert.Equal(0, terrain.FilesLoaded);
        Assert.False(kalimdor.Terrain.IsTileLoaded(tx, ty));

        var clock = Stopwatch.StartNew();
        while (region != (17u, 392u) && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(5);
            region = survey.RegionAt(goal);
        }

        Assert.Equal((17u, 392u), region);
        Assert.Equal(0, terrain.FilesLoaded);
        Assert.False(kalimdor.Terrain.IsTileLoaded(tx, ty));
        Assert.Equal(region, kalimdor.GetZoneAndAreaId(goal.X, goal.Y, goal.Z)); // the full lookup (it reads the tile) agrees
        Assert.Equal(1, terrain.FilesLoaded);
    }

    /// <summary>
    /// While the goal's region is not known, no lift is chosen and the choice is pending (not "no lift is the way"); once it is known, the
    /// Great Lift is chosen from the canyon floor towards Ratchet as on the fixture zones (<see cref="PlayerbotElevatorNetworkTests"/>).
    /// </summary>
    [Fact]
    public void AGoalWhoseRegionIsNotKnownYet_LeavesTheChoicePending_ThenTheLiftIsChosen()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        ElevatorLift great = network.Lifts.Single(l => Vector2.Distance(l.Centre, new Vector2(-4668f, -1839f)) < 15f);
        bool goalKnown = false;
        int asked = 0;
        var survey = new ElevatorSurvey((Vector3 at, out (uint Zone, uint Area) region) =>
        {
            bool isGoal = Vector3.Distance(at, ElevatorTestData.Dizzywig) < 1f;
            asked += isGoal ? 1 : 0;
            region = isGoal ? (17u, 392u) : (400u, 0u);
            return !isGoal || goalKnown;
        });

        Assert.Null(network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.Dizzywig, survey, out bool pending));
        Assert.True(pending);
        Assert.True(asked > 0);
        Assert.Equal((0u, 0u), survey.RegionAt(ElevatorTestData.Dizzywig)); // RegionAt: unknown reads as no region

        goalKnown = true;
        Assert.Equal((great, ElevatorLevel.Low),
            network.Choose(ElevatorTestData.Kalimdor, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.Dizzywig, survey, out pending));
        Assert.False(pending);

        // Another map: no lift is weighed, so nothing waits.
        goalKnown = false;
        Assert.Null(network.Choose(0, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.Dizzywig, survey, out pending));
        Assert.False(pending);
    }
}
