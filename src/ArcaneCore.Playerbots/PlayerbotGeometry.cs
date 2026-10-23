using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The small geometry helpers the Playerbots share: the 3D distance of two world objects, the finiteness of a position and the flat
/// (X, Y) projection of a point. Pure and allocation-free; callable from any thread.
/// </summary>
internal static class PlayerbotGeometry
{
    /// <summary>The straight-line (3D) distance between two world objects' stored positions: component deltas, summed x then y then z.</summary>
    internal static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>The object's position as the bots steer by it: a player's in-flight motion position, anything else's stored X, Y, Z.</summary>
    internal static Vector3 MotionPosition(WorldObject o) => o is Player p ? PlayerbotMotion.CurrentPosition(p) : new(o.X, o.Y, o.Z);

    /// <summary>The distance between two objects' <see cref="MotionPosition"/>s (Vector3.Distance, unlike the component-sum <see cref="Distance"/>).</summary>
    internal static float MotionDistance(WorldObject a, WorldObject b) => Vector3.Distance(MotionPosition(a), MotionPosition(b));

    /// <summary>Whether every component of the point is finite (neither NaN nor infinite).</summary>
    internal static bool IsFinite(this Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Whether every coordinate of the spawn is finite (neither NaN nor infinite).</summary>
    internal static bool IsFinite(this CreatureSpawn spawn) => float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z);

    /// <summary>The point's ground-plane (X, Y) projection; Z is dropped.</summary>
    internal static Vector2 Flat(Vector3 value) => new(value.X, value.Y);
}
