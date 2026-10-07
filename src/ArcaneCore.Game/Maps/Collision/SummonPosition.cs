using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>Collision-backed caster-relative spell destination (vmangos Object.cpp MovePositionToFirstCollision).</summary>
public static class SummonPosition
{
    public static Vector3 Resolve(Unit caster, float radius, float angle)
    {
        Vector3 origin = new(caster.X, caster.Y, caster.Z);
        if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(angle)) return origin;
        Vector3 destination = origin + new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0);
        if (!Finite(origin) || !Finite(destination)) return origin;
        if (caster.Map is not { } map) return destination;

        MapCollision collision = map.Collision;
        float eye = MapCollision.DefaultEyeHeight;
        uint inhabit = caster is Creature creature ? creature.Template.InhabitType : 3u;
        var mover = new PathMover((inhabit & 1) != 0, (inhabit & 2) != 0, (inhabit & 4) != 0, caster is Player);
        PathResult path = collision.FindPath(origin, destination + new Vector3(0, 0, eye),
            new PathOptions { MaxPoints = 128, MaxSearchNodes = 512, Mover = mover });
        if ((path.Type & PathType.NoPath) == 0 && path.Points.Count > 0 && Finite(path.Points[^1]))
            destination = path.Points[^1];
        destination = Ground(collision, destination, origin.Z);
        if (collision.LineOfSight.TryGetObjectHit(map.MapId, origin + new Vector3(0, 0, eye),
                destination + new Vector3(0, 0, eye), -1f, out Vector3 hit) && Finite(hit))
            destination = hit - new Vector3(0, 0, eye);
        if (collision.LineOfSight.TryGetObjectHit(map.MapId, destination + new Vector3(0, 0, eye),
                destination, -0.5f, out hit) && Finite(hit)) destination = hit;

        // Retract in ten bounded steps when the ground would place the summon across
        // an abrupt vertical discontinuity. Missing terrain keeps the caster's Z.
        Vector3 candidate = destination;
        for (int i = 0; i <= 10; i++)
        {
            candidate = Ground(collision, candidate, origin.Z);
            if (Finite(candidate) && MathF.Abs(candidate.Z - origin.Z) <= 5f) return candidate;
            if (i < 10) candidate = Vector3.Lerp(destination, origin, (i + 1) / 10f);
        }
        return origin;
    }

    private static Vector3 Ground(MapCollision collision, Vector3 point, float fallback)
    {
        float height = collision.GetHeight(point.X, point.Y, MathF.Max(point.Z, fallback));
        point.Z = float.IsFinite(height) && height > TerrainTile.InvalidHeightValue + 1 ? height : fallback;
        return point;
    }

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
