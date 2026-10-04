using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Gm.Objects;

/// <summary>Plain 3D centre-to-centre distance for the GM listings (not the bounding-radius distance gameplay rules use).</summary>
public static class GmDistance
{
    public static float Between(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
