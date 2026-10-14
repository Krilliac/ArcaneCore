using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>Cooperative cancellation of private navigation searches leaves reusable query state intact.</summary>
public sealed class NavMeshCancellationTests
{
    private const uint MapId = 0;

    [Fact]
    public void PreCancelledSearch_DoesNotEnterNavigation()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 8, 8, Size: 5));
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        PathOptions options = new() { CancellationToken = cancelled.Token };

        Assert.Throws<OperationCanceledException>(() => nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(30, 30, 0), options));
    }

    [Fact]
    public void CancellationDuringSearch_ThrowsAndLaterSearchStillWorksOnSameThread()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { LinkedSides = [2] });
        fixture.WriteTile(MapId, 30, 31, new CellTile(0, 1, 20, 0, 4, 4, Size: 5) { LinkedSides = [2, 6] });
        fixture.WriteTile(MapId, 29, 31, new CellTile(0, 2, 40, 0, 4, 4, Size: 5) { LinkedSides = [6] });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 29, 31));

        using var cancelled = new CancellationTokenSource();
        NavMesh mesh = nav.GetNavMesh(MapId)!;
        mesh.TileLoader = (_, _) =>
        {
            cancelled.Cancel();
            return null;
        };
        PathOptions options = new() { CancellationToken = cancelled.Token };

        Assert.Throws<OperationCanceledException>(() => nav.FindPath(MapId, new Vector3(17, 17, 0), new Vector3(42, 17, 0), options));

        mesh.TileLoader = null;
        Assert.True(nav.LoadTile(MapId, 30, 31));
        PathResult later = nav.FindPath(MapId, new Vector3(2, 2, 0), new Vector3(58, 2, 0));
        Assert.True(later.HasPath);
    }
}
