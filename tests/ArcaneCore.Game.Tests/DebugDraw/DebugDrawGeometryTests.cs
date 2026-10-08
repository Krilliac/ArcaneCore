using System.Numerics;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Maps.Grid;
using Xunit;

namespace ArcaneCore.Game.Tests.DebugDraw;

/// <summary>The point sampling of the <c>.debug vis</c> markers (docs/areas/debug-draw.md): spacing, caps, the skipped start, the cell lattice and rings.</summary>
public sealed class DebugDrawGeometryTests
{
    [Fact]
    public void SampleSegment_SpacesPointsEvenly_EndsIncluded()
    {
        IReadOnlyList<Vector3> points = DebugDrawGeometry.SampleSegment(Vector3.Zero, new Vector3(10, 0, 0), spacing: 2, maxPoints: 100);

        Assert.Equal(6, points.Count); // 0, 2, 4, 6, 8, 10
        Assert.Equal(Vector3.Zero, points[0]);
        Assert.Equal(new Vector3(10, 0, 0), points[^1]);
        for (int i = 1; i < points.Count; i++)
        {
            Assert.Equal(2f, Vector3.Distance(points[i - 1], points[i]), 0.001f);
        }
    }

    [Fact]
    public void SampleSegment_SpreadsALongLineOverTheCap_KeepingBothEnds()
    {
        var to = new Vector3(0, 1000, 50);
        IReadOnlyList<Vector3> points = DebugDrawGeometry.SampleSegment(Vector3.Zero, to, spacing: 2, maxPoints: 10);

        Assert.Equal(10, points.Count);
        Assert.Equal(Vector3.Zero, points[0]);
        Assert.Equal(to, points[^1]);
    }

    [Fact]
    public void SampleSegment_LeavesOutPointsNearTheStart()
    {
        IReadOnlyList<Vector3> points = DebugDrawGeometry.SampleSegment(Vector3.Zero, new Vector3(10, 0, 0), spacing: 1, maxPoints: 100, skipNearStart: 3);

        Assert.All(points, p => Assert.True(p.X >= 3f));
        Assert.Equal(3f, points[0].X, 0.001f);
        Assert.Equal(8, points.Count); // 3..10
    }

    [Fact]
    public void SampleSegment_RejectsNonFiniteInput_AndClampsTinySpacing()
    {
        Assert.Empty(DebugDrawGeometry.SampleSegment(Vector3.Zero, new Vector3(float.NaN, 0, 0), 2, 10));
        Assert.Empty(DebugDrawGeometry.SampleSegment(Vector3.Zero, new Vector3(1, 0, 0), 2, 0));
        Assert.Equal(5, DebugDrawGeometry.SampleSegment(Vector3.Zero, new Vector3(2, 0, 0), spacing: 0.01f, maxPoints: 100).Count); // 0.5 yd floor
        Assert.Equal([new Vector3(1, 1, 1)], DebugDrawGeometry.SampleSegment(new Vector3(1, 1, 1), new Vector3(1, 1, 1), 2, 10));
    }

    [Fact]
    public void PolylineFill_ExcludesCorners_AndStaysOnTheSegments()
    {
        Vector3[] corners = [Vector3.Zero, new(10, 0, 0), new(10, 10, 0)];
        IReadOnlyList<Vector3> fill = DebugDrawGeometry.SamplePolylineFill(corners, spacing: 2, maxPoints: 100);

        Assert.Equal(8, fill.Count); // 4 inner points on each 10-yard segment
        Assert.DoesNotContain(corners[1], fill);
        Assert.All(fill, p => Assert.True(p.Y == 0 || p.X == 10));
    }

    [Fact]
    public void PolylineFill_WidensTheSpacingToTheCap_AndClosesALoop()
    {
        Vector3[] corners = [Vector3.Zero, new(100, 0, 0), new(100, 100, 0), new(0, 100, 0)];
        IReadOnlyList<Vector3> open = DebugDrawGeometry.SamplePolylineFill(corners, spacing: 1, maxPoints: 20);
        IReadOnlyList<Vector3> closed = DebugDrawGeometry.SamplePolylineFill(corners, spacing: 10, maxPoints: 100, closed: true);

        Assert.True(open.Count <= 20);
        Assert.True(open.Count >= 15);
        Assert.Contains(closed, p => p.X == 0 && p.Y > 0 && p.Y < 100); // the closing side back to the first corner
        Assert.Empty(DebugDrawGeometry.SamplePolylineFill([Vector3.Zero], 1, 10));
    }

    [Fact]
    public void CellLattice_SnapsToCellBoundaries_AndFlagsGridCorners()
    {
        const float cell = GridDefines.SizeOfGridCell;
        // Just north-east of the map origin: the corner below is (0, 0), a grid corner (cells and grids both meet at the origin).
        IReadOnlyList<LatticeCorner> corners = DebugDrawGeometry.CellLattice(5f, 7f, radius: 1);

        Assert.Equal(9, corners.Count);
        LatticeCorner centre = Assert.Single(corners, c => c.OffsetX == 0 && c.OffsetY == 0);
        Assert.Equal(0f, centre.X);
        Assert.Equal(0f, centre.Y);
        Assert.True(centre.IsGridCorner);
        Assert.Single(corners, c => c.IsGridCorner);
        Assert.Contains(corners, c => c.X == -cell && c.Y == cell && !c.IsGridCorner);

        // Every corner is a cell boundary: the cell index changes across it.
        foreach (LatticeCorner c in corners)
        {
            Assert.NotEqual(GridDefines.ComputeCellCoord(c.X - 0.01f, c.Y + 0.01f).X, GridDefines.ComputeCellCoord(c.X + 0.01f, c.Y + 0.01f).X);
        }

        // A grid boundary: 16 cells from the origin.
        IReadOnlyList<LatticeCorner> far = DebugDrawGeometry.CellLattice((16 * cell) + 1f, 1f, radius: 0);
        Assert.True(Assert.Single(far).IsGridCorner);
        Assert.NotEqual(GridDefines.ComputeGridCoord((16 * cell) - 0.01f, 1f).X, GridDefines.ComputeGridCoord((16 * cell) + 0.01f, 1f).X);
    }

    [Fact]
    public void CellLattice_ClampsTheRadius()
    {
        Assert.Equal(121, DebugDrawGeometry.CellLattice(-8949.95f, -132.49f, radius: 50).Count); // clamped to 5
        Assert.Single(DebugDrawGeometry.CellLattice(-8949.95f, -132.49f, radius: -3));
        Assert.Empty(DebugDrawGeometry.CellLattice(float.PositiveInfinity, 0, 2));
    }

    [Fact]
    public void Ring_PutsPointsOnTheCircle()
    {
        var center = new Vector3(100, 200, 30);
        IReadOnlyList<Vector3> ring = DebugDrawGeometry.Ring(center, 25, 36);

        Assert.Equal(36, ring.Count);
        Assert.All(ring, p => Assert.Equal(25f, Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(center.X, center.Y)), 0.01f));
        Assert.Equal(new Vector3(125, 200, 30), ring[0]);
        Assert.Empty(DebugDrawGeometry.Ring(center, 0, 36));
    }

    [Fact]
    public void Ahead_FollowsTheFacing()
    {
        Vector3 end = DebugDrawGeometry.Ahead(new Vector3(1, 2, 3), MathF.PI / 2, 10);
        Assert.Equal(1f, end.X, 0.001f);
        Assert.Equal(12f, end.Y, 0.001f);
        Assert.Equal(3f, end.Z);
    }
}
