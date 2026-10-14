using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Post-processes multi-corner NavMesh paths for creatures and server-driven unit splines:
/// <list type="number">
/// <item><description><b>Per-creature lateral corridor variation</b>: applies a subtle deterministic lateral offset
/// (<c>±0.85</c> yards derived from the creature's GUID counter, tapered to zero at start and destination) on interior
/// corners of paths <c>&gt;= 12</c> yards when verified walkable via <see cref="IMeshPointQuery.Raycast"/> and height lookup,
/// preventing groups of chasing or patrolling NPCs from stacking onto a single 1D wire.</description></item>
/// <item><description><b>Quadratic Bezier corner-cutting spline smoothing</b>: replaces sharp interior polygon-portal
/// turns (<c>&gt;= 15°</c>) with 3 smooth Bezier arc points (<c>u = 0.25, 0.50, 0.75</c>) verified against the navmesh
/// and terrain height, keeping total points bounded (<c>&lt;= 32</c>) and spaced <c>&gt;= 0.5</c> yards apart for
/// <c>SMSG_MONSTER_MOVE</c>'s 11/11/10-bit quarter-yard offset packing.</description></item>
/// </list>
/// </summary>
public static class CreatureSplineSmoothing
{
    public const float MaxLateralOffsetYards = 0.85f;
    public const float MinVariationPathLengthYards = 12.0f;
    public const float MaxCornerCutArmYards = 3.5f;
    public const float MinCornerArmYards = 2.0f;
    public const float MinCornerTurnCos = 0.9659f; // cos(15 deg)
    public const float MaxHeightDeltaYards = 1.5f;
    public const int MaxSplinePoints = 32;

    /// <summary>
    /// Apply subtle per-creature lateral variation (when <paramref name="allowVariation"/> is true) and quadratic Bezier
    /// corner-cutting smoothing to <paramref name="corners"/> (every point after <paramref name="start"/>, destination last).
    /// Preserves <c>corners[^1]</c> exactly.
    /// </summary>
    public static List<Vector3> VaryAndSmooth(
        Creature creature,
        Vector3 start,
        List<Vector3> corners,
        IMeshPointQuery mesh,
        Func<uint, float, float, float, float?> getHeight,
        PathOptions? pathOptions = null,
        bool allowVariation = true)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(corners);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(getHeight);

        if (corners.Count < 2)
        {
            return corners;
        }

        if (allowVariation)
        {
            ApplyLateralVariation(creature.MapId, creature.Guid.Counter, start, corners, mesh, getHeight, pathOptions);
        }

