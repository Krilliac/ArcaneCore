using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The tiles a detached search wanted and did not have (<see cref="ISharedTiledPathSearch"/>): the shared overload reports them, the
/// owner reads them without blocking, and the owner own searches report nothing.
/// </summary>
public sealed class NavMeshSharedMissedTileTests
{
    private const uint MapId = 0;
    private static readonly Vector3 From = new(2, 2, 0);

    /// <summary>Three linked tiles in a row (x 0-60), written to disk; <see cref="LoadEnds"/> loads the outer two.</summary>
    private static NavMeshFixture TwoTiles()
    {
        var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { LinkedSides = [2] });
        fixture.WriteTile(MapId, 30, 31, new CellTile(0, 1, 20, 0, 4, 4, Size: 5) { LinkedSides = [2, 6] });
        fixture.WriteTile(MapId, 29, 31, new CellTile(0, 2, 40, 0, 4, 4, Size: 5) { LinkedSides = [6] });
        return fixture;
    }

    /// <summary>The outer tiles loaded, the middle one (x 20-40) not: a search from one end to the other finds no way through and misses the middle tile.</summary>
    private static NavMeshPathfinder LoadEnds(NavMeshFixture fixture)
    {
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 29, 31));
        return nav;
    }

    [Fact]
    public void ASharedSearch_RecordsTheUnloadedNeighbour_AndAnOwnerSearchRecordsNothing()
    {
        using NavMeshFixture fixture = TwoTiles();
        NavMeshPathfinder nav = LoadEnds(fixture);
        var missed = new List<(int X, int Y)>();
        Vector3 to = new(58, 2, 0);

        PathResult shared = nav.FindPathShared(MapId, From, to, new PathOptions { MaxSearchNodes = 4096 }, missed);
        Assert.NotEmpty(missed);
        Assert.Equal(missed.Count, missed.Distinct().Count());

        int recorded = missed.Count;
        PathResult owner = nav.FindPath(MapId, From, to, new PathOptions { MaxSearchNodes = 4096 });
        Assert.Equal(recorded, missed.Count);
        Assert.Equal(owner.Type, shared.Type);
        Assert.Equal(owner.Points, shared.Points);

        // The list without missed tiles is the plain shared search.
        Assert.Equal(owner.Points, nav.FindPathShared(MapId, From, to, new PathOptions { MaxSearchNodes = 4096 }).Points);
    }

    [Fact]
    public void TheOwnerReadsAMissedTileWithoutBlocking_AndThenSaysTheAnswerChanged()
    {
        using NavMeshFixture fixture = TwoTiles();
        NavMeshPathfinder nav = LoadEnds(fixture);
        var missed = new List<(int X, int Y)>();
        nav.FindPathShared(MapId, From, new Vector3(58, 2, 0), new PathOptions { MaxSearchNodes = 4096 }, missed);
        Assert.NotEmpty(missed);

        SharedMissVerdict verdict = nav.ResolveSharedMisses(MapId, missed);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (verdict == SharedMissVerdict.Pending && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            verdict = nav.ResolveSharedMisses(MapId, missed);
        }

        Assert.Equal(SharedMissVerdict.Changed, verdict);
        // The tile is in: nothing is open any more.
        Assert.NotEqual(SharedMissVerdict.Pending, nav.ResolveSharedMisses(MapId, missed));
    }

    [Fact]
    public void AnEndpointTileNotLoaded_IsReadByThePrepareStep_WithoutBlocking()
    {
        using NavMeshFixture fixture = TwoTiles();
        NavMeshPathfinder nav = LoadEnds(fixture);
        Vector3 near = From + new Vector3(8, 0, 0);
        PathResult loaded = nav.FindPathShared(MapId, From, near, new PathOptions { MaxSearchNodes = 4096 });
        Assert.True(loaded.HasPath);

        nav.UnloadTile(MapId, 31, 31); // the endpoints lie in this terrain tile
        PathType unloaded = nav.FindPathShared(MapId, From, near, new PathOptions { MaxSearchNodes = 4096 }).Type;
        Assert.NotEqual(0, (int)(unloaded & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath)));

        bool ready = nav.PrepareShared(MapId, From, near);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            ready = nav.PrepareShared(MapId, From, near);
        }

        Assert.True(ready);
        PathResult path = nav.FindPathShared(MapId, From, near, new PathOptions { MaxSearchNodes = 4096 });
        Assert.True(path.HasPath);
        Assert.Equal(0, (int)(path.Type & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath)));
    }
}
