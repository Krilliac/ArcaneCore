using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Tests.GridTerrain;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The world's vmap and navmesh services are shared by every instance of a map id (vmangos keeps one <c>StaticMapTree</c> and one
/// navmesh per map id for all instances, VMapManager2.cpp:89-103, MoveMap.cpp:123-131). A tile goes only when no instance has a grid
/// on it any more:
/// vmangos counts the grids of every instance on the map id's <c>TerrainInfo</c> (<c>RefGrid</c>/<c>UnrefGrid</c>,
/// GridMap.cpp:792-812) and unloads the vmap and mmap tiles only when that count is 0 (GridMap.cpp:769-785). Two battlegrounds of
/// the same map, or two groups in the same raid, must not take each other's walls and floors away.
/// </summary>
public sealed class InstanceSharedTileTests
{
    private const uint MapId = 0;

    // World (100, 100) lies in terrain tile (31, 31).
    private static readonly GridCoord WallGrid = GridDefines.ComputeGridCoord(100, 100);

    [Fact]
    public void VMapTile_StaysLoaded_WhileAnotherInstanceHoldsItsGrid()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), new Vector3(100, 100, 0));
        fixture.Write(MapId);
        using WorldRuntime world = TestWorld.CreateRuntime();
        var vmaps = new VMapManager(fixture.Directory);
        WorldCollision.Of(world).Install(vmaps);
        Map first = world.GetMap(MapId, 1);
        Map second = world.GetMap(MapId, 2);
        first.AddObject(new TestUnit(1, 100, 100));
        second.AddObject(new TestUnit(2, 100, 100));
        Assert.False(second.Collision.IsInLineOfSight(95, 100, 2, 105, 100, 2));

        Assert.True(first.Grids.UnloadGrid(WallGrid, force: true));
        Assert.True(vmaps.GetTree(MapId)!.IsTileLoaded(31, 31), "the second instance still has a grid on the tile");
        Assert.False(second.Collision.IsInLineOfSight(95, 100, 2, 105, 100, 2));

        Assert.True(second.Grids.UnloadGrid(WallGrid, force: true));
        Assert.False(vmaps.GetTree(MapId)!.IsTileLoaded(31, 31), "the last grid on the tile releases it");
        Assert.True(second.Collision.IsInLineOfSight(95, 100, 2, 105, 100, 2));
    }

    [Fact]
    public void NavMeshTile_StaysLoaded_WhileAnotherInstanceHoldsItsGrid()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 4, 4));
        using WorldRuntime world = TestWorld.CreateRuntime();
        var mesh = new NavMeshPathfinder(fixture.Directory);
        WorldCollision.Of(world).Install(pathfinder: mesh);
        Map first = world.GetMap(MapId, 1);
        Map second = world.GetMap(MapId, 2);
        first.AddObject(new TestUnit(1, 100, 100));
        second.AddObject(new TestUnit(2, 100, 100));
        Assert.True(mesh.GetNavMesh(MapId)!.IsTerrainTileLoaded(31, 31));

        Assert.True(first.Grids.UnloadGrid(WallGrid, force: true));
        Assert.True(mesh.GetNavMesh(MapId)!.IsTerrainTileLoaded(31, 31), "the second instance still has a grid on the tile");

        Assert.True(second.Grids.UnloadGrid(WallGrid, force: true));
        Assert.False(mesh.GetNavMesh(MapId)!.IsTerrainTileLoaded(31, 31), "the last grid on the tile releases it");
    }
}
