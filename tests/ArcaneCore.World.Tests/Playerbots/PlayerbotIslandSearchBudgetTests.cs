using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA #1483: the island probe (<c>PlayerbotNavigation.IsOnMeshIsland</c>)
/// ran up to twelve synchronous 8192-node searches on the world thread (a 504 ms think). It now shares one node budget between its queries.
/// </summary>
public sealed class PlayerbotIslandSearchBudgetTests
{
    private static readonly Vector3 Start = new(100f, 200f, 30f);

    private static PathResult Near(Vector3 start) => new(PathType.Normal | PathType.Incomplete, [start, start + new Vector3(12f, 0f, 0f)]);

    private sealed class Counter(long nodesPerQuery, bool cut = false)
    {
        public int Queries;
        public long Requested;
        public long Largest;
        public long Spent;

        public (PathResult, long, bool) Query(Vector3 probe, PathOptions options)
        {
            Queries++;
            Requested += options.MaxSearchNodes;
            Largest = Math.Max(Largest, options.MaxSearchNodes);
            long used = Math.Min(nodesPerQuery, options.MaxSearchNodes);
            Spent += used;
            return (Near(Start), used, cut && Queries == 3);
        }
    }

    [Fact]
    public void ATinyIsland_IsProvedWithAllTwelveQueries_EachWithinThePerQueryCap()
    {
        var counter = new Counter(nodesPerQuery: 150);
        Assert.True(PlayerbotNavigation.IsOnMeshIsland(Start, counter.Query));
        Assert.Equal(PlayerbotNavigation.IslandDirections, counter.Queries);
        Assert.True(counter.Largest <= PlayerbotNavigation.IslandSearchNodes, $"a query was given {counter.Largest} nodes");
        Assert.True(PlayerbotNavigation.IslandSearchNodes < 8192, "the per-query cap is still the old 8192");
    }

    [Fact]
    public void AnIslandThatSpendsTheBudget_IsNotProved_AndTheSearchesStayWithinIt()
    {
        // Every query uses all it is given: the old code ran twelve of 8192 (98 304 nodes); the budget stops it at IslandTotalSearchNodes.
        var counter = new Counter(nodesPerQuery: long.MaxValue);
        Assert.False(PlayerbotNavigation.IsOnMeshIsland(Start, counter.Query));
        Assert.True(counter.Spent <= PlayerbotNavigation.IslandTotalSearchNodes, $"{counter.Spent} nodes spent");
        Assert.True(counter.Queries < PlayerbotNavigation.IslandDirections, $"{counter.Queries} queries ran");
        Assert.True(counter.Largest <= PlayerbotNavigation.IslandSearchNodes);
        Assert.True(PlayerbotNavigation.IslandTotalSearchNodes < PlayerbotNavigation.IslandDirections * 8192);
    }

    [Fact]
    public void AQueryTheNodeBoundCutShort_ProvesNoIsland_AndStopsTheProbe()
    {
        var counter = new Counter(nodesPerQuery: 150, cut: true);
        Assert.False(PlayerbotNavigation.IsOnMeshIsland(Start, counter.Query));
        Assert.Equal(3, counter.Queries);
    }

    [Fact]
    public void AProbeThatEndsFarAway_StopsAtTheFirstQuery()
    {
        int queries = 0;
        bool island = PlayerbotNavigation.IsOnMeshIsland(Start, (probe, options) =>
        {
            queries++;
            return (new PathResult(PathType.Normal, [Start, Start + new Vector3(PlayerbotNavigation.IslandYards + 20f, 0f, 0f)]), 400, false);
        });
        Assert.False(island);
        Assert.Equal(1, queries);
    }
}
