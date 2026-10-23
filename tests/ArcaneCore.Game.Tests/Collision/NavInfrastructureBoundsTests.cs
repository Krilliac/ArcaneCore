using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The bounds on <see cref="NavMeshPathfinder"/>'s background work and caches: region graph builds are deduplicated per map and capped
/// in concurrency, a failed build is not retried, and the on-demand tile cache (<c>EnsureTile</c>) keeps at most
/// <see cref="NavMeshPathfinder.MaxOnDemandTiles"/> tiles per map, releasing the one used longest ago.
/// </summary>
public sealed class NavInfrastructureBoundsTests : IDisposable
{
    private static readonly NavTerrain Include = PathMover.Player.IncludeFlags;
    private static readonly NavTerrain Exclude = NavTerrain.SteepSlopes;

    // Guards against a hang, not behaviour: pool work can wait many seconds behind other tests' blocked threads in a busy full-suite run.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(120);
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private NavMeshPathfinder WriteMaps(int maps)
    {
        for (uint map = 0; map < maps; map++)
        {
            _fixture.WriteParams(map);
        }

        _fixture.WriteTile(0, 10, 10, new CellTile(0, 0, 0, 0, 2, 2, Size: 5));
        var nav = new NavMeshPathfinder(_fixture.Directory);
        for (uint map = 0; map < maps; map++)
        {
            Assert.True(nav.HasNavigationData(map)); // resolve the meshes on this thread, as the world thread would
        }

        return nav;
    }

