using System.Numerics;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where one summoned bot stands and which way it faces (towards the anchor).</summary>
internal readonly record struct FleetSlot(float X, float Y, float Z, float Orientation);

/// <summary>
/// Ring placement for <c>.playerbot summon|send</c>: the bots stand in concentric rings around the anchor, never on one spot. Pure
/// (the ground and sight queries are passed in); any thread.
/// <para>
/// Ring k has radius <see cref="BaseRadius"/> + <see cref="RingStep"/> * k and holds as many slots as fit at least
/// <see cref="SlotSpacing"/> yards apart; the first slot is straight ahead of the anchor's facing. A slot's z is the ground there
/// (vmangos <c>UpdateGroundPositionZ</c>, Object.cpp:2079-2084: when the height is invalid z is kept, so a map without terrain data
/// puts every bot at the anchor's z). A slot more than <see cref="MaxStep"/> yards above or below the anchor, or out of the anchor's
/// line of sight (vmangos <c>GetNearPointAroundPosition</c> checks it), is left empty.
/// </para>
/// </summary>
internal static class PlayerbotFleetPlacement
{
    /// <summary>Radius of the innermost ring, in yards.</summary>
    internal const float BaseRadius = 3f;

    /// <summary>Radial distance between two rings, in yards.</summary>
    internal const float RingStep = 2.25f;

    /// <summary>The most rings (radius up to <see cref="BaseRadius"/> + 7 * <see cref="RingStep"/> = 18.75 yards).</summary>
    internal const int MaxRings = 8;

    /// <summary>
    /// The arc length the slot count is chosen for. Neighbours on a ring are a chord apart, a hair shorter than the arc, so the
    /// target is a little above the 2 yards the commands promise.
    /// </summary>
    internal const float SlotSpacing = 2.1f;

    /// <summary>The greatest height difference to the anchor a slot may have, in yards.</summary>
    internal const float MaxStep = 5f;

    /// <summary>
    /// Up to <paramref name="count"/> distinct slots around <paramref name="anchor"/>. <paramref name="ground"/> is
    /// <c>(x, y, anchorZ) -> z</c> (invalid or non-finite: keep the anchor's z); <paramref name="clear"/> says whether the slot is in
    /// the anchor's line of sight. Fewer slots come back when the rings run out or the terrain refuses them; callers skip the bots
    /// left over ("no-free-spot").
    /// </summary>
    internal static IReadOnlyList<FleetSlot> Ring(Vector3 anchor, float orientation, int count, Func<float, float, float, float> ground,
        Func<Vector3, bool> clear)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(clear);
        var slots = new List<FleetSlot>(Math.Clamp(count, 0, 64));
        for (int ring = 0; ring < MaxRings && slots.Count < count; ring++)
        {
            float radius = BaseRadius + (RingStep * ring);
            int perRing = (int)MathF.Floor(MathF.Tau * radius / SlotSpacing);
            for (int i = 0; i < perRing && slots.Count < count; i++)
            {
                float angle = orientation + (MathF.Tau * i / perRing);
                float x = anchor.X + (MathF.Cos(angle) * radius);
                float y = anchor.Y + (MathF.Sin(angle) * radius);
                float z = ground(x, y, anchor.Z);
                if (!float.IsFinite(z) || z <= TerrainTile.InvalidHeight) z = anchor.Z;
                if (MathF.Abs(z - anchor.Z) > MaxStep) continue;
                if (!clear(new Vector3(x, y, z))) continue;
                float facing = MathF.Atan2(anchor.Y - y, anchor.X - x);
                if (facing < 0) facing += MathF.Tau;
                slots.Add(new FleetSlot(x, y, z, facing));
            }
        }

        return slots;
    }
}
