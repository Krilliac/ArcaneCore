using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Teleport;

/// <summary>Area-trigger geometry (vmangos ObjectMgr.cpp <c>IsPointInAreaTriggerZone</c>).</summary>
public static class AreaTriggerZone
{
    /// <summary>
    /// The tolerance the CMSG_AREATRIGGER handler allows for client/server position lag
    /// (vmangos HandleAreaTriggerOpcode: <c>const float delta = 5.0f</c>).
    /// </summary>
    public const float ClientDelta = 5.0f;

    /// <summary>
    /// Whether the point lies in the trigger: a sphere when the trigger has a radius, otherwise
    /// an oriented box (the point is rotated by 2π − box orientation so the box edges are axis
    /// aligned), each grown by <paramref name="delta"/> on every side.
    /// </summary>
    public static bool Contains(AreaTriggerTemplate trigger, uint mapId, float x, float y, float z, float delta = 0.0f)
    {
        if (mapId != trigger.MapId)
        {
            return false;
        }

        if (trigger.Radius > 0)
        {
            float distSq = ((x - trigger.X) * (x - trigger.X)) + ((y - trigger.Y) * (y - trigger.Y)) + ((z - trigger.Z) * (z - trigger.Z));
            float reach = trigger.Radius + delta;
            return distSq <= reach * reach;
        }

        double rotation = (2 * Math.PI) - trigger.BoxOrientation;
        double sin = Math.Sin(rotation);
        double cos = Math.Cos(rotation);
        float px = x - trigger.X;
        float py = y - trigger.Y;
        float dx = (float)((px * cos) - (py * sin));
        float dy = (float)((py * cos) + (px * sin));
        float dz = z - trigger.Z;
        return Math.Abs(dx) <= (trigger.BoxX / 2) + delta
            && Math.Abs(dy) <= (trigger.BoxY / 2) + delta
            && Math.Abs(dz) <= (trigger.BoxZ / 2) + delta;
    }
}
