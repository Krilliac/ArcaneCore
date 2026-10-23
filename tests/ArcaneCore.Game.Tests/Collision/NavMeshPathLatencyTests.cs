using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>Every completed <see cref="NavMeshPathfinder.FindPath"/> adds one sample to the <c>nav.path_query</c> histogram; a query that throws adds none.</summary>
public sealed class NavMeshPathLatencyTests
{
    private const uint MapId = 0;

    [Fact]
    public void EachCompletedQuery_AddsOneSample_AndACancelledOneAddsNone()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 8, 8, Size: 5));
        var histogram = new LatencyHistogram();
        var nav = new NavMeshPathfinder(fixture.Directory) { QueryLatency = histogram };
        Assert.True(nav.LoadTile(MapId, 31, 31));

        PathResult first = nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(30, 30, 0));
        Assert.True(first.HasPath);
        Assert.Equal(1, histogram.Count);

        nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(30, 30, 0));
        Assert.Equal(2, histogram.Count);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(30, 30, 0), new PathOptions { CancellationToken = cancelled.Token }));
        Assert.Equal(2, histogram.Count);
    }

    [Fact]
    public void ByDefault_TheQueriesFeedTheSharedPathQueryHistogram()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 8, 8, Size: 5));
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));

        Assert.Same(LatencyRegistry.Shared.Get(LatencyNames.PathQuery), nav.QueryLatency);
    }
}
