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
    public async Task APrefetchedMap_IsReadOffTheCallingThread_AndTheFirstQueryReadsNothing()
    {
        _fixture.WriteParams(0);
        int callerThread = Environment.CurrentManagedThreadId;
        var readOn = new List<int>();
        var nav = new NavMeshPathfinder(_fixture.Directory)
        {
            FileReader = path =>
            {
                lock (readOn) readOn.Add(Environment.CurrentManagedThreadId);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            },
        };

        ((ICollisionMapPrefetch)nav).PrefetchMap(0);
        ((ICollisionMapPrefetch)nav).PrefetchMap(0); // a second instance of the map: no second read
        await nav.MapPrefetch(0)!.WaitAsync(TimeSpan.FromSeconds(30));
        lock (readOn)
        {
            Assert.Single(readOn);
            Assert.NotEqual(callerThread, readOn[0]);
        }

        Assert.True(nav.HasNavigationData(0)); // the first query
        lock (readOn) Assert.Single(readOn); // it took the prefetched bytes
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
}
