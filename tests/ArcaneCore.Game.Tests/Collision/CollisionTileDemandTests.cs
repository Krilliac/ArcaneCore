using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Tiles loaded for a question about a place no grid covers (<see cref="ICollisionTileDemand"/>): the model tiles of
/// <see cref="VMapManager"/> are held at most <see cref="VMapManager.MaxOnDemandTiles"/> per map, the one asked for longest ago released
/// first; the navigation tiles of <see cref="NavMeshPathfinder"/> join those a query with <see cref="PathOptions.LoadTiles"/> reads; a
/// grid's tile is the grid's in both. One row of synthetic tiles on map 0.
/// </summary>
public sealed class CollisionTileDemandTests : IDisposable
{
    private const uint MapId = 0;
    private const int TileRow = 30;
    private const int Tiles = VMapManager.MaxOnDemandTiles + 8;

    /// <summary>Navigation tiles enough to fill the on-demand ones past their bound beside one a grid holds.</summary>
    private const int MeshTiles = NavMeshPathfinder.MaxOnDemandTiles + 2;

    private readonly VMapFixture _models = new();
    private readonly NavMeshFixture _mesh = new();

    public CollisionTileDemandTests()
    {
        // A wall on the first tile gives the map its model tree; every other tile is a model tile with nothing in it.
        _models.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(0));
        _models.Write(MapId);
        for (int x = 1; x < Tiles; x++)
        {
            _models.WriteTile(MapId, x, TileRow, []);
        }