        return SmoothCorners(creature.MapId, start, corners, mesh, getHeight, pathOptions);
    }

    /// <summary>
    /// Replace sharp interior corners (<c>&gt;= 15°</c>) of <paramref name="corners"/> with 3-point quadratic Bezier arcs
    /// when every sub-chord passes <see cref="IMeshPointQuery.Raycast"/> and stays within <see cref="MaxHeightDeltaYards"/> of the floor.
    /// Preserves <c>corners[^1]</c> exactly.
    /// </summary>
    public static List<Vector3> SmoothCorners(
        uint mapId,
        Vector3 start,
        List<Vector3> corners,
        IMeshPointQuery mesh,
        Func<uint, float, float, float, float?> getHeight,
        PathOptions? pathOptions = null)
    {
        ArgumentNullException.ThrowIfNull(corners);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(getHeight);

        if (corners.Count < 2 || corners.Count >= MaxSplinePoints)
        {
            return corners;
        }

        var smoothed = new List<Vector3>(Math.Min(MaxSplinePoints, corners.Count + 12));
        Vector3 prev = start;

        for (int i = 0; i < corners.Count - 1; i++)
        {
            Vector3 b = corners[i];
            Vector3 c = corners[i + 1];

            int remainingAfter = corners.Count - 1 - i;
            if (smoothed.Count + 3 + remainingAfter <= MaxSplinePoints
                && TryBuildBezierCorner(mapId, prev, b, c, mesh, getHeight, pathOptions, out Vector3 q1, out Vector3 q2, out Vector3 q3))
            {
                smoothed.Add(q1);
                smoothed.Add(q2);
                smoothed.Add(q3);
                prev = q3;
            }
            else
            {
                smoothed.Add(b);
                prev = b;
            }
        }

        smoothed.Add(corners[^1]);
        return smoothed;
    }

    private static void ApplyLateralVariation(
        uint mapId,
        ulong seedCounter,
        Vector3 start,
        List<Vector3> corners,
        IMeshPointQuery mesh,
        Func<uint, float, float, float, float?> getHeight,
        PathOptions? pathOptions)
    {
        float totalLen = 0f;
        Vector3 prev = start;
        for (int i = 0; i < corners.Count; i++)
        {
            totalLen += FlatDistance(prev, corners[i]);
            prev = corners[i];
        }

        if (totalLen < MinVariationPathLengthYards)
        {
            return;
        }

        uint h = HashSeed(seedCounter, start, corners[^1]);
        float unitSigned = ((h & 0xFFFFu) / 32767.5f) - 1.0f;
        float lateralBias = unitSigned * MaxLateralOffsetYards;
        if (MathF.Abs(lateralBias) < 0.08f)
        {
            return;
        }

        float cumDist = 0f;
        prev = start;
        for (int i = 0; i < corners.Count - 1; i++)
        {
            Vector3 cur = corners[i];
            Vector3 next = corners[i + 1];
            float legIn = FlatDistance(prev, cur);
            cumDist += legIn;

            float progress = Math.Clamp(cumDist / totalLen, 0f, 1f);
            float envelope = MathF.Sin(MathF.PI * progress);
            float offset = lateralBias * envelope;
            if (MathF.Abs(offset) >= 0.10f && legIn >= MinCornerArmYards && FlatDistance(cur, next) >= MinCornerArmYards)
            {
                Vector2 dirIn = NormalizeFlat(cur - prev);
                Vector2 dirOut = NormalizeFlat(next - cur);
                Vector2 tangent = dirIn + dirOut;
                float tLenSq = tangent.LengthSquared();
                Vector2 perp = tLenSq > 1e-4f
                    ? new Vector2(-tangent.Y, tangent.X) / MathF.Sqrt(tLenSq)
                    : new Vector2(-dirIn.Y, dirIn.X);

                var candidate = new Vector3(cur.X + (perp.X * offset), cur.Y + (perp.Y * offset), cur.Z);
                if (TryValidateShiftedCorner(mapId, prev, cur, next, candidate, mesh, getHeight, pathOptions, out Vector3 grounded))
                {
                    corners[i] = grounded;
                    prev = grounded;
                    continue;
                }
            }

            prev = cur;
        }
    }

    private static bool TryValidateShiftedCorner(
        uint mapId,
        Vector3 prev,
        Vector3 original,
        Vector3 next,
        Vector3 candidate,
        IMeshPointQuery mesh,
        Func<uint, float, float, float, float?> getHeight,
        PathOptions? pathOptions,
        out Vector3 grounded)
    {
        grounded = original;
        MeshRaycastResult rayIn = mesh.Raycast(mapId, prev, candidate, pathOptions);
        if (!rayIn.Available || rayIn.Hit)
        {
            return false;
        }

        MeshRaycastResult rayOut = mesh.Raycast(mapId, candidate, next, pathOptions);
        if (!rayOut.Available || rayOut.Hit)
        {
            return false;
        }

        float z = candidate.Z;
        if (getHeight(mapId, candidate.X, candidate.Y, candidate.Z + 2f) is { } floor
            && float.IsFinite(floor)
            && floor > TerrainTile.InvalidHeight)
        {
            if (MathF.Abs(floor - original.Z) > MaxHeightDeltaYards)
            {
                return false;
            }

            z = floor;
        }

        grounded = new Vector3(candidate.X, candidate.Y, z);
        return true;
    }

    private static bool TryBuildBezierCorner(
        uint mapId,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        IMeshPointQuery mesh,
        Func<uint, float, float, float, float?> getHeight,
        PathOptions? pathOptions,
        out Vector3 q1,
        out Vector3 q2,
        out Vector3 q3)
    {
        q1 = q2 = q3 = default;

        float inLen = FlatDistance(a, b);
        float outLen = FlatDistance(b, c);
        if (inLen < MinCornerArmYards || outLen < MinCornerArmYards)
        {
            return false;
        }

        Vector2 dirIn = new Vector2(b.X - a.X, b.Y - a.Y) / inLen;
        Vector2 dirOut = new Vector2(c.X - b.X, c.Y - b.Y) / outLen;
        float cos = Vector2.Dot(dirIn, dirOut);
        if (cos > MinCornerTurnCos || cos < -0.85f)
        {
            return false;
        }

        float arm = MathF.Min(MaxCornerCutArmYards, 0.35f * MathF.Min(inLen, outLen));
        if (arm < 0.75f)
        {
            return false;
        }

        Vector3 p0 = Vector3.Lerp(b, a, arm / inLen);
        Vector3 p2 = Vector3.Lerp(b, c, arm / outLen);

        if (!TryEvalBezierPoint(mapId, p0, b, p2, 0.25f, getHeight, out q1)
            || !TryEvalBezierPoint(mapId, p0, b, p2, 0.50f, getHeight, out q2)
            || !TryEvalBezierPoint(mapId, p0, b, p2, 0.75f, getHeight, out q3))
        {
            return false;
        }

        // Keep points at least 0.5 yards apart so 11/11/10-bit quarter-yard offsets in SMSG_MONSTER_MOVE stay distinct.
        if (Vector3.DistanceSquared(a, q1) < 0.25f
            || Vector3.DistanceSquared(q1, q2) < 0.25f
            || Vector3.DistanceSquared(q2, q3) < 0.25f
            || Vector3.DistanceSquared(q3, c) < 0.25f)
        {
            return false;
        }

        if (!RaycastClear(mesh, mapId, a, q1, pathOptions)
            || !RaycastClear(mesh, mapId, q1, q2, pathOptions)
            || !RaycastClear(mesh, mapId, q2, q3, pathOptions)
            || !RaycastClear(mesh, mapId, q3, c, pathOptions))
        {
            return false;
        }

        return true;
    }

    private static bool TryEvalBezierPoint(
        uint mapId,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        float u,
        Func<uint, float, float, float, float?> getHeight,
        out Vector3 result)
    {
        float om = 1f - u;
        Vector3 raw = (om * om * p0) + (2f * om * u * p1) + (u * u * p2);
        float z = raw.Z;
        if (getHeight(mapId, raw.X, raw.Y, raw.Z + 2f) is { } floor
            && float.IsFinite(floor)
            && floor > TerrainTile.InvalidHeight)
        {
            if (MathF.Abs(floor - raw.Z) > MaxHeightDeltaYards)
            {
                result = default;
                return false;
            }

            z = floor;
        }

        result = new Vector3(raw.X, raw.Y, z);
        return true;
    }

    private static bool RaycastClear(IMeshPointQuery mesh, uint mapId, Vector3 from, Vector3 to, PathOptions? options)
    {
        MeshRaycastResult ray = mesh.Raycast(mapId, from, to, options);
        return ray.Available && !ray.Hit;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static Vector2 NormalizeFlat(Vector3 v)
    {
        float len = MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));
        return len > 1e-4f ? new Vector2(v.X / len, v.Y / len) : new Vector2(1f, 0f);
    }

    private static uint HashSeed(ulong counter, Vector3 start, Vector3 end)
    {
        uint h = (uint)counter ^ (uint)(counter >> 32) ^ 0x9E3779B9u;
        h ^= (uint)((int)MathF.Round(start.X * 0.25f) * 73856093);
        h ^= (uint)((int)MathF.Round(end.Y * 0.25f) * 19349663);
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }
}
