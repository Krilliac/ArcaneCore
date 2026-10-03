using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>The ILineOfSight / IPathfinder seam: defaults, per-map access, eye height, options.</summary>
public sealed class CollisionSeamTests
{
    [Fact]
    public void WithoutData_TheDefaultsAreOpenAndStraight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);

        Assert.IsType<OpenLineOfSight>(collision.LineOfSight);
        Assert.IsType<StraightLinePathfinder>(collision.Pathfinder);
        Assert.False(collision.LineOfSight.Enabled);
        Assert.False(collision.Pathfinder.Enabled);
        Assert.Same(collision, WorldCollision.Of(world));

        Map map = world.GetMap(0);
        Assert.Same(map.FindUpdater<MapCollision>(), map.Collision);
        Assert.True(map.Collision.IsInLineOfSight(0, 0, 0, 100, 100, 0));
        Assert.True(map.Collision.IsOutdoors(0, 0, 0));
        Assert.Equal(TerrainTile.InvalidHeightValue, map.Collision.GetHeight(0, 0, 0));

        PathResult path = map.Collision.FindPath(new Vector3(1, 2, 3), new Vector3(10, 20, 3));
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, path.Type);
        Assert.Equal([new Vector3(1, 2, 3), new Vector3(10, 20, 3)], path.Points);
        Assert.True(path.HasPath);
    }

    [Fact]
    public void OpenDefault_ReportsNoHitHeightOrArea()
    {
        var los = OpenLineOfSight.Instance;
        Assert.False(los.TryGetObjectHit(0, Vector3.Zero, new Vector3(5, 0, 0), -1, out Vector3 hit));
        Assert.Equal(new Vector3(5, 0, 0), hit);
        Assert.Null(los.GetModelHeight(0, 0, 0, 0, 50));
        Assert.False(los.TryGetAreaInfo(0, 0, 0, 0, out _));
    }

    [Fact]
    public void InstalledServices_AreSeenByExistingMaps()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var wall = new WallAtX(5);
        WorldCollision.Of(world).Install(wall);

        Assert.False(map.Collision.IsInLineOfSight(0, 0, 0, 10, 0, 0));
        Assert.True(map.Collision.IsInLineOfSight(0, 0, 0, 4, 0, 0));
        Assert.IsType<StraightLinePathfinder>(map.Collision.Pathfinder);
    }

    [Fact]
    public void ObjectLineOfSight_UsesEyeHeight_AndRejectsOtherMaps()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var probe = new RecordingLineOfSight();
        WorldCollision.Of(world).Install(probe);
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        Player b = TestWorld.CreatePlayer(2, 10, 0, new FakeSession(2));
        Player c = TestWorld.CreatePlayer(3, 10, 0, new FakeSession(3), mapId: 1);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.AddPlayer(c);

        Assert.True(a.IsWithinLineOfSight(b));
        (Vector3 from, Vector3 to) = Assert.Single(probe.Calls);
        Assert.Equal(a.Z + MapCollision.DefaultEyeHeight, from.Z);
        Assert.Equal(b.Z + MapCollision.DefaultEyeHeight, to.Z);

        Assert.True(a.IsWithinLineOfSight(a));
        Assert.False(a.IsWithinLineOfSight(c));
    }

    [Fact]
    public void ExplicitView_OverDefaultServices_IsOpen()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var view = new MapCollision(map, WorldCollision.Of(world));
        Assert.True(view.IsInLineOfSight(0, 0, 0, 1, 1, 1));
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, view.FindPath(Vector3.Zero, Vector3.One).Type);
    }

    [Fact]
    public void Options_ResolveDirectories_FromTheTerrainDataDirectory()
    {
        var options = new CollisionOptions();
        Assert.Null(options.ResolveVMapDirectory(null));
        Assert.Null(options.ResolveMMapDirectory(""));
        Assert.Equal(Path.Combine("data", "vmaps"), options.ResolveVMapDirectory("data"));
        Assert.Equal(Path.Combine("data", "mmaps"), options.ResolveMMapDirectory("data"));

        options.VMapDirectory = "/v";
        options.MMapDirectory = "/m";
        Assert.Equal("/v", options.ResolveVMapDirectory("data"));
        Assert.Equal("/m", options.ResolveMMapDirectory("data"));
    }

    [Fact]
    public void Install_WithoutData_KeepsTheDefaults()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);
        CollisionServices.Install(collision, new CollisionOptions(), dataDirectory: null, NullLogger.Instance);
        Assert.IsType<OpenLineOfSight>(collision.LineOfSight);
        Assert.IsType<StraightLinePathfinder>(collision.Pathfinder);
    }

    /// <summary>A vertical wall at x = <paramref name="wallX"/> blocks every segment crossing it.</summary>
    internal sealed class WallAtX(float wallX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => (from.X - wallX) * (to.X - wallX) > 0;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class RecordingLineOfSight : ILineOfSight
    {
        public List<(Vector3 From, Vector3 To)> Calls { get; } = [];

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            Calls.Add((from, to));
            return true;
        }

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
