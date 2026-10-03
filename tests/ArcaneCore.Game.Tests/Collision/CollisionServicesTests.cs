using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>Which collision services the configuration and the data on disk install.</summary>
public sealed class CollisionServicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanecore-collision-" + Guid.NewGuid().ToString("N"));

    public CollisionServicesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void VMapDirectory_InstallsTheReader_UnlessDisabled()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "vmaps"));
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);

        CollisionServices.Install(collision, new CollisionOptions { EnableLineOfSight = false, EnableHeight = false }, _dir, NullLogger.Instance);
        Assert.IsType<OpenLineOfSight>(collision.LineOfSight);

        CollisionServices.Install(collision, new CollisionOptions { EnableHeight = false }, _dir, NullLogger.Instance);
        var vmaps = Assert.IsType<VMapManager>(collision.LineOfSight);
        Assert.True(vmaps.LineOfSightEnabled);
        Assert.False(vmaps.HeightEnabled);
    }

    [Fact]
    public void MMapDirectory_InstallsTheNavMesh_UnlessDisabled()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "mmaps"));
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);

        CollisionServices.Install(collision, new CollisionOptions { EnablePathfinding = false }, _dir, NullLogger.Instance);
        Assert.IsType<StraightLinePathfinder>(collision.Pathfinder);

        CollisionServices.Install(collision, new CollisionOptions(), _dir, NullLogger.Instance);
        Assert.IsType<NavMeshPathfinder>(collision.Pathfinder);
        Assert.IsType<OpenLineOfSight>(collision.LineOfSight);
    }

    [Fact]
    public void MissingDirectories_KeepTheDefaults()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);
        CollisionServices.Install(collision, new CollisionOptions { VMapDirectory = Path.Combine(_dir, "none"), MMapDirectory = Path.Combine(_dir, "none") }, _dir, NullLogger.Instance);
        Assert.IsType<OpenLineOfSight>(collision.LineOfSight);
        Assert.IsType<StraightLinePathfinder>(collision.Pathfinder);
    }
}
