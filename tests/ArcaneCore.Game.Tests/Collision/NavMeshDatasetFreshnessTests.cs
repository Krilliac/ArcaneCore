using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

public sealed class NavMeshDatasetFreshnessTests
{
    [Fact]
    public void IndependentContextKeepsDatasetIdentityButOwnsMeshesAndTiles()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(0);
        fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 4, 4));

        var owner = new NavMeshPathfinder(fixture.Directory);
        NavMeshPathfinder detached = owner.CreateIndependent();

        Assert.NotSame(owner, detached);
        Assert.Equal(owner.Dataset, detached.Dataset);
        Assert.NotSame(owner.GetNavMesh(0), detached.GetNavMesh(0));
        Assert.True(owner.LoadTile(0, 31, 31));
        Assert.True(detached.LoadTile(0, 31, 31));
        Assert.NotSame(owner.GetNavMesh(0), detached.GetNavMesh(0));

        owner.UnloadTile(0, 31, 31);
        Assert.True(detached.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));
    }
}
