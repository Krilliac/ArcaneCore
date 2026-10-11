using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
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
}
