using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// A search over its node cap (<see cref="PathOptions.MaxSearchNodes"/>) degrades to the answer of a search out of corridor: the
/// partial path towards the goal when one is allowed, no path otherwise; and <see cref="PathSearchCounters"/> says it was capped, which
/// the bots use to tell a far goal from a fragment of mesh (PlayerbotNavigation).
/// </summary>
public sealed class PathSearchCapTests
{
    private const uint MapId = 0;

    private static NavMeshPathfinder Open(NavMeshFixture fixture)
    {
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20)); // 40 x 40 yards of open ground, 2-yard cells
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        return nav;
    }

    private static readonly Vector3 Start = new(1, 1, 0);
    private static readonly Vector3 Goal = new(39, 39, 0);

    [Fact]
    public void OverTheCap_APartialQuery_AnswersThePathTowardTheGoal_AndIsCountedCapped()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        PathSearchTotals before = PathSearchCounters.Current;

        PathResult path = nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchNodes = 16, AllowPartial = true });

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(PathType.Incomplete, path.Type & PathType.Incomplete);
        Assert.True(path.HasPath);
        Assert.True(Vector3.Distance(path.End, Goal) < Vector3.Distance(Start, Goal), "the partial path does not close on the goal");
        Assert.Equal(1, spent.Searches);
        Assert.Equal(1, spent.Capped);
        Assert.InRange(spent.Nodes, 2, 16);
    }

    [Fact]
    public void OverTheCap_AnExactQuery_AnswersNoPath()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        long capped = PathSearchCounters.CappedSearches;

        PathResult path = nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchNodes = 16, AllowPartial = false });

        Assert.False(path.HasPath);
        Assert.Equal(PathType.NoPath, path.Type & PathType.NoPath);
        Assert.Equal(capped + 1, PathSearchCounters.CappedSearches);
    }

    [Fact]
    public void WithinTheCap_TheSameQueryReachesTheGoal_AndIsNotCapped()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        PathSearchTotals before = PathSearchCounters.Current;

        PathResult path = nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchNodes = 4096, AllowPartial = false });

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(PathType.Normal, path.Type);
        Assert.True(Vector3.Distance(path.End, Goal) < 0.1f);
        Assert.Equal(0, spent.Capped);
        Assert.True(spent.Expansions > 0 && spent.Nodes > 16);
    }

    /// <summary>
    /// A real dead end below the cap: the goal lies on an island the mesh does not join (a wall of missing cells splits the tile in two),
    /// so the search runs out of corridor after the 200 polygons of its own side, long before its 4 096-node cap. It is not counted
    /// capped: the discriminator the bots use (PlayerbotNavigation: a fragment versus a far goal) comes from the real A*, not a flag.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADisconnectedIsland_BelowTheCap_IsADeadEnd_NotCapped(bool partial)
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20) { Walkable = (i, _) => i != 10 }); // column 10 missing
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        PathSearchTotals before = PathSearchCounters.Current;

        PathResult path = nav.FindPath(MapId, new Vector3(1, 20, 0), new Vector3(39, 20, 0),
            new PathOptions { MaxSearchNodes = 4096, AllowPartial = partial });

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(partial ? PathType.Incomplete : PathType.NoPath, path.Type & (PathType.Incomplete | PathType.NoPath));
        if (partial) Assert.True(path.End.X <= 20.01f, $"the partial path crossed the gap (column 10, X 20 to 22) to {path.End}");
        Assert.Equal(1, spent.Searches);
        Assert.Equal(0, spent.Capped);
        Assert.InRange(spent.Nodes, 100, 4095); // the whole west side was searched, the cap never reached
    }

    /// <summary>
    /// The node pool bounds the nodes of a search, not its expansions: a reopened node is expanded again. The expansions are bounded too
    /// (<see cref="NavMeshQuery.MaxExpansionsPerNode"/> per pooled node), so one creature's search cannot cost a map update tens of
    /// milliseconds whatever the mesh (live 2026-10-16: 65-425 ms of path search in one creature's update).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(4096)]
    public void ASearch_NeverExpandsMoreThanItsBound(int cap)
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20) { Walkable = (i, _) => i != 10 }); // a dead end: the whole side is searched
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        PathSearchTotals before = PathSearchCounters.Current;

        _ = nav.FindPath(MapId, new Vector3(1, 20, 0), new Vector3(39, 20, 0), new PathOptions { MaxSearchNodes = cap, AllowPartial = true });

        PathSearchTotals spent = PathSearchCounters.Current - before;
        Assert.Equal(1, spent.Searches);
        Assert.InRange(spent.Expansions, 1, (long)NavMeshQuery.MaxExpansionsPerNode * cap);
        Assert.InRange(spent.Nodes, 1, cap);
    }
}
