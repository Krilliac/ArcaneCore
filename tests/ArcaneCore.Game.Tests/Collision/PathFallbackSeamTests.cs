using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// vmangos routes a map without navmesh (or with <c>mmap.enabled = 0</c>) to
/// <c>BuildPathWithoutMMaps</c> (PathFinder.cpp:86-90, MoveMap.cpp:70-71). The fallback seam lets
/// the creature lane install its terrain-step pathfinder there without replacing the navmesh one.
/// </summary>
public sealed class PathFallbackSeamTests
{
    private static readonly Vector3 From = new(7, 5, 0);
    private static readonly Vector3 To = new(17, 5, 0);

    private static CellTile Walled() => new(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, j) => !(i == 2 && j < 3) };

    private sealed class MarkerPathfinder(PathType marker) : IPathfinder
    {
        public PathOptions? LastOptions { get; private set; }

        public int Calls { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            LastOptions = options;
            return PathResult.StraightLine(start, end, marker);
        }
    }

    [Fact]
    public void NoFallbackRegistered_KeepsTheDocumentedStraightLine()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, world.GetMap(0).Collision.FindPath(From, To).Type);
    }

    [Fact]
    public void FallbackAnswers_WhenNoNavigationDataIsConfigured()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var fallback = new MarkerPathfinder(PathType.Shortcut);
        WorldCollision.Of(world).InstallFallback(fallback);

        Assert.Equal(PathType.Shortcut, world.GetMap(0).Collision.FindPath(From, To).Type);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public void FallbackAnswers_OnlyForMapsWithoutANavmesh()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(0);
        fixture.WriteTile(0, 31, 31, Walled());
        using WorldRuntime world = TestWorld.CreateRuntime();
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, 31, 31));
        WorldCollision.Of(world).Install(pathfinder: nav);
        WorldCollision.Of(world).InstallFallback(new MarkerPathfinder(PathType.Shortcut));

        Assert.True(nav.HasNavigationData(0));
        Assert.False(nav.HasNavigationData(1));
        Assert.Equal(PathType.Normal, world.GetMap(0).Collision.FindPath(From, To).Type); // navmesh map: navmesh answers
        Assert.Equal(PathType.Shortcut, world.GetMap(1).Collision.FindPath(From, To).Type); // no .mmap: fallback
    }

    [Fact]
    public void ReplacingThePrimary_KeepsTheFallback()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);
        var fallback = new MarkerPathfinder(PathType.Shortcut);
        collision.InstallFallback(fallback);
        collision.Install(pathfinder: StraightLinePathfinder.Instance);

        Assert.Same(fallback, collision.Fallback);
        Assert.Equal(PathType.Shortcut, world.GetMap(0).Collision.FindPath(From, To).Type);
    }

    [Fact]
    public void AnEnabledPrimaryWithoutMapAwareness_IsAlwaysUsed()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var primary = new MarkerPathfinder(PathType.Normal);
        WorldCollision.Of(world).Install(pathfinder: primary);
        WorldCollision.Of(world).InstallFallback(new MarkerPathfinder(PathType.Shortcut));

        Assert.Equal(PathType.Normal, world.GetMap(0).Collision.FindPath(From, To).Type);
    }

    [Fact]
    public void TheMoverTravelsInTheOptions_ToWhoeverAnswers()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var fallback = new MarkerPathfinder(PathType.Shortcut);
        WorldCollision.Of(world).InstallFallback(fallback);
        var mover = new PathMover(CanWalk: false, CanSwim: true, CanFly: false, IsPlayer: false);

        world.GetMap(0).Collision.FindPath(From, To, new PathOptions { Mover = mover });

        Assert.Equal(mover, fallback.LastOptions!.Mover);
    }

    [Fact]
    public void ClearingTheFallback_RestoresTheStraightLine()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldCollision collision = WorldCollision.Of(world);
        collision.InstallFallback(new MarkerPathfinder(PathType.Shortcut));
        collision.InstallFallback(null);

        Assert.Same(StraightLinePathfinder.Instance, collision.Fallback);
        Assert.Equal(PathType.Normal | PathType.NotUsingPath, world.GetMap(0).Collision.FindPath(From, To).Type);
    }

    [Fact]
    public void AnEndpointOnAnUnloadedTile_GoesStraight_NotNoPath()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(0);
        fixture.WriteTile(0, 31, 31, Walled());
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, 31, 31));

        // World Y 700 lies in Detour tile column 1 (tile width 533.3333), which is not loaded.
        PathResult path = nav.FindPath(0, new Vector3(7, 5, 0), new Vector3(7, 700, 0));

        Assert.Equal(PathType.Normal | PathType.NotUsingPath, path.Type);
        Assert.Equal(2, path.Points.Count);
    }
}
