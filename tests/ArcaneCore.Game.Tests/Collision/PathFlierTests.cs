using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// A creature that can fly goes straight through the air unless a collision model is in the way
/// (vmangos PathFinder.cpp:172-186); a hole in the mesh is a flying shortcut for it (:190-198), and
/// a destination forced past a model is flagged (:451-472).
/// </summary>
public sealed class PathFlierTests
{
    private static readonly Vector3 From = new(7, 5, 0);
    private static readonly Vector3 To = new(17, 5, 0);

    private static CellTile Walled() => new(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, j) => !(i == 2 && j < 3) };

    private sealed class Los(bool clear) : ILineOfSight
    {
        public bool? LastIgnoreM2 { get; private set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            LastIgnoreM2 = ignoreM2;
            return clear;
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

    private static (WorldRuntime World, NavMeshFixture Fixture, WorldCollision Collision) Setup(ILineOfSight? los, bool installLosFirst)
    {
        var fixture = new NavMeshFixture();
        fixture.WriteParams(0);
        fixture.WriteTile(0, 31, 31, Walled());
        WorldRuntime world = TestWorld.CreateRuntime();
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, 31, 31));
        WorldCollision collision = WorldCollision.Of(world);
        if (installLosFirst)
        {
            collision.Install(lineOfSight: los);
            collision.Install(pathfinder: nav);
        }
        else
        {
            collision.Install(pathfinder: nav);
            collision.Install(lineOfSight: los);
        }

        return (world, fixture, collision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFlierWithAClearSegment_GetsTheFlyShortcut_InEitherInstallOrder(bool losFirst)
    {
        var los = new Los(clear: true);
        (WorldRuntime world, NavMeshFixture fixture, _) = Setup(los, losFirst);
        using (world)
        using (fixture)
        {
            var mover = new PathMover(CanWalk: false, CanSwim: false, CanFly: true, IsPlayer: false);

            PathResult path = world.GetMap(0).Collision.FindPath(From, To, new PathOptions { Mover = mover });

            Assert.Equal(PathType.Normal | PathType.NotUsingPath | PathType.FlyPath, path.Type);
            Assert.True(path.HasPath);
            Assert.Equal([From, To], path.Points);
            Assert.False(los.LastIgnoreM2); // FindCollisionModel does not skip doodads
        }
    }

    [Fact]
    public void AFlierBlockedByAModel_FollowsTheMesh_NotTheShortcut()
    {
        (WorldRuntime world, NavMeshFixture fixture, _) = Setup(new Los(clear: false), installLosFirst: true);
        using (world)
        using (fixture)
        {
            var mover = new PathMover(CanWalk: true, CanSwim: false, CanFly: true, IsPlayer: false);

            PathResult path = world.GetMap(0).Collision.FindPath(From, To, new PathOptions { Mover = mover });

            Assert.True(path.HasPath);
            Assert.NotEqual(PathType.NoPath, path.Type);
            Assert.True(path.Points.Count > 2); // routed around the wall, not the straight shortcut
            Assert.Equal(To.X, path.Points[^1].X, 3);
            Assert.Equal(To.Y, path.Points[^1].Y, 3);
        }
    }

    [Fact]
    public void AGroundMoverIsNotAffectedByTheLineTest()
    {
        (WorldRuntime world, NavMeshFixture fixture, _) = Setup(new Los(clear: true), installLosFirst: true);
        using (world)
        using (fixture)
        {
            PathResult path = world.GetMap(0).Collision.FindPath(From, To, new PathOptions { Mover = PathMover.Player });

            Assert.Equal(PathType.Normal, path.Type);
            Assert.True(path.Points.Count > 2);
        }
    }

    [Fact]
    public void AFlierOffTheMesh_GetsTheFlyShortcut_NotNoPath()
    {
        (WorldRuntime world, NavMeshFixture fixture, _) = Setup(new Los(clear: false), installLosFirst: true);
        using (world)
        using (fixture)
        {
            var mover = new PathMover(CanWalk: false, CanSwim: false, CanFly: true, IsPlayer: false);

            PathResult path = world.GetMap(0).Collision.FindPath(From, To, new PathOptions { Mover = mover });

            Assert.Equal(PathType.Normal | PathType.NotUsingPath | PathType.FlyPath, path.Type);
            Assert.Equal(2, path.Points.Count);
        }
    }
}
