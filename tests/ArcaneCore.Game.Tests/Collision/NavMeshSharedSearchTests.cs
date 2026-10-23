using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="NavMeshPathfinder.FindPathShared"/> (multithreading plan W5): searches from other threads over the owner's loaded tiles
/// answer what the owner's own search answers, fill the lazy per-tile caches safely when they race, and never see a tile change half
/// done (the owner's change asks them to stop and waits).
/// </summary>
public sealed class NavMeshSharedSearchTests
{
    private const uint MapId = 0;
    private static readonly Vector3 From = new(2, 2, 0);
    private static readonly Vector3 To = new(58, 2, 0);

    /// <summary>Three linked tiles in a row (x 0-60), the layout of the cancellation tests.</summary>
    private static NavMeshFixture ThreeTiles()
    {
        var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { LinkedSides = [2] });
        fixture.WriteTile(MapId, 30, 31, new CellTile(0, 1, 20, 0, 4, 4, Size: 5) { LinkedSides = [2, 6] });
        fixture.WriteTile(MapId, 29, 31, new CellTile(0, 2, 40, 0, 4, 4, Size: 5) { LinkedSides = [6] });
        return fixture;
    }

    private static NavMeshPathfinder LoadAll(NavMeshFixture fixture)
    {
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 30, 31));
        Assert.True(nav.LoadTile(MapId, 29, 31));
        return nav;
    }

    private static string Describe(PathResult path) => $"{path.Type}: {string.Join(" ", path.Points.Select(p => $"({p.X:F2},{p.Y:F2},{p.Z:F2})"))}";

    [Fact]
    public void SharedSearches_FromManyThreads_OnColdCaches_AnswerTheOwnersPath()
    {
        using NavMeshFixture fixture = ThreeTiles();
        string expected = Describe(LoadAll(fixture).FindPath(MapId, From, To));
        Assert.Contains("Normal", expected, StringComparison.Ordinal);

        for (int round = 0; round < 5; round++)
        {
            NavMeshPathfinder nav = LoadAll(fixture); // fresh tiles: the border-edge and cross-portal caches are built by the racing searches
            Assert.True(nav.CanSearchShared(MapId));
            var answers = new ConcurrentBag<string>();
            using var start = new Barrier(8);
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                start.SignalAndWait();
                for (int i = 0; i < 50; i++)
                {
                    answers.Add(Describe(nav.FindPathShared(MapId, From, To, PathOptions.Default)));
                }
            });

            Assert.Equal(400, answers.Count);
            Assert.All(answers, a => Assert.Equal(expected, a));
        }
    }

    [Fact]
    public async Task OwnerTileChanges_DuringSharedSearches_AnswerOneOfTheTwoWorlds_OrStopTheSearch()
    {
        using NavMeshFixture fixture = ThreeTiles();
        NavMeshPathfinder nav = LoadAll(fixture);
        string whole = Describe(nav.FindPath(MapId, From, To));
        nav.UnloadTile(MapId, 29, 31);
        string withoutEnd = Describe(nav.FindPath(MapId, From, To));
        Assert.True(nav.LoadTile(MapId, 29, 31));
        Assert.NotEqual(whole, withoutEnd);

        var unexpected = new ConcurrentBag<string>();
        int answered = 0;
        int stopped = 0;
        using var done = new CancellationTokenSource();
        Task[] searchers = Enumerable.Range(0, 6).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!done.IsCancellationRequested)
            {
                try
                {
                    string answer = Describe(nav.FindPathShared(MapId, From, To, PathOptions.Default));
                    Interlocked.Increment(ref answered);
                    if (answer != whole && answer != withoutEnd)
                    {
                        unexpected.Add(answer);
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref stopped);
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        for (int i = 0; i < 300; i++)
        {
            nav.UnloadTile(MapId, 29, 31);
            Thread.SpinWait(200);
            Assert.True(nav.LoadTile(MapId, 29, 31));
            Thread.SpinWait(200);
        }

        done.Cancel();
        await Task.WhenAll(searchers);
        Assert.Empty(unexpected);
        Assert.True(answered > 0);
        Assert.Equal(whole, Describe(nav.FindPath(MapId, From, To))); // the owner's own search is unharmed
        _ = stopped;
    }

    [Fact]
    public void SharedSearch_RefusesTileLoadingAndFliers()
    {
        using NavMeshFixture fixture = ThreeTiles();
        NavMeshPathfinder nav = LoadAll(fixture);

        Assert.Throws<ArgumentException>(() => nav.FindPathShared(MapId, From, To, new PathOptions { LoadTiles = true }));
        Assert.Throws<ArgumentException>(() => nav.FindPathShared(MapId, From, To, new PathOptions { Mover = PathMover.FromInhabit(4) }));
    }

    [Fact]
    public void SharedSearch_OnAMapWithoutMesh_GoesStraight()
    {
        using NavMeshFixture fixture = ThreeTiles();
        NavMeshPathfinder nav = LoadAll(fixture);

        Assert.False(nav.CanSearchShared(1));
        PathResult path = nav.FindPathShared(1, From, To, PathOptions.Default);
        Assert.True((path.Type & PathType.NotUsingPath) != 0);
    }

    [Fact]
    public void NavJobSystem_RunsJobsOffTheCallingThread_UrgentFirst()
    {
        using NavMeshFixture fixture = ThreeTiles();
        NavMeshPathfinder nav = LoadAll(fixture);
        string expected = Describe(nav.FindPath(MapId, From, To));
        using var jobs = new NavJobSystem(threads: 3);
        var submitted = new List<NavJob>();
        for (int i = 0; i < 64; i++)
        {
            var job = new NavJob(nav, MapId, From, To, PathOptions.Default, urgent: i % 4 == 0);
            Assert.True(jobs.TrySubmit(job));
            submitted.Add(job);
        }

        SpinWait.SpinUntil(() => submitted.All(j => j.IsDone), TimeSpan.FromSeconds(20));
        Assert.All(submitted, j =>
        {
            Assert.Equal(NavJobStatus.Completed, j.Status);
            Assert.Equal(expected, Describe(j.Result!));
            Assert.True(j.Search.Searches >= 1);
        });
        Assert.Equal(64, jobs.Totals.Completed);
    }

    [Fact]
    public void NavJobSystem_RefusesPastItsCapacity_AndSkipsAbandonedJobs()
    {
        using NavMeshFixture fixture = ThreeTiles();
        NavMeshPathfinder nav = LoadAll(fixture);
        var gate = new GatedSearch(nav);
        using var jobs = new NavJobSystem(threads: 1, capacity: 2);
        var first = new NavJob(gate, MapId, From, To, PathOptions.Default);
        Assert.True(jobs.TrySubmit(first));
        SpinWait.SpinUntil(() => gate.Entered, TimeSpan.FromSeconds(10)); // the worker holds the first job
        var second = new NavJob(gate, MapId, From, To, PathOptions.Default);
        var third = new NavJob(gate, MapId, From, To, PathOptions.Default);
        var fourth = new NavJob(gate, MapId, From, To, PathOptions.Default);
        Assert.True(jobs.TrySubmit(second));
        Assert.True(jobs.TrySubmit(third));
        Assert.False(jobs.TrySubmit(fourth));
        second.Abandon();
        gate.Open.Set();

        SpinWait.SpinUntil(() => first.IsDone && second.IsDone && third.IsDone, TimeSpan.FromSeconds(10));
        Assert.Equal(NavJobStatus.Completed, first.Status);
        Assert.Equal(NavJobStatus.Canceled, second.Status);
        Assert.Equal(NavJobStatus.Completed, third.Status);
        Assert.Equal(1, jobs.Totals.Rejected);
        Assert.Equal(1, jobs.Totals.Skipped);
    }

    private sealed class GatedSearch(NavMeshPathfinder inner) : ISharedPathSearch
    {
        public ManualResetEventSlim Open { get; } = new(false);

        public volatile bool Entered;

        public bool CanSearchShared(uint mapId) => inner.CanSearchShared(mapId);

        public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
        {
            Entered = true;
            Open.Wait(TimeSpan.FromSeconds(10));
            return inner.FindPathShared(mapId, start, end, options);
        }
    }
}
