using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Issue #1497, rule (b) of <c>NavMeshPathfinder.RegionAt(..., out far)</c> on a synthetic tile: cells at X below 20 are walkable ground, cells from
/// X 20 on are steep slope (flag SteepSlopes alone, as the mmap generator writes it). A goal on the slope within reach of the walkable edge snaps to
/// the edge by the walking filter; it is far when the slope is over a yard nearer than that hit.
/// </summary>
public sealed class PlayerbotExcludedGroundRegionTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly (int X, int Y, CellTile Tile)[] Slope =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { Flags = (i, _) => i >= 4 ? NavTerrain.SteepSlopes : NavTerrain.Ground }),
    ];

    private static PathOptions Walking(float tolerance = 0, bool onlyInSearch = false)
        => new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, EndTolerance = tolerance, ExcludeOnlyInSearch = onlyInSearch };

    private async Task<(int Region, bool Far)> LocateAsync(Vector3 goal, PathOptions options, bool plain = false)
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Slope);
        return await world.Host.OnWorldAsync(() =>
        {
            NavRegionGraph graph = world.Nav.RegionGraph(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
            if (plain) return (world.Nav.RegionAt(graph, goal, options), false);
            int region = world.Nav.RegionAt(graph, goal, options, out bool far);
            return (region, far);
        });
    }

    [Fact]
    public async Task AGoalOnTheSlopeOverAYardFromTheWalkableEdge_IsFar_AndSnapsToTheEdgeRegion()
    {
        (int region, bool far) = await LocateAsync(new Vector3(22.5f, 22.5f, 0), Walking());
        Assert.True(region >= 0, "the walking filter snaps the goal to the walkable edge");
        Assert.True(far, "the slope is 2.5 yd nearer than the walking hit");
    }

    [Fact]
    public async Task AGoalWithinTheMarginOfTheEdge_IsNotFar()
    {
        (int region, bool far) = await LocateAsync(new Vector3(20.5f, 22.5f, 0), Walking());
        Assert.True(region >= 0);
        Assert.False(far, "the slope is only 0.5 yd nearer than the walking hit");
    }

    [Fact]
    public async Task AGoalOnTheWalkableSide_IsNotFar()
    {
        (int region, bool far) = await LocateAsync(new Vector3(10, 22.5f, 0), Walking());
        Assert.True(region >= 0);
        Assert.False(far);
    }

    [Fact]
    public async Task ATolerantQuery_NeverTakesTheSlopeRule()
    {
        (_, bool far) = await LocateAsync(new Vector3(22.5f, 22.5f, 0), Walking(tolerance: 3));
        Assert.False(far);
    }

    [Fact]
    public async Task AnExcludeOnlyInSearchQuery_NeverTakesTheSlopeRule()
    {
        (_, bool far) = await LocateAsync(new Vector3(22.5f, 22.5f, 0), Walking(onlyInSearch: true));
        Assert.False(far);
    }

    [Fact]
    public async Task ThePlainOverload_ReturnsTheSameRegion_AsTheFarOverload()
    {
        (int plain, _) = await LocateAsync(new Vector3(22.5f, 22.5f, 0), Walking(), plain: true);
        (int withFar, bool far) = await LocateAsync(new Vector3(22.5f, 22.5f, 0), Walking());
        Assert.Equal(withFar, plain);
        Assert.True(far);
    }
}
