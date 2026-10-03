using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Tests.GridTerrain;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>The navmesh reader and path queries on synthetic tiles.</summary>
public sealed class NavMeshTests
{
    private const uint MapId = 0;

    /// <summary>4×4 cells of 5 yd over world [0, 20)²; a wall of missing cells at X 10–15, Y 0–15 (gap at the top).</summary>
    private static CellTile Walled() => new(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, j) => !(i == 2 && j < 3) };

    private static readonly Vector3 WallStart = new(7, 5, 0);
    private static readonly Vector3 WallEnd = new(17, 5, 0);

    private static NavMeshPathfinder Load(NavMeshFixture fixture, params (int X, int Y, CellTile Tile)[] tiles)
    {
        fixture.WriteParams(MapId);
        foreach ((int x, int y, CellTile tile) in tiles)
        {
            fixture.WriteTile(MapId, x, y, tile);
        }

        var pathfinder = new NavMeshPathfinder(fixture.Directory);
        foreach ((int x, int y, _) in tiles)
        {
            Assert.True(pathfinder.LoadTile(MapId, x, y));
        }

        return pathfinder;
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.01f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void OpenGround_GivesTheStraightLine()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 10, 10)));

        PathResult path = nav.FindPath(MapId, new Vector3(2, 3, 0), new Vector3(18, 3, 0));
        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(2, path.Points.Count);
        Near(new Vector3(18, 3, 0), path.End);
        Assert.True(nav.Enabled);
    }

    [Fact]
    public void Path_GoesAroundTheObstacle_ThroughItsCorners()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));
        Vector3 start = WallStart;
        Vector3 end = WallEnd;

        PathResult path = nav.FindPath(MapId, start, end);
        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(4, path.Points.Count);
        Assert.Equal(start, path.Points[0]);
        Near(new Vector3(10, 15, 0), path.Points[1]);
        Near(new Vector3(15, 15, 0), path.Points[2]);
        Near(end, path.End);
        Assert.True(path.Length > 2 * Vector3.Distance(start, end));
    }

    [Fact]
    public void ExcludedFlags_AreAvoided_LikeMissingCells()
    {
        using var fixture = new NavMeshFixture();
        var steep = new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { Flags = (i, j) => i == 2 && j < 3 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground };
        NavMeshPathfinder nav = Load(fixture, (31, 31, steep));

        PathResult around = nav.FindPath(MapId, WallStart, WallEnd, new PathOptions { ExcludeFlags = NavTerrain.SteepSlopes }); // vmangos excludes steep slopes only on request
        Assert.Equal(4, around.Points.Count);
        Near(new Vector3(10, 15, 0), around.Points[1]);

        PathResult straight = nav.FindPath(MapId, WallStart, WallEnd, new PathOptions());
        Assert.Equal(2, straight.Points.Count);
    }

    [Fact]
    public void Path_CrossesIntoTheNeighbourTile()
    {
        using var fixture = new NavMeshFixture();
        // Detour tile (0, 1) lies at Recast +z, i.e. world +X.
        var west = new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { LinkedSides = [2] };
        var east = new CellTile(0, 1, 20, 0, 4, 4, Size: 5) { LinkedSides = [6], Walkable = (i, j) => !(i == 0 && j > 0) };
        NavMeshPathfinder nav = Load(fixture, (31, 31, west), (30, 31, east));

        PathResult straight = nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(38, 2, 0));
        Assert.Equal(PathType.Normal, straight.Type);
        Assert.Equal(2, straight.Points.Count);

        // A wall in the east tile (X 20–25, Y 5–20): the path crosses the border below it and bends
        // at the border portal's end and the wall's far corner.
        PathResult bent = nav.FindPath(MapId, new Vector3(17, 17, 0), new Vector3(27, 17, 0));
        Assert.Equal(PathType.Normal, bent.Type);
        Assert.Equal(4, bent.Points.Count);
        Near(new Vector3(20, 5, 0), bent.Points[1]);
        Near(new Vector3(25, 5, 0), bent.Points[2]);

        nav.UnloadTile(MapId, 30, 31);
        PathResult gone = nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(38, 2, 0));
        Assert.Equal(PathType.NoPath, gone.Type);
        Assert.False(gone.HasPath);
        Assert.Equal(2, gone.Points.Count); // the shortcut, for callers that still want to move
    }

    [Fact]
    public void UnlinkedTiles_DoNotConnect()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 5)), (30, 31, new CellTile(0, 1, 20, 0, 4, 4, Size: 5)));

        PathResult path = nav.FindPath(MapId, new Vector3(5, 5, 0), new Vector3(35, 5, 0), new PathOptions { AllowPartial = false });
        Assert.Equal(PathType.NoPath, path.Type);
    }

    [Fact]
    public void Unreachable_GivesThePartialPath_OrNoPath()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, _) => i != 2 }));

        PathResult partial = nav.FindPath(MapId, WallStart, WallEnd);
        Assert.Equal(PathType.Incomplete, partial.Type);
        Assert.True(partial.HasPath);
        Assert.True(partial.End.X <= 10.01f, $"ends on the near side, got {partial.End}");

        PathResult none = nav.FindPath(MapId, WallStart, WallEnd, new PathOptions { AllowPartial = false });
        Assert.Equal(PathType.NoPath, none.Type);
    }

    [Fact]
    public void EndInsideAHole_IsProjectedOntoTheMesh_AndIncomplete()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));

        PathResult path = nav.FindPath(MapId, WallStart, new Vector3(11.5f, 5, 0));
        Assert.Equal(PathType.Incomplete, path.Type);
        Near(new Vector3(10, 5, 0), path.End);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Heights_FollowTheMeshSurface(bool detailMesh)
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 10, 10) { Height = (x, _) => 0.5f * x, DetailMesh = detailMesh }));

        PathResult path = nav.FindPath(MapId, new Vector3(2, 3, 1.5f), new Vector3(17, 9, 9.9f));
        Assert.Equal(PathType.Normal, path.Type);
        Near(new Vector3(17, 9, 8.5f), path.End);

        // Above the mesh but inside the tall search box: projected down, flagged incomplete.
        PathResult high = nav.FindPath(MapId, new Vector3(2, 3, 1), new Vector3(17, 9, 60));
        Assert.Equal(PathType.Incomplete, high.Type);
        Near(new Vector3(17, 9, 8.5f), high.End);
    }

    [Fact]
    public void LongPaths_AreCutToMaxPoints()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));

        PathResult path = nav.FindPath(MapId, WallStart, WallEnd, new PathOptions { MaxPoints = 3 });
        Assert.Equal(PathType.Normal | PathType.Short, path.Type);
        Assert.Equal(3, path.Points.Count);
    }

    [Fact]
    public void SearchBudget_EndsInAPartialPath()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));

        PathResult path = nav.FindPath(MapId, WallStart, WallEnd, new PathOptions { MaxSearchNodes = 4 });
        Assert.Equal(PathType.Incomplete, path.Type);
    }

    [Fact]
    public void ShortLinks_AreAccepted()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled() with { LinkSize = 12 }));
        Assert.Equal(4, nav.FindPath(MapId, WallStart, WallEnd).Points.Count);
    }

    [Fact]
    public void MissingData_FallsBackGracefully()
    {
        using var fixture = new NavMeshFixture();
        var nav = new NavMeshPathfinder(fixture.Directory);

        // No .mmap: the map has no navmesh, paths are straight lines.
        Assert.False(nav.LoadTile(MapId, 31, 31));
        Assert.Null(nav.GetNavMesh(MapId));
        PathResult straight = nav.FindPath(MapId, new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, straight.Type);

        // A navmesh without the tile: an unloaded .mmtile is a straight shortcut, not an error (vmangos PathFinder.cpp:99-105).
        fixture.WriteParams(1);
        Assert.NotNull(nav.GetNavMesh(1));
        Assert.False(nav.LoadTile(1, 31, 31));
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, nav.FindPath(1, new Vector3(5, 5, 0), new Vector3(15, 5, 0)).Type);
        Assert.Equal(PathType.NoPath, nav.FindPath(1, new Vector3(float.NaN, 5, 0), new Vector3(15, 5, 0)).Type);
    }

    [Theory]
    [InlineData("params")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("vertex")]
    public void CorruptData_IsIgnored(string damage)
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        CellTile tile = damage == "vertex" ? Walled() with { CorruptVertexIndex = 5000 } : Walled();
        fixture.WriteTile(MapId, 31, 31, tile, mmapVersion: damage == "version" ? 4u : NavMeshFormat.MmapVersion);
        string tilePath = fixture.PathOf(NavMeshFormat.TileFileName(MapId, 31, 31));
        byte[] bytes = File.ReadAllBytes(tilePath);
        switch (damage)
        {
            case "params":
                File.WriteAllBytes(fixture.PathOf(NavMeshFormat.ParamsFileName(MapId)), [1, 2, 3]);
                break;
            case "magic":
                bytes[20] ^= 0xff;
                File.WriteAllBytes(tilePath, bytes);
                break;
            case "truncated":
                File.WriteAllBytes(tilePath, bytes[..^8]);
                break;
        }

        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.False(nav.LoadTile(MapId, 31, 31));
        PathResult path = nav.FindPath(MapId, WallStart, WallEnd);
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, path.Type); // no usable tile: straight (vmangos HaveTiles)
    }

    [Fact]
    public void TileForAnOccupiedDetourSlot_IsRefused()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));
        fixture.WriteTile(MapId, 31, 32, Walled());

        Assert.False(nav.LoadTile(MapId, 31, 32));
        Assert.Equal(1, nav.GetNavMesh(MapId)!.TileCount);
    }

    [Fact]
    public void Tiles_FollowTheMapGrids()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, Walled());
        using WorldRuntime world = TestWorld.CreateRuntime();
        var nav = new NavMeshPathfinder(fixture.Directory);
        WorldCollision.Of(world).Install(pathfinder: nav);
        Map map = world.GetMap(MapId);

        Assert.Equal(PathType.Normal | PathType.NotUsingPath, map.Collision.FindPath(WallStart, WallEnd).Type);
        map.AddObject(new TestUnit(1, 7, 5));
        Assert.True(nav.GetNavMesh(MapId)!.IsTerrainTileLoaded(31, 31));
        Assert.Equal(4, map.Collision.FindPath(WallStart, WallEnd).Points.Count);
    }

    [Fact]
    public void CreatureChase_WalksToTheNextCorner()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, Walled());
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(Content([Template()], [Spawn(1, WolfEntry, 7, 5, 0)]));
        using WorldRuntime world = runtime;
        world.AddPlayer(TestWorld.CreatePlayer(1, 7, 5, new FakeSession()));
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);

        // Without navmesh: straight to the destination.
        PathResult straight = CreaturePathing.MoveTowards(system, wolf, WallEnd, run: true);
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, straight.Type);
        Assert.Equal(17f, wolf.Spline!.EndX);

        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        WorldCollision.Of(world).Install(pathfinder: nav);
        PathResult path = CreaturePathing.MoveTowards(system, wolf, WallEnd, run: true);
        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(10f, wolf.Spline!.EndX, 2);
        Assert.Equal(15f, wolf.Spline!.EndY, 2);

        // Off the mesh: no path, no new spline.
        uint id = wolf.Spline!.Id;
        PathResult none = CreaturePathing.MoveTowards(system, wolf, new Vector3(300, 300, 0), run: true);
        Assert.Equal(PathType.NoPath, none.Type);
        Assert.Equal(id, wolf.Spline!.Id);
    }
}
