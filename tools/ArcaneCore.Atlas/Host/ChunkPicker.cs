using System.Runtime.CompilerServices;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Host;

/// <summary>The nearest primitive of one chunk that a ray met.</summary>
/// <param name="Distance">Distance along the (unit) ray, yards.</param>
/// <param name="Primitive"><c>triangle</c>, <c>line</c> or <c>point</c>.</param>
/// <param name="Attribute">The primitive's attribute id.</param>
/// <param name="Point">World position of the hit (for lines and points, the ray's closest approach).</param>
internal readonly record struct ChunkHit(double Distance, string Primitive, uint Attribute, WorldPoint Point);

/// <summary>
/// Ray tests against <see cref="GeometryChunk"/>s in double precision. Triangles are intersected exactly (both faces),
/// segments and points match when the ray passes within <see cref="Tolerance"/> yards of them. Chunks are immutable,
/// so each chunk's bounding box is computed once and remembered for as long as the chunk is alive.
/// </summary>
internal static class ChunkPicker
{
    /// <summary>How close a ray must pass to a line or point to pick it, yards.</summary>
    public const double Tolerance = 0.25;

    private static readonly ConditionalWeakTable<GeometryChunk, Bounds> BoundsOf = new();

    private sealed record Bounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    /// <summary>
    /// The nearest hit of <paramref name="chunk"/> along the ray, or null. <paramref name="direction"/> must be a unit
    /// vector; the ray covers [0, <paramref name="maxDistance"/>].
    /// </summary>
    public static ChunkHit? Nearest(GeometryChunk chunk, WorldPoint origin, WorldPoint direction, double maxDistance, bool includeLinesAndPoints = true)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Positions.Length == 0)
        {
            return null;
        }

        Bounds box = BoundsOf.GetValue(chunk, Measure);
        double ox = origin.X - chunk.Origin.X;
        double oy = origin.Y - chunk.Origin.Y;
        double oz = origin.Z - chunk.Origin.Z;
        double pad = includeLinesAndPoints ? Tolerance : 1e-6;
        if (!RayHitsBox(ox, oy, oz, direction.X, direction.Y, direction.Z, maxDistance, box, pad))
        {
            return null;
        }

        double bestT = double.PositiveInfinity;
        string primitive = string.Empty;
        uint attribute = 0;
        FindTriangle(chunk, ox, oy, oz, direction, maxDistance, ref bestT, ref primitive, ref attribute);
        if (includeLinesAndPoints)
        {
            FindLine(chunk, ox, oy, oz, direction, maxDistance, ref bestT, ref primitive, ref attribute);
            FindPoint(chunk, ox, oy, oz, direction, maxDistance, ref bestT, ref primitive, ref attribute);
        }

        if (double.IsPositiveInfinity(bestT))
        {
            return null;
        }

        var point = new WorldPoint(origin.X + (direction.X * bestT), origin.Y + (direction.Y * bestT), origin.Z + (direction.Z * bestT));
        return new ChunkHit(bestT, primitive, attribute, point);
    }

    private static Bounds Measure(GeometryChunk chunk)
    {
        float[] p = chunk.Positions;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            minX = Math.Min(minX, p[i]);
            maxX = Math.Max(maxX, p[i]);
            minY = Math.Min(minY, p[i + 1]);
            maxY = Math.Max(maxY, p[i + 1]);
            minZ = Math.Min(minZ, p[i + 2]);
            maxZ = Math.Max(maxZ, p[i + 2]);
        }

        return new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>Slab test of the segment [0, max] along (dx, dy, dz) against the box grown by <paramref name="pad"/>.</summary>
    private static bool RayHitsBox(double ox, double oy, double oz, double dx, double dy, double dz, double max, Bounds b, double pad)
    {
        double tMin = 0;
        double tMax = max;
        return Slab(ox, dx, b.MinX - pad, b.MaxX + pad, ref tMin, ref tMax)
            && Slab(oy, dy, b.MinY - pad, b.MaxY + pad, ref tMin, ref tMax)
            && Slab(oz, dz, b.MinZ - pad, b.MaxZ + pad, ref tMin, ref tMax);
    }

    private static bool Slab(double o, double d, double lo, double hi, ref double tMin, ref double tMax)
    {
        if (Math.Abs(d) < 1e-12)
        {
            return o >= lo && o <= hi;
        }

        double t1 = (lo - o) / d;
        double t2 = (hi - o) / d;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMin <= tMax;
    }

    /// <summary>Moller-Trumbore ray/triangle intersection over every triangle; the nearest hit within the segment wins.</summary>
    private static void FindTriangle(GeometryChunk chunk, double ox, double oy, double oz, WorldPoint d, double max, ref double bestT, ref string primitive, ref uint attribute)
    {
        float[] p = chunk.Positions;
        uint[] tri = chunk.Triangles;
        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            int a = (int)tri[i] * 3;
            int b = (int)tri[i + 1] * 3;
            int c = (int)tri[i + 2] * 3;
            double ax = p[a], ay = p[a + 1], az = p[a + 2];
            double e1x = p[b] - ax, e1y = p[b + 1] - ay, e1z = p[b + 2] - az;
            double e2x = p[c] - ax, e2y = p[c + 1] - ay, e2z = p[c + 2] - az;

            // pvec = d x e2
            double px = (d.Y * e2z) - (d.Z * e2y);
            double py = (d.Z * e2x) - (d.X * e2z);
            double pz = (d.X * e2y) - (d.Y * e2x);
            double det = (e1x * px) + (e1y * py) + (e1z * pz);
            if (Math.Abs(det) < 1e-14)
            {
                continue;
            }

            double inv = 1 / det;
            double sx = ox - ax, sy = oy - ay, sz = oz - az;
            double u = ((sx * px) + (sy * py) + (sz * pz)) * inv;
            if (u < 0 || u > 1)
            {
                continue;
            }

            // qvec = s x e1
            double qx = (sy * e1z) - (sz * e1y);
            double qy = (sz * e1x) - (sx * e1z);
            double qz = (sx * e1y) - (sy * e1x);
            double v = ((d.X * qx) + (d.Y * qy) + (d.Z * qz)) * inv;
            if (v < 0 || u + v > 1)
            {
                continue;
            }

            double t = ((e2x * qx) + (e2y * qy) + (e2z * qz)) * inv;
            if (t >= 0 && t <= max && t < bestT)
            {
                bestT = t;
                primitive = "triangle";
                attribute = chunk.TriangleAttributes[i / 3];
            }
        }
    }

    /// <summary>Closest approach of the ray segment to every line segment; matches within <see cref="Tolerance"/>.</summary>
    private static void FindLine(GeometryChunk chunk, double ox, double oy, double oz, WorldPoint d, double max, ref double bestT, ref string primitive, ref uint attribute)
    {
        float[] p = chunk.Positions;
        uint[] lines = chunk.Lines;
        double d1x = d.X * max, d1y = d.Y * max, d1z = d.Z * max;
        double a = max * max;
        double tolSq = Tolerance * Tolerance;
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            int ia = (int)lines[i] * 3;
            int ib = (int)lines[i + 1] * 3;
            double p2x = p[ia], p2y = p[ia + 1], p2z = p[ia + 2];
            double d2x = p[ib] - p2x, d2y = p[ib + 1] - p2y, d2z = p[ib + 2] - p2z;
            double rx = ox - p2x, ry = oy - p2y, rz = oz - p2z;
            double e = (d2x * d2x) + (d2y * d2y) + (d2z * d2z);
            double f = (d2x * rx) + (d2y * ry) + (d2z * rz);
            double c = (d1x * rx) + (d1y * ry) + (d1z * rz);
            double s;
            double t;
            if (e <= 1e-12)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                double b = (d1x * d2x) + (d1y * d2y) + (d1z * d2z);
                double denom = (a * e) - (b * b);
                s = denom > 1e-12 ? Math.Clamp(((b * f) - (c * e)) / denom, 0, 1) : 0;
                t = ((b * s) + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }

            double cx = ox + (d1x * s) - (p2x + (d2x * t));
            double cy = oy + (d1y * s) - (p2y + (d2y * t));
            double cz = oz + (d1z * s) - (p2z + (d2z * t));
            double distance = s * max;
            if (((cx * cx) + (cy * cy) + (cz * cz)) <= tolSq && distance < bestT)
            {
                bestT = distance;
                primitive = "line";
                attribute = chunk.LineAttributes[i / 2];
            }
        }
    }

    private static void FindPoint(GeometryChunk chunk, double ox, double oy, double oz, WorldPoint d, double max, ref double bestT, ref string primitive, ref uint attribute)
    {
        float[] p = chunk.Positions;
        double tolSq = Tolerance * Tolerance;
        for (int i = 0; i < chunk.Points.Length; i++)
        {
            int v = (int)chunk.Points[i] * 3;
            double rx = p[v] - ox, ry = p[v + 1] - oy, rz = p[v + 2] - oz;
            double t = Math.Clamp((rx * d.X) + (ry * d.Y) + (rz * d.Z), 0, max);
            double cx = rx - (d.X * t), cy = ry - (d.Y * t), cz = rz - (d.Z * t);
            if (((cx * cx) + (cy * cy) + (cz * cz)) <= tolSq && t < bestT)
            {
                bestT = t;
                primitive = "point";
                attribute = chunk.PointAttributes[i];
            }
        }
    }
}

