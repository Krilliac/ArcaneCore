using System.Numerics;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Game.DebugDraw;

/// <summary>A cell corner of the map lattice near a point: its offset from the centre corner, its world position, and whether a grid boundary passes through it.</summary>
public readonly record struct LatticeCorner(int OffsetX, int OffsetY, float X, float Y, bool IsGridCorner);

/// <summary>
/// The point sampling behind the debug-draw commands: where the markers of a line, a path, a lattice or a circle go. Pure functions, every
/// result bounded by its <c>maxPoints</c>.
/// </summary>
public static class DebugDrawGeometry
{
    /// <summary>
    /// Points along the segment <paramref name="from"/> → <paramref name="to"/>, start and end included, about
    /// <paramref name="spacing"/> apart. When that would need more than <paramref name="maxPoints"/> points they are spread evenly
    /// instead (so a long line keeps both ends). Points closer than <paramref name="skipNearStart"/> to the start are left out (a
    /// marker on the invoker would sit inside the character). Spacing below 0.5 yards counts as 0.5.
    /// </summary>
    public static IReadOnlyList<Vector3> SampleSegment(Vector3 from, Vector3 to, float spacing, int maxPoints, float skipNearStart = 0f)
    {
        if (maxPoints <= 0 || !IsFinite(from) || !IsFinite(to))
        {
            return [];
        }

        float length = Vector3.Distance(from, to);
        if (length < 1e-3f || maxPoints == 1)
        {
            return length >= skipNearStart ? [to] : [];
        }

        spacing = MathF.Max(spacing, 0.5f);
        int steps = (int)MathF.Ceiling(length / spacing);
        steps = Math.Clamp(steps, 1, maxPoints - 1);
        var points = new List<Vector3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            if (length * t < skipNearStart)
            {
                continue;
            }

            points.Add(Vector3.Lerp(from, to, t));
        }

        return points;
    }

    /// <summary>
    /// The fill points between consecutive <paramref name="corners"/> (corners themselves excluded), about <paramref name="spacing"/>
    /// apart along the whole polyline, at most <paramref name="maxPoints"/>: the spacing widens evenly when the polyline is too long.
    /// With <paramref name="closed"/> the last corner joins the first (a waypoint loop).
    /// </summary>
    public static IReadOnlyList<Vector3> SamplePolylineFill(IReadOnlyList<Vector3> corners, float spacing, int maxPoints, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (maxPoints <= 0 || corners.Count < 2)
        {
            return [];
        }

        int segments = closed ? corners.Count : corners.Count - 1;
        float total = 0;
        for (int s = 0; s < segments; s++)
        {
            total += Vector3.Distance(corners[s], corners[(s + 1) % corners.Count]);
        }

        if (!float.IsFinite(total) || total < 1e-3f)
        {
            return [];
        }

        spacing = MathF.Max(MathF.Max(spacing, 0.5f), total / (maxPoints + 1));
        var points = new List<Vector3>();
        for (int s = 0; s < segments && points.Count < maxPoints; s++)
        {
            Vector3 a = corners[s];
            Vector3 b = corners[(s + 1) % corners.Count];
            float length = Vector3.Distance(a, b);
            int inner = (int)MathF.Ceiling(length / spacing) - 1;
            for (int i = 1; i <= inner && points.Count < maxPoints; i++)
            {
                points.Add(Vector3.Lerp(a, b, (float)i / (inner + 1)));
            }
        }

        return points;
    }

    /// <summary>
    /// The cell corners within <paramref name="radius"/> cells of the corner at or below (<paramref name="x"/>, <paramref name="y"/>):
    /// (2r+1)² corners, <paramref name="radius"/> clamped to 0..<paramref name="maxRadius"/>. Cell boundaries lie on multiples of
    /// <see cref="GridDefines.SizeOfGridCell"/> (<see cref="GridDefines.ComputeCellCoord"/> is floor(x / cell) + 512), and a boundary is
    /// also a grid boundary when its multiple is divisible by <see cref="GridDefines.MaxNumberOfCells"/>.
    /// </summary>
    public static IReadOnlyList<LatticeCorner> CellLattice(float x, float y, int radius, int maxRadius = 5)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return [];
        }

        radius = Math.Clamp(radius, 0, Math.Max(0, maxRadius));
        const float cell = GridDefines.SizeOfGridCell;
        long baseX = (long)MathF.Floor(x / cell);
        long baseY = (long)MathF.Floor(y / cell);
        var corners = new List<LatticeCorner>((2 * radius + 1) * (2 * radius + 1));
        for (int i = -radius; i <= radius; i++)
        {
            for (int j = -radius; j <= radius; j++)
            {
                long kx = baseX + i;
                long ky = baseY + j;
                bool grid = kx % GridDefines.MaxNumberOfCells == 0 && ky % GridDefines.MaxNumberOfCells == 0;
                corners.Add(new LatticeCorner(i, j, kx * cell, ky * cell, grid));
            }
        }

        return corners;
    }

    /// <summary><paramref name="count"/> points evenly on the horizontal circle of <paramref name="radius"/> around <paramref name="center"/> (z of the centre), starting east.</summary>
    public static IReadOnlyList<Vector3> Ring(Vector3 center, float radius, int count)
    {
        if (count <= 0 || !IsFinite(center) || !float.IsFinite(radius) || radius <= 0)
        {
            return [];
        }

        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float angle = 2 * MathF.PI * i / count;
            points[i] = new Vector3(center.X + radius * MathF.Cos(angle), center.Y + radius * MathF.Sin(angle), center.Z);
        }

        return points;
    }

    /// <summary>The end of a ray of <paramref name="distance"/> yards from <paramref name="from"/> along the horizontal facing <paramref name="orientation"/>.</summary>
    public static Vector3 Ahead(Vector3 from, float orientation, float distance)
        => new(from.X + distance * MathF.Cos(orientation), from.Y + distance * MathF.Sin(orientation), from.Z);

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
