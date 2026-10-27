using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// vmangos <c>PathInfo::BuildPolyPath</c> locates the start and the end alike (<c>getPolyByLocation</c>, PathFinder.cpp:188-189):
/// <c>FindWalkPoly</c> looks 5 yd around and 10 up and down (PathFinder.cpp:128-142, PathFinder.h:89) and refuses a polygon more than 3 yd
/// above the point; then <c>farFromPoly</c> (PathFinder.cpp:209-235) makes the path incomplete, or a flier's straight line, when either
/// endpoint is more than 7 yd from its polygon. A query with <see cref="PathOptions.EndTolerance"/> follows that for both endpoints; one
/// without keeps the search boxes for both.
/// </summary>
public sealed class PathStartWalkPolyTests
{
    private const uint MapId = 0;

    /// <summary>10×10 cells of 2 yd over world [0, 20)², flat at z 0.</summary>
    private static CellTile Flat() => new(0, 0, 0, 0, 10, 10);

    /// <summary>The destination: on the mesh, 6 yd from the start columns at x 10.</summary>
    private static readonly Vector3 OnMesh = new(4, 10, 0);

    private static readonly PathOptions Vmangos = new() { Mover = PathMover.FromInhabit(1), EndTolerance = PathOptions.VmangosEndTolerance };

    private static NavMeshPathfinder Load(NavMeshFixture fixture)
    {
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, Flat());
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        return nav;
    }

    /// <summary>A collision-model line test that is always blocked, so a flier reaches the mesh search (PathFinder.cpp:172-186).</summary>
    private sealed class Blocked : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => false;

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

    [Theory]
    [InlineData(24.5f, 0f, PathType.Normal)]      // 4.5 yd beside the mesh: inside the 5 yd box, within 7 yd
    [InlineData(24f, 4.5f, PathType.Normal)]      // 4 beside and 4.5 above: 3D distance 6.0
    [InlineData(10f, 8f, PathType.Incomplete)]    // 8 above: inside the 10 yd box, past farFromPoly
    [InlineData(10f, 15f, PathType.NoPath)]       // 15 above: outside the 10 yd box
    [InlineData(10f, -4f, PathType.NoPath)]       // the mesh 4 above the start: refused
    [InlineData(26f, 0f, PathType.NoPath)]        // 6 beside: outside the 5 yd box
    public void AQueryWithAnEndTolerance_LocatesItsStart_AsVmangosFindWalkPoly(float startX, float startZ, PathType expected)
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture);
        var start = new Vector3(startX, 10, startZ);

        PathResult path = nav.FindPath(MapId, start, OnMesh, Vmangos);

        Assert.Equal(expected, path.Type);
        Assert.Equal(start, path.Points[0]);
        if (expected != PathType.NoPath)
        {
            Assert.True(path.HasPath);
            Assert.Equal(OnMesh.X, path.End.X, 3); // the corridor still reaches the end's polygon
            Assert.Equal(OnMesh.Y, path.End.Y, 3);
        }
    }

    [Theory]
    [InlineData(10f, 15f, PathType.Normal)]   // the tall second box finds the floor 15 below
    [InlineData(10f, -4f, PathType.Normal)]   // the first box finds the floor 4 above
    [InlineData(24.5f, 0f, PathType.NoPath)]  // 4.5 beside: outside the 3 yd box
    public void AQueryWithoutAnEndTolerance_KeepsTheSearchBoxesForItsStart(float startX, float startZ, PathType expected)
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture);

        PathResult path = nav.FindPath(MapId, new Vector3(startX, 10, startZ), OnMesh, new PathOptions { Mover = PathMover.FromInhabit(1) });

        Assert.Equal(expected, path.Type);
    }

    [Theory]
    [InlineData(10f, 15f, true)]    // only the tall second box finds the floor, 15 below
    [InlineData(10f, 60f, true)]    // 60 above an unmeshed ledge's floor (issue #1501)
    [InlineData(10f, -8f, true)]    // the floor 8 above
    [InlineData(10f, 4.5f, false)]  // inside the first box
    [InlineData(10f, -4f, false)]   // the first box finds the floor 4 above
    [InlineData(10f, 0f, false)]    // on the floor
    public void AQueryWithoutAnEndTolerance_TellsAStartFoundOnlyFarAboveOrBelowItsPolygon(float startX, float startZ, bool expected)
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture);

        PathResult path = nav.FindPath(MapId, new Vector3(startX, 10, startZ), OnMesh, new PathOptions { Mover = PathMover.FromInhabit(1) });

        Assert.True(path.HasPath);
        Assert.Equal(expected, path.StartFar);
    }

    [Fact]
    public void AQueryWithAnEndTolerance_NeverTellsAFarStart()
    {
        // vmangos grades the start itself (farFromPoly, PathFinder.cpp:209-235): the path is incomplete, StartFar stays unset.
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture);

        PathResult path = nav.FindPath(MapId, new Vector3(10, 10, 8), OnMesh, Vmangos);

        Assert.Equal(PathType.Incomplete, path.Type);
        Assert.False(path.StartFar);
    }

    [Theory]
    [InlineData(10f, 8f, 4f, 0f)]  // the start 8 above its polygon
    [InlineData(10f, 0f, 4f, 8f)]  // the end 8 above its polygon
    public void AFlierFarFromAnEndpointsPolygon_GoesStraight(float startX, float startZ, float endX, float endZ)
    {
        // farFromPoly with CanFly: BuildShortcut, PATHFIND_NORMAL | PATHFIND_NOT_USING_PATH (PathFinder.cpp:220-225).
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture);
        nav.LineOfSight = new Blocked();
        var start = new Vector3(startX, 10, startZ);
        var end = new Vector3(endX, 10, endZ);
        var options = new PathOptions { Mover = PathMover.FromInhabit(5), EndTolerance = PathOptions.VmangosEndTolerance };

        PathResult path = nav.FindPath(MapId, start, end, options);

        Assert.Equal(PathType.Normal | PathType.NotUsingPath, path.Type);
        Assert.Equal([start, end], path.Points);
    }
}
