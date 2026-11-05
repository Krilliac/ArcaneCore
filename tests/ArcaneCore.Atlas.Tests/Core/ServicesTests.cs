using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class ServicesTests
{
    private sealed class CountingSource : ILayerSource
    {
        private int _builds;

        public int Builds => Volatile.Read(ref _builds);

        public TimeSpan Delay { get; init; }

        public ManualResetEventSlim? Gate { get; init; }

        public Func<TileKey, Exception?> Fail { get; init; } = _ => null;

        public IReadOnlyList<string> Layers { get; } = [LayerIds.Terrain];

        public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId) => [];

        public TileStatus PeekStatus(string layerId, TileKey tile) => new(layerId, tile, TileState.Unknown);

        public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _builds);
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }

            if (Gate is not null && !Gate.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the build gate never opened");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Fail(tile) is { } failure)
            {
                throw failure;
            }

            var builder = new GeometryBuilder(layerId, tile, WorldGrid.Origin(tile));
            uint a = builder.AddVertex(0, 0, 0);
            uint b = builder.AddVertex(1, 0, 0);
            uint c = builder.AddVertex(0, 1, 0);
            builder.AddTriangle(a, b, c);
            return LayerBuild.Of(layerId, tile, [builder.Build()]);
        }
    }

    private static (LayerCache Cache, CountingSource Source) Cache(long maxBytes = LayerCache.DefaultMaxBytes, CountingSource? source = null)
    {
        source ??= new CountingSource();
        var registry = new LayerRegistry();
        registry.Add(source);
        return (new LayerCache(registry, maxBytes), source);
    }

    [Fact]
    public void Cache_BuildsEachTileOnce_AndServesWireBytes()
    {
        (LayerCache cache, CountingSource source) = Cache();
        var tile = new TileKey(0, 49, 31);

        LayerBuild first = cache.Get(LayerIds.Terrain, tile, CancellationToken.None);
        IReadOnlyList<byte[]> wire = cache.GetWire(LayerIds.Terrain, tile, CancellationToken.None);

        Assert.Equal(1, source.Builds);
        Assert.Equal(TileState.Ok, first.Status.State);
        Assert.Equal(TileState.Ok, cache.Peek(LayerIds.Terrain, tile).State);
        Assert.Equal(TileState.Unknown, cache.Peek(LayerIds.Terrain, new TileKey(0, 1, 1)).State);
        Assert.Equal(1, GeometryWire.Read(Assert.Single(wire)).TriangleCount);
        Assert.True(cache.Bytes > 0);
    }

    [Fact]
    public async Task Cache_SharesOneBuildBetweenConcurrentRequests()
    {
        using var gate = new ManualResetEventSlim();
        (LayerCache cache, CountingSource source) = Cache(source: new CountingSource { Gate = gate });
        var tile = new TileKey(0, 32, 32);

        // The first request holds the build open on the gate; the other seven call Get only after that, so each one meets a build in flight.
        Task first = Task.Run(() => cache.Get(LayerIds.Terrain, tile, CancellationToken.None));
        Assert.True(SpinWait.SpinUntil(() => source.Builds == 1, TimeSpan.FromSeconds(10)), "no build started");
        Task[] rest = Enumerable.Range(0, 7).Select(_ => Task.Run(() => cache.Get(LayerIds.Terrain, tile, CancellationToken.None))).ToArray();
        Task waiters = Task.WhenAll(rest);
        await Task.WhenAny(waiters, Task.Delay(100));
        Assert.False(waiters.IsCompleted, "a request finished while the build was held");
        gate.Set();
        await Task.WhenAll([first, .. rest]).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, source.Builds);
    }

    [Fact]
    public void Cache_DoesNotKeepFailures_AndInvalidateForcesARebuild()
    {
        bool fail = true;
        (LayerCache cache, CountingSource source) = Cache(source: new CountingSource { Fail = _ => fail ? new InvalidDataException("corrupt") : null });
        var tile = new TileKey(0, 10, 10);

        Assert.Throws<InvalidDataException>(() => cache.Get(LayerIds.Terrain, tile, CancellationToken.None));
        fail = false;
        cache.Get(LayerIds.Terrain, tile, CancellationToken.None);
        Assert.Equal(1, cache.Invalidate(LayerIds.Terrain));
        cache.Get(LayerIds.Terrain, tile, CancellationToken.None);

        Assert.Equal(3, source.Builds);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_WhenOverBudget()
    {
        (LayerCache cache, CountingSource source) = Cache(maxBytes: 1);
        var a = new TileKey(0, 1, 1);
        var b = new TileKey(0, 2, 2);

        cache.Get(LayerIds.Terrain, a, CancellationToken.None);
        cache.Get(LayerIds.Terrain, b, CancellationToken.None);
        cache.Get(LayerIds.Terrain, b, CancellationToken.None);
        cache.Get(LayerIds.Terrain, a, CancellationToken.None);

        // The budget holds only the newest tile: b stays cached on its second request, a is rebuilt.
        Assert.Equal(3, source.Builds);
    }

    [Fact]
    public void Cache_RefusesUnknownLayersAndTiles()
    {
        (LayerCache cache, _) = Cache();

        Assert.Throws<KeyNotFoundException>(() => cache.Get(LayerIds.Liquid, new TileKey(0, 1, 1), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => cache.Get(LayerIds.Terrain, new TileKey(0, 64, 1), CancellationToken.None));
    }

    [Fact]
    public void Cache_CancelledBuildIsNotCached()
    {
        (LayerCache cache, CountingSource source) = Cache();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => cache.Get(LayerIds.Terrain, new TileKey(0, 5, 5), cancelled.Token));
        cache.Get(LayerIds.Terrain, new TileKey(0, 5, 5), CancellationToken.None);

        Assert.Equal(1, source.Builds);
    }

    [Fact]
    public void EventHub_DeliversUntilDisposed()
    {
        var hub = new EventHub();
        var seen = new List<string>();
        IDisposable subscription = hub.Subscribe((topic, payload) => seen.Add($"{topic}:{payload?.ToJsonString()}"));

        hub.Publish(EventHub.Topics.View, new JsonObject { ["version"] = 1 });
        subscription.Dispose();
        hub.Publish(EventHub.Topics.View, null);

        Assert.Equal(new[] { """view:{"version":1}""" }, seen);
    }
}
