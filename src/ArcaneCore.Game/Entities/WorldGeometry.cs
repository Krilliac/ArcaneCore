namespace ArcaneCore.Game.Entities;

/// <summary>How <see cref="WorldGeometry.IsWithinDist"/> decides the two objects share a map.</summary>
internal enum MapCheck
{
    /// <summary>No map check.</summary>
    None,

    /// <summary>The two objects have the same <see cref="WorldObject.MapId"/>.</summary>
    Id,

    /// <summary>The two objects reference the same <see cref="WorldObject.Map"/> instance.</summary>
    Ref,
}

/// <summary>
/// The distance variants the game code uses between two world objects (vmangos GetDistance / GetDistance3dToCenter /
/// IsWithinDistInMap). Every variant keeps the float operation order of the hand-written copies it replaces:
/// <c>(dx * dx) + (dy * dy) + (dz * dz)</c> with <c>d = a - b</c> per axis.
/// <para>Stateless and thread-safe; callers own the thread affinity of the objects they pass.</para>
/// </summary>
internal static class WorldGeometry
{
    /// <summary>Squared 3D distance between the two object centres.</summary>
    public static float DistanceSq3D(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>3D distance between the two object centres.</summary>
    public static float CenterDistance3D(WorldObject a, WorldObject b) => MathF.Sqrt(DistanceSq3D(a, b));

    /// <summary>3D centre distance minus both bounding radii, clamped at 0 (vmangos GetDistance).</summary>
    public static float DistanceMinusRadii(WorldObject a, WorldObject b)
    {
        float d = CenterDistance3D(a, b) - a.BoundingRadius - b.BoundingRadius;
        return d > 0 ? d : 0;
    }

    /// <summary>2D (X/Y) distance between the two object centres.</summary>
    public static float Distance2D(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Whether the centres lie within <paramref name="range"/> in 3D, compared squared. With <paramref name="addRadii"/> both bounding
    /// radii are added to the range first. <paramref name="inclusive"/> picks <c>&lt;=</c> over <c>&lt;</c>; a failed
    /// <paramref name="mapCheck"/> returns false.
    /// </summary>
    public static bool IsWithinDist(WorldObject a, WorldObject b, float range, bool inclusive, bool addRadii, MapCheck mapCheck)
    {
        if (mapCheck == MapCheck.Id ? a.MapId != b.MapId : mapCheck == MapCheck.Ref && !ReferenceEquals(a.Map, b.Map))
        {
            return false;
        }

        float limit = addRadii ? range + a.BoundingRadius + b.BoundingRadius : range;
        float distanceSq = DistanceSq3D(a, b);
        return inclusive ? distanceSq <= limit * limit : distanceSq < limit * limit;
    }
}