/// <summary>Which 64x64 grid tiles a ray crosses, in order from its origin.</summary>
internal static class TileTraversal
{
    /// <summary>
    /// Walks the tile grid (tile u = 32 - x/533.33, v = 32 - y/533.33, index floor(u), floor(v): doc section 3) with a
    /// grid DDA over the ray's horizontal footprint. <paramref name="direction"/> is a unit vector. A purely vertical ray
    /// yields its own tile; a ray that never enters the map yields nothing.
    /// </summary>
    public static IEnumerable<TileKey> Along(uint map, WorldPoint origin, WorldPoint direction, double maxDistance)
    {
        const int n = WorldGrid.TilesPerSide;
        double g = WorldGrid.TileSize;
        double u0 = 32 - (origin.X / g);
        double v0 = 32 - (origin.Y / g);
        double du = -direction.X / g;
        double dv = -direction.Y / g;
        double tEnter = 0;
        double tExit = maxDistance;
        if (!Clip(u0, du, n, ref tEnter, ref tExit) || !Clip(v0, dv, n, ref tEnter, ref tExit))
        {
            yield break;
        }

        double u = u0 + (du * tEnter);
        double v = v0 + (dv * tEnter);
        int cu = Math.Clamp((int)Math.Floor(u), 0, n - 1);
        int cv = Math.Clamp((int)Math.Floor(v), 0, n - 1);
        int stepU = du > 0 ? 1 : (du < 0 ? -1 : 0);
        int stepV = dv > 0 ? 1 : (dv < 0 ? -1 : 0);
        double nextU = stepU == 0 ? double.PositiveInfinity : tEnter + ((stepU > 0 ? cu + 1 - u : cu - u) / du);
        double nextV = stepV == 0 ? double.PositiveInfinity : tEnter + ((stepV > 0 ? cv + 1 - v : cv - v) / dv);
        double deltaU = stepU == 0 ? double.PositiveInfinity : 1 / Math.Abs(du);
        double deltaV = stepV == 0 ? double.PositiveInfinity : 1 / Math.Abs(dv);
        for (int guard = 0; guard < 2 * n + 2; guard++)
        {
            yield return new TileKey(map, cu, cv);
            if (nextU < nextV)
            {
                if (nextU > tExit)
                {
                    yield break;
                }

                cu += stepU;
                nextU += deltaU;
            }
            else
            {
                if (nextV > tExit)
                {
                    yield break;
                }

                cv += stepV;
                nextV += deltaV;
            }

            if (cu < 0 || cu >= n || cv < 0 || cv >= n)
            {
                yield break;
            }
        }
    }

    /// <summary>Intersects the ray's parameter range with the slab [0, n) of one tile axis.</summary>
    private static bool Clip(double start, double rate, int n, ref double tEnter, ref double tExit)
    {
        if (Math.Abs(rate) < 1e-15)
        {
            return start >= 0 && start < n;
        }

        double t1 = (0 - start) / rate;
        double t2 = (n - start) / rate;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tEnter = Math.Max(tEnter, t1);
        tExit = Math.Min(tExit, t2);
        return tEnter <= tExit;
    }
}