        _mesh.WriteParams(MapId);
        for (int x = 0; x < MeshTiles; x++)
        {
            _mesh.WriteTile(MapId, x, TileRow, new CellTile(x, 0, 0, 0, 2, 2, Size: 5));
        }
    }

    public void Dispose()
    {
        _models.Dispose();
        _mesh.Dispose();
    }

    /// <summary>The centre of terrain tile (<paramref name="tileX"/>, <see cref="TileRow"/>).</summary>
    private static Vector3 On(int tileX) => new((32f - tileX - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0f);

    private static bool Demand(ICollisionTileDemand service, int tileX) => service.LoadTileOnDemand(MapId, On(tileX).X, On(tileX).Y);

    [Fact]
    public void OnDemandModelTiles_AreBoundedPerMap_AndTheOneAskedForLongestAgoIsReleasedFirst()
    {
        var vmaps = new VMapManager(_models.Directory);
        VMapTree tree = vmaps.GetTree(MapId)!;
        for (int x = 0; x < VMapManager.MaxOnDemandTiles; x++)
        {
            Assert.True(Demand(vmaps, x));
        }

        Assert.Equal(VMapManager.MaxOnDemandTiles, vmaps.OnDemandTileCount);
        Assert.False(vmaps.IsInLineOfSight(MapId, On(0) - new Vector3(5, 0, -2), On(0) + new Vector3(5, 0, 2)), "the wall's tile is loaded");

        Assert.True(Demand(vmaps, 0)); // asked for again: now tile 1 is the one asked for longest ago
        Assert.True(Demand(vmaps, VMapManager.MaxOnDemandTiles)); // one over: releases tile 1

        Assert.Equal(VMapManager.MaxOnDemandTiles, vmaps.OnDemandTileCount);
        Assert.True(tree.IsTileLoaded(0, TileRow));
        Assert.False(tree.IsTileLoaded(1, TileRow));
        Assert.True(tree.IsTileLoaded(2, TileRow));
        Assert.True(tree.IsTileLoaded(VMapManager.MaxOnDemandTiles, TileRow));

        for (int x = VMapManager.MaxOnDemandTiles + 1; x < Tiles; x++)
        {
            Assert.True(Demand(vmaps, x));
        }

        Assert.Equal(VMapManager.MaxOnDemandTiles, vmaps.OnDemandTileCount);
        Assert.Equal(VMapManager.MaxOnDemandTiles, tree.LoadedTiles.Count);
    }

    [Fact]
    public void AGridTakesAnOnDemandModelTileOver_AndAGridsTileIsNeverReleasedForRoom()
    {
        var vmaps = new VMapManager(_models.Directory);
        ICollisionTileLifecycle grids = vmaps;
        VMapTree tree = vmaps.GetTree(MapId)!;

        Assert.True(Demand(vmaps, 0));
        grids.OnTileLoaded(MapId, 0, TileRow); // a grid is created on the tile held on demand: the grid holds it now
        grids.OnTileLoaded(MapId, 1, TileRow);
        Assert.True(Demand(vmaps, 1)); // asked for where a grid stands: left to the grid
        Assert.Equal(0, vmaps.OnDemandTileCount);

        for (int x = 2; x < Tiles; x++)
        {
            Assert.True(Demand(vmaps, x));
        }

        Assert.Equal(VMapManager.MaxOnDemandTiles, vmaps.OnDemandTileCount);
        Assert.True(tree.IsTileLoaded(0, TileRow), "a grid's tile was released to make room");
        Assert.True(tree.IsTileLoaded(1, TileRow), "a grid's tile was released to make room");

        grids.OnTileUnloaded(MapId, 0, TileRow); // the grid's unload releases it
        Assert.False(tree.IsTileLoaded(0, TileRow));
    }

    [Fact]
    public void AnEmptyModelTileHeldOnDemand_IsForgottenWhenReleased()
    {
        File.Delete(_models.PathOf(VMapFormat.TileFileName(MapId, 1, TileRow)));
        var vmaps = new VMapManager(_models.Directory);

        Assert.False(Demand(vmaps, 1)); // no vmtile: loaded, and nothing in it
        Assert.Equal(ModelCoverage.Loaded, vmaps.CoverageAt(MapId, 1, TileRow));
        Assert.Equal(1, vmaps.OnDemandTileCount);

        for (int x = 2; x <= VMapManager.MaxOnDemandTiles + 1; x++)
        {
            Assert.True(Demand(vmaps, x));
        }

        Assert.Equal(ModelCoverage.TileNotLoaded, vmaps.CoverageAt(MapId, 1, TileRow));
        Assert.Equal(VMapManager.MaxOnDemandTiles, vmaps.OnDemandTileCount);
    }

    [Fact]
    public void AnOnDemandNavigationTile_IsOneOfTheTilesQueriesRead_AndAGridsTileStaysTheGrids()
    {
        var nav = new NavMeshPathfinder(_mesh.Directory);
        ICollisionTileLifecycle grids = nav;
        NavMesh mesh = nav.GetNavMesh(MapId)!;

        Assert.True(Demand(nav, 0));
        Assert.Equal(1, nav.OnDemandTileCount);
        grids.OnTileLoaded(MapId, 1, TileRow);
        Assert.True(Demand(nav, 1));
        Assert.Equal(1, nav.OnDemandTileCount); // the grid's tile is not counted

        var loading = new PathOptions { LoadTiles = true, AllowPartial = true };
        for (int x = 2; x < MeshTiles; x++)
        {
            nav.FindPath(MapId, On(x), On(x), loading);
        }

        Assert.True(mesh.IsTerrainTileLoaded(1, TileRow), "a grid's tile was released to make room");
        Assert.False(mesh.IsTerrainTileLoaded(0, TileRow), "the tile loaded on demand was held as a grid's");
    }

    [Fact]
    public void MapCollision_LoadsTheTilesOfBothServicesOnDemand()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var vmaps = new VMapManager(_models.Directory);
        var nav = new NavMeshPathfinder(_mesh.Directory);
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        Map map = world.GetMap(MapId);

        map.Collision.LoadTilesOnDemand(On(0).X, On(0).Y);

        Assert.True(vmaps.GetTree(MapId)!.IsTileLoaded(0, TileRow));
        Assert.True(nav.GetNavMesh(MapId)!.IsTerrainTileLoaded(0, TileRow));
        Assert.Equal(1, vmaps.OnDemandTileCount);
        Assert.Equal(1, nav.OnDemandTileCount);

        map.Collision.LoadTilesOnDemand(float.NaN, 0f); // outside the map: nothing
        Assert.Equal(1, vmaps.OnDemandTileCount);
        Assert.Equal(1, nav.OnDemandTileCount);
    }
}