    [Fact]
    public async Task ConcurrentRequestsForOneMap_CauseExactlyOneBuild()
    {
        NavMeshPathfinder nav = WriteMaps(1);
        NavRegionGraph real = NavRegionGraph.Build(_fixture.Directory, 0, Include, Exclude);
        int builds = 0;
        nav.RegionGraphBuilder = (_, _, _, _) =>
        {
            Interlocked.Increment(ref builds);
            Thread.Sleep(50);
            return real;
        };

        using var start = new ManualResetEventSlim();
        Task<Task<NavRegionGraph?>?>[] requests = Enumerable.Range(0, 32)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                return nav.RegionGraphTask(0, Include, Exclude);
            }, TaskCreationOptions.LongRunning)).ToArray();
        start.Set();
        Task<NavRegionGraph?>?[] tasks = await Task.WhenAll(requests);

        Assert.All(tasks, t => Assert.Same(tasks[0], t));
        Assert.Same(real, await tasks[0]!);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task BuildsOfDifferentMaps_NeverRunMoreThanTheCap_AndAllFinish()
    {
        const int maps = 6;
        NavMeshPathfinder nav = WriteMaps(maps);
        NavRegionGraph real = NavRegionGraph.Build(_fixture.Directory, 0, Include, Exclude);
        int running = 0, peak = 0, started = 0;
        using var gate = new ManualResetEventSlim();
        nav.RegionGraphBuilder = (_, _, _, _) =>
        {
            Interlocked.Increment(ref started);
            int now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            gate.Wait();
            Interlocked.Decrement(ref running);
            return real;
        };

        Task<NavRegionGraph?>[] tasks = Enumerable.Range(0, maps).Select(m => nav.RegionGraphTask((uint)m, Include, Exclude)!).ToArray();

        // The cap's worth start and block; the rest must wait for a slot, not start.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) >= 2, HangGuard));
        await Task.Delay(300);
        Assert.Equal(2, Volatile.Read(ref started));

        gate.Set();
        NavRegionGraph?[] graphs = await Task.WhenAll(tasks);

        Assert.All(graphs, g => Assert.Same(real, g));
        Assert.Equal(maps, started);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task AFailedBuild_AnswersNoGraph_AndIsNotRetried()
    {
        NavMeshPathfinder nav = WriteMaps(1);
        int builds = 0;
        nav.RegionGraphBuilder = (_, _, _, _) =>
        {
            Interlocked.Increment(ref builds);
            throw new InvalidDataException("corrupt tile");
        };

        Assert.Null(await nav.RegionGraphTask(0, Include, Exclude)!);
        for (int i = 0; i < 50; i++)
        {
            Assert.Null(nav.RegionGraph(0, Include, Exclude));
        }

        Assert.Null(await nav.RegionGraphTask(0, Include, Exclude)!);
        Assert.Equal(1, builds);
    }

    private const int TileRow = 30;

    private static Vector3 CenterOf(int terrainX) => new((32f - terrainX - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);

    private NavMeshPathfinder WriteTileRow(uint map, int tiles)
    {
        _fixture.WriteParams(map);
        for (int x = 0; x < tiles; x++)
        {
            _fixture.WriteTile(map, x, TileRow, new CellTile(x, 0, 0, 0, 2, 2, Size: 5));
        }

        return new NavMeshPathfinder(_fixture.Directory);
    }

    private static readonly PathOptions Loading = new() { LoadTiles = true, AllowPartial = true };

    private static void Touch(NavMeshPathfinder nav, uint map, int terrainX)
    {
        Vector3 at = CenterOf(terrainX);
        nav.FindPath(map, at, at, Loading);
    }

    [Fact]
    public void OnDemandTiles_AreBoundedPerMap_AndTheOneUsedLongestAgoIsReleasedFirst()
    {
        int extra = NavMeshPathfinder.MaxOnDemandTiles + 8;
        NavMeshPathfinder nav = WriteTileRow(0, extra);
        NavMesh mesh = nav.GetNavMesh(0)!;

        for (int x = 0; x < NavMeshPathfinder.MaxOnDemandTiles; x++)
        {
            Touch(nav, 0, x);
        }

        Assert.Equal(NavMeshPathfinder.MaxOnDemandTiles, nav.OnDemandTileCount);

        Touch(nav, 0, 0); // tile 0 is now the most recently used; tile 1 the oldest
        Touch(nav, 0, NavMeshPathfinder.MaxOnDemandTiles); // one over: releases tile 1

        Assert.Equal(NavMeshPathfinder.MaxOnDemandTiles, nav.OnDemandTileCount);
        Assert.True(mesh.IsTerrainTileLoaded(0, TileRow));
        Assert.False(mesh.IsTerrainTileLoaded(1, TileRow));
        Assert.True(mesh.IsTerrainTileLoaded(2, TileRow));
        Assert.True(mesh.IsTerrainTileLoaded(NavMeshPathfinder.MaxOnDemandTiles, TileRow));

        for (int x = NavMeshPathfinder.MaxOnDemandTiles + 1; x < extra; x++)
        {
            Touch(nav, 0, x);
        }

        Assert.Equal(NavMeshPathfinder.MaxOnDemandTiles, nav.OnDemandTileCount);
    }

    [Fact]
    public void OnDemandBound_CountsEachMapSeparately_AndAGridTakingATileFreesItsSlot()
    {
        int tiles = NavMeshPathfinder.MaxOnDemandTiles;
        NavMeshPathfinder nav = WriteTileRow(0, tiles + 1);
        _fixture.WriteParams(1);
        for (int x = 0; x < tiles; x++)
        {
            _fixture.WriteTile(1, x, TileRow, new CellTile(x, 0, 0, 0, 2, 2, Size: 5));
        }

        for (int x = 0; x < tiles; x++)
        {
            Touch(nav, 0, x);
        }

        for (int x = 0; x < tiles; x++)
        {
            Touch(nav, 1, x); // a full map 0 does not stop map 1 loading
        }

        Assert.Equal(2 * tiles, nav.OnDemandTileCount);
        NavMesh mesh0 = nav.GetNavMesh(0)!;
        Assert.True(mesh0.IsTerrainTileLoaded(0, TileRow));

        // A grid taking tile 5 of map 0 releases its on-demand slot, so one more tile loads without evicting anything.
        ICollisionTileLifecycle lifecycle = nav;
        lifecycle.OnTileLoaded(0, 5, TileRow);
        Assert.Equal((2 * tiles) - 1, nav.OnDemandTileCount);
        Touch(nav, 0, tiles);

        Assert.Equal(2 * tiles, nav.OnDemandTileCount);
        Assert.True(mesh0.IsTerrainTileLoaded(0, TileRow)); // nothing was evicted for it
        Assert.True(mesh0.IsTerrainTileLoaded(tiles, TileRow));

        lifecycle.OnTileUnloaded(0, 6, TileRow); // an unloaded grid tile frees its on-demand slot too
        Assert.Equal((2 * tiles) - 1, nav.OnDemandTileCount);
    }
}
