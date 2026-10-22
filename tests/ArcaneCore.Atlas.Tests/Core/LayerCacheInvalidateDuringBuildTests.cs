using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class LayerCacheInvalidateDuringBuildTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(10);

    /// <summary>Blocks inside Build until released, so Invalidate can run while the Lazy is still building.</summary>
    private sealed class GatedSource : ILayerSource
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _builds;

        public bool Gate { get; set; } = true;

        public int Builds => Volatile.Read(ref _builds);

        public IReadOnlyList<string> Layers { get; } = [LayerIds.Terrain];

        public void WaitEntered() => Assert.True(_entered.Wait(s_wait), "Build was never entered.");

        public void Release() => _release.Set();

        public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId) => [];

        public TileStatus PeekStatus(string layerId, TileKey tile) => new(layerId, tile, TileState.Unknown);

        public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _builds);
            if (Gate)
            {
                _entered.Set();
                Assert.True(_release.Wait(s_wait), "Build was never released.");
            }

            var builder = new GeometryBuilder(layerId, tile, WorldGrid.Origin(tile));
            uint a = builder.AddVertex(0, 0, 0);
            uint b = builder.AddVertex(1, 0, 0);
            uint c = builder.AddVertex(0, 1, 0);
            builder.AddTriangle(a, b, c);
            return LayerBuild.Of(layerId, tile, [builder.Build()]);
        }
    }

    private static LayerCache NewCache(GatedSource source, long maxBytes = LayerCache.DefaultMaxBytes)
    {
        var registry = new LayerRegistry();
        registry.Add(source);
        return new LayerCache(registry, maxBytes);
    }

    [Fact]
    public async Task Invalidate_DuringBuild_DoesNotLeaveBytesCountedForAnUncachedEntry()
    {
        var source = new GatedSource();
        LayerCache cache = NewCache(source);
        var tile = new TileKey(0, 20, 20);

        Task<LayerBuild> build = Task.Run(() => cache.Get(LayerIds.Terrain, tile, CancellationToken.None));
        source.WaitEntered();
        int removed = cache.Invalidate(LayerIds.Terrain);
        source.Release();
        await build.WaitAsync(s_wait);

        Assert.Equal(1, removed);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public async Task Invalidate_DuringBuild_ThenRebuild_CountsOnlyTheLiveEntry()
    {
        var source = new GatedSource();
        LayerCache cache = NewCache(source);
        var tile = new TileKey(0, 21, 21);

        Task<LayerBuild> build = Task.Run(() => cache.Get(LayerIds.Terrain, tile, CancellationToken.None));
        source.WaitEntered();
        cache.Invalidate();
        source.Release();
        await build.WaitAsync(s_wait);

        source.Gate = false;
        cache.Get(LayerIds.Terrain, tile, CancellationToken.None);
        long oneEntry = cache.Bytes;
        Assert.Equal(2, source.Builds);

        // Dropping the single live entry must bring the count back to zero; a phantom remainder means drift.
        Assert.Equal(1, cache.Invalidate());
        Assert.Equal(0, cache.Bytes);
        Assert.True(oneEntry > 0);
    }

    [Fact]
    public async Task Invalidate_DuringBuild_DoesNotEvictLiveTilesForPhantomBytes()
    {
        var source = new GatedSource();
        var tileA = new TileKey(0, 30, 30);
        var tileB = new TileKey(0, 31, 31);

        // Measure one entry's size with an unconstrained cache.
        source.Gate = false;
        LayerCache probe = NewCache(source);
        probe.Get(LayerIds.Terrain, tileA, CancellationToken.None);
        long one = probe.Bytes;

        // Budget fits exactly two entries. A leaked phantom entry would push the count over and evict a live tile.
        source.Gate = true;
        LayerCache cache = NewCache(source, maxBytes: one * 2);
        Task<LayerBuild> stale = Task.Run(() => cache.Get(LayerIds.Terrain, tileA, CancellationToken.None));
        source.WaitEntered();
        cache.Invalidate();
        source.Release();
        await stale.WaitAsync(s_wait);

        source.Gate = false;
        int before = source.Builds;
        cache.Get(LayerIds.Terrain, tileA, CancellationToken.None);
        cache.Get(LayerIds.Terrain, tileB, CancellationToken.None);
        int afterFill = source.Builds;
        cache.Get(LayerIds.Terrain, tileA, CancellationToken.None);
        cache.Get(LayerIds.Terrain, tileB, CancellationToken.None);

        Assert.Equal(before + 2, afterFill);
        Assert.Equal(afterFill, source.Builds);
    }
}
