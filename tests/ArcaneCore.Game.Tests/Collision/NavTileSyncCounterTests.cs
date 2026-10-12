using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The counters behind the slow-map-update line's "navmesh tile reads on world thread": on-demand reads and grid tile loads both count,
/// the slowest is remembered, a tile that is already resident costs nothing. Also pins that creature path queries (no
/// <see cref="PathOptions.LoadTiles"/>) never read a tile file on the calling thread, which is why creature movement needs no
/// <see cref="PathOptions.NonBlockingTiles"/>.
/// </summary>
public sealed class NavTileSyncCounterTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();
    private int _tileReads;

    public void Dispose() => _fixture.Dispose();

    private NavMeshPathfinder Pathfinder()
    {
        _fixture.WriteParams(0);
        _fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 10, 10));
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                if (path.EndsWith(".mmtile", StringComparison.Ordinal)) Interlocked.Increment(ref _tileReads);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };
        Assert.True(nav.HasNavigationData(0));
        return nav;
    }

    [Fact]
    public void GridTileLoad_IsCounted_WithItsSlowestTile_AndAResidentTileIsNot()
    {
        NavMeshPathfinder nav = Pathfinder();
        PathSearchTotals before = PathSearchCounters.Current;
        (long processReads, _) = PathSearchCounters.ProcessTileReads;
        PathSearchCounters.ResetSlowestSyncTile();

        ((ICollisionTileLifecycle)nav).OnTileLoaded(0, 31, 31);

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(1, spent.GridTileLoads);
        Assert.Equal(0, spent.TileLoads);
        Assert.Equal(1, spent.SyncTileReads);
        Assert.True(spent.GridTileLoadTicks > 0);
        Assert.True(spent.SyncTileReadMs > 0);
        Assert.Equal(1, _tileReads);
        TileLoadDetail slowest = PathSearchCounters.SlowestSyncTile;
        Assert.Equal((0u, 31, 31), (slowest.MapId, slowest.X, slowest.Y));
        Assert.False(slowest.PrefetchHit);
        // Process-wide and shared with tests running in parallel: other classes may add reads, never remove ours.
        Assert.True(PathSearchCounters.ProcessTileReads.Reads >= processReads + 1);

        // The same tile again (a second grid over it): resident, so nothing more is counted.
        ((ICollisionTileLifecycle)nav).OnTileLoaded(0, 31, 31);
        Assert.Equal(1, (PathSearchCounters.Current - before).SyncTileReads);
        Assert.Equal(1, _tileReads);
    }

    [Fact]
    public void ATileThatInstallsNothing_IsAFailure_NotAnInstall()
    {
        NavMeshPathfinder nav = Pathfinder();
        PathSearchTotals before = PathSearchCounters.Current;

        ((ICollisionTileLifecycle)nav).OnTileLoaded(0, 5, 5); // no such tile file

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(0, spent.SyncTileReads);
        Assert.Equal(1, spent.TileLoadFailures);
    }

    [Fact]
    public void OnDemandRead_IsCounted_AsASyncTileRead()
    {
        NavMeshPathfinder nav = Pathfinder();
        PathSearchTotals before = PathSearchCounters.Current;

        PathResult path = nav.FindPath(0, new Vector3(2, 3, 0), new Vector3(18, 3, 0), new PathOptions { LoadTiles = true });

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(1, spent.TileLoads);
        Assert.Equal(0, spent.GridTileLoads);
        Assert.Equal(1, spent.SyncTileReads);
        Assert.Equal(1, _tileReads);
    }

    [Fact]
    public void CreatureStylePathQuery_NeverReadsATile_WhateverIsResident()
    {
        NavMeshPathfinder nav = Pathfinder();
        PathSearchTotals before = PathSearchCounters.Current;

        // Creature movement asks with the default options (CreatureMapSystem.SearchPath, CreaturePathing, ForcedMovement): the tile is not resident.
        PathResult cold = nav.FindPath(0, new Vector3(2, 3, 0), new Vector3(18, 3, 0));
        Assert.Equal(0, _tileReads);
        Assert.Equal(0, (PathSearchCounters.Current - before).SyncTileReads);
        Assert.Equal(0, (PathSearchCounters.Current - before).TilesPending);
        Assert.False(nav.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));
        Assert.True(cold.Points.Count >= 2); // the straight line vmangos answers for a tile no grid holds

        // Once the grid holds the tile the same query finds the mesh path, still without a read of its own.
        ((ICollisionTileLifecycle)nav).OnTileLoaded(0, 31, 31);
        int readsAfterGrid = _tileReads;
        PathResult warm = nav.FindPath(0, new Vector3(2, 3, 0), new Vector3(18, 3, 0));
        Assert.Equal(PathType.Normal, warm.Type);
        Assert.Equal(readsAfterGrid, _tileReads);
    }
}
