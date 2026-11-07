using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The per-map <c>.mmap</c> parameter file is read on the thread pool when the map is created (<see cref="ICollisionMapPrefetch"/>), so the
/// first query on the map finds the bytes and does not read the file on its own thread.
/// </summary>
public sealed class NavMeshParamsPrefetchTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task APrefetchedMap_ReturnsBeforeTheRead_AndTheFirstQueryReadsNothing()
    {
        _fixture.WriteParams(0);
        // The read is held on a gate: PrefetchMap returning while the reader is still blocked proves the read is not done by the caller,
        // without comparing thread ids (a pool thread that called PrefetchMap could legitimately run the queued read itself).
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        int reads = 0;
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                Interlocked.Increment(ref reads);
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };

        try
        {
            ((ICollisionMapPrefetch)nav).PrefetchMap(0);
            ((ICollisionMapPrefetch)nav).PrefetchMap(0); // a second instance of the map: no second read
            Task prefetch = nav.MapPrefetch(0)!;
            Assert.False(prefetch.IsCompleted); // PrefetchMap returned while the read is still held
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            Assert.False(prefetch.IsCompleted);
        }
        finally
        {
            gate.Set();
        }

        await nav.MapPrefetch(0)!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.True(nav.HasNavigationData(0)); // the first query
        Assert.Equal(1, Volatile.Read(ref reads)); // it took the prefetched bytes
    }

    [Fact]
    public void AMapNeverPrefetched_IsStillReadByTheFirstQuery()
    {
        _fixture.WriteParams(0);
        int reads = 0;
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                Interlocked.Increment(ref reads);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };

        Assert.True(nav.HasNavigationData(0));
        Assert.Equal(1, reads);
        Assert.False(nav.HasNavigationData(7)); // no .mmap: no navmesh
    }

    [Fact]
    public async Task APrefetchedMapWithoutAFile_HasNoNavigationData_AndIsNotReadAgain()
    {
        int reads = 0;
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                Interlocked.Increment(ref reads);
                return null;
            },
        };

        nav.PrefetchMap(9);
        await nav.MapPrefetch(9)!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(nav.HasNavigationData(9));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ACreatedMapView_PrefetchesItsMapsParameters_ASecondViewDoesNotReadAgain_AndADetachedViewReadsNothing()
    {
        _fixture.WriteParams(0);
        int reads = 0;
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                Interlocked.Increment(ref reads);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);
        collision.Install(null, nav);
        Assert.Null(nav.MapPrefetch(0));

        // Creating the map attaches its default MapCollision view (a followed view over the world's services), which starts the read:
        // removing the PrefetchNavigationParameters call from the constructor leaves MapPrefetch(0) null and fails here.
        Map map = world.GetMap(0);
        Task prefetch = nav.MapPrefetch(0) ?? throw new InvalidOperationException("creating the map did not prefetch its parameters");
        await prefetch.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref reads));

        // Another followed view of the same map, then the first query: both take the bytes already read (the read had ended before them).
        _ = new MapCollision(map, collision);
        Assert.True(nav.HasNavigationData(0));
        Assert.Equal(1, Volatile.Read(ref reads));

        // A detached view subscribes to nothing and prefetches nothing.
        var other = new NavMeshPathfinder(_fixture.Directory) { FileReader = _ => throw new InvalidOperationException("a detached view read a file") };
        collision.Install(null, other);
        _ = new MapCollision(map, collision, followGrids: false);
        Assert.Null(other.MapPrefetch(0));
    }
}
