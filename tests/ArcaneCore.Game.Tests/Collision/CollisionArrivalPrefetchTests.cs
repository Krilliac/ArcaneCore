using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The arrival prefetch of a teleport (<see cref="ICollisionTilePrefetch.PrefetchArrival"/>) on the model and navigation loaders: the
/// destination's tile is read on the thread pool and the grid's own load then takes it (a prefetch hit); and arrivals have a budget of their
/// own in <see cref="TilePrefetchCache{T}"/>, so a burst of teleports never evicts the prefetches of players approaching grids.
/// </summary>
public sealed class CollisionArrivalPrefetchTests : IDisposable
{
    private const uint MapId = 0;
    private const int TileX = 31;
    private const int TileY = 31;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly VMapFixture _models = new();
    private readonly NavMeshFixture _mesh = new();

    public void Dispose()
    {
        _models.Dispose();
        _mesh.Dispose();
    }

    private static void WaitUntil(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < ReadTimeout)
        {
            Thread.Sleep(5);
        }

        Assert.True(done());
    }

    [Fact]
    public void TheModelLoader_ReadsAnArrivalTileInTheBackground_AndTheGridsLoadTakesIt()
    {
        // A wall on one tile gives the map its model tree; the arrival tile is a model tile with nothing in it.
        _models.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), new Vector3(0, 0, 0));
        _models.Write(MapId);
        _models.WriteTile(MapId, TileX, TileY, []);
        var vmaps = new VMapManager(_models.Directory);
        Assert.NotNull(vmaps.GetTree(MapId));
        int before = vmaps.PrefetchHits;

        ((ICollisionTilePrefetch)vmaps).PrefetchArrival(MapId, TileX, TileY);
        WaitUntil(() => vmaps.IsPrefetchFinished(MapId, TileX, TileY));
        ((ICollisionTileLifecycle)vmaps).OnTileLoaded(MapId, TileX, TileY);

        Assert.Equal(before + 1, vmaps.PrefetchHits);
        Assert.True(vmaps.GetTree(MapId)!.IsTileLoaded(TileX, TileY));
        Assert.False(vmaps.IsPrefetchFinished(MapId, TileX, TileY));
    }

    [Fact]
    public void TheNavigationLoader_ReadsAnArrivalTileInTheBackground_AndTheGridsLoadTakesIt()
    {
        _mesh.WriteParams(MapId);
        _mesh.WriteTile(MapId, TileX, TileY, new CellTile(0, 0, 0, 0, 10, 10));
        var nav = new NavMeshPathfinder(_mesh.Directory);
        Assert.True(nav.HasNavigationData(MapId));
        int before = nav.PrefetchHits;

        ((ICollisionTilePrefetch)nav).PrefetchArrival(MapId, TileX, TileY);
        WaitUntil(() => nav.IsPrefetchFinished(MapId, TileX, TileY));
        ((ICollisionTileLifecycle)nav).OnTileLoaded(MapId, TileX, TileY);

        Assert.Equal(before + 1, nav.PrefetchHits);
        Assert.False(nav.IsPrefetchFinished(MapId, TileX, TileY));
    }

    /// <summary>
    /// Arrivals fill their own <see cref="TilePrefetchCache{T}.MaxArrivalEntries"/>: a burst of them evicts the oldest arrivals only, and the
    /// approach prefetches (<see cref="TilePrefetchCache{T}.MaxEntries"/>) are all still there, and the other way round.
    /// </summary>
    [Fact]
    public void ArrivalsAndApproaches_HaveBudgetsOfTheirOwn()
    {
        var cache = new TilePrefetchCache<object>();
        for (int i = 0; i < TilePrefetchCache<object>.MaxEntries; i++)
        {
            cache.Request(MapId, i, 0, () => new object());
        }

        int arrivals = TilePrefetchCache<object>.MaxArrivalEntries + 8;
        for (int i = 0; i < arrivals; i++)
        {
            cache.Request(MapId, i, 1, () => new object(), arrival: true);
            Thread.Sleep(2); // the budget evicts the oldest by request time (ms)
        }

        Assert.Equal(TilePrefetchCache<object>.MaxEntries + TilePrefetchCache<object>.MaxArrivalEntries, cache.Pending);
        for (int i = 0; i < TilePrefetchCache<object>.MaxEntries; i++)
        {
            Assert.True(cache.Contains(MapId, i, 0), $"approach prefetch {i} was evicted by an arrival");
        }

        Assert.False(cache.Contains(MapId, 0, 1)); // the oldest arrival went
        Assert.True(cache.Contains(MapId, arrivals - 1, 1));

        // More approaches evict approaches, never arrivals.
        for (int i = 0; i < 10; i++)
        {
            cache.Request(MapId, 100 + i, 0, () => new object());
            Thread.Sleep(2);
        }

        Assert.Equal(TilePrefetchCache<object>.MaxEntries + TilePrefetchCache<object>.MaxArrivalEntries, cache.Pending);
        Assert.True(cache.Contains(MapId, arrivals - 1, 1));
        Assert.True(cache.Contains(MapId, 109, 0));
    }
}
