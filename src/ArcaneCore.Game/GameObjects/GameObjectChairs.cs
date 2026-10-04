using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Chair geometry (GAMEOBJECT_TYPE_CHAIR): a chair has <c>data0</c> slots on a straight line through its centre, perpendicular to its
/// orientation, spaced by the template size; the user is seated at the slot nearest to them. Re-implemented from vmangos
/// GameObject::GetClosestChairSlotPosition (GameObject.cpp:2536-2582), PlayerCanUse (:2229-2236), ObjectMgr.cpp:8108-8118.
/// </summary>
public static class GameObjectChairs
{
    /// <summary>MAX_SITCHAIRUSE_DISTANCE (GameObjectDefines.h:799): the user must be this close (3D) to the slot to sit.</summary>
    public const float MaxSitDistance = 3.0f;

    /// <summary>The initial lowest distance of the slot search: DEFAULT_VISIBILITY_DISTANCE (ObjectDefines.h), 100 yards.</summary>
    public const float SlotSearchDistance = 100.0f;

    /// <summary>
    /// Hardening beyond vmangos: the most slots <see cref="ClosestSlot"/> evaluates. Real chairs have 1 to 4 (<c>data0</c>); a malformed
    /// imported value (up to 2^32-1) would otherwise run billions of trigonometric iterations on the world thread per use packet.
    /// Pass 0 for vmangos's unbounded loop.
    /// </summary>
    public const uint DefaultMaxSlots = 64;

    /// <summary>
    /// The slot position nearest to (<paramref name="userX"/>, <paramref name="userY"/>). With no slots, or no slot within
    /// <see cref="SlotSearchDistance"/> of the user, the chair centre. Of equally near slots the later one wins (the vmangos comparison is <c>&lt;=</c>).
    /// Uses the template size, not the spawn scale (<c>GetGOInfo()-&gt;size</c>).
    /// </summary>
    public static (float X, float Y) ClosestSlot(GameObject chair, float userX, float userY, uint maxSlots = DefaultMaxSlots)
    {
        ArgumentNullException.ThrowIfNull(chair);
        uint slots = chair.Template.GetData(0);
        if (maxSlots > 0 && slots > maxSlots)
        {
            // Imported data0 is a loop bound reached from one client packet: clamp it (see DefaultMaxSlots).
            slots = maxSlots;
        }

        float bestX = chair.X;
        float bestY = chair.Y;
        if (slots == 0)
        {
            return (bestX, bestY);
        }

        float lowest = SlotSearchDistance;
        float orthogonal = chair.Orientation + (MathF.PI * 0.5f);
        float size = chair.Template.Size;
        for (uint i = 0; i < slots; i++)
        {
            float relative = (size * i) - (size * (slots - 1) / 2.0f);
            float x = chair.X + (relative * MathF.Cos(orthogonal));
            float y = chair.Y + (relative * MathF.Sin(orthogonal));
            float distance = MathF.Sqrt(((userX - x) * (userX - x)) + ((userY - y) * (userY - y)));
            if (distance <= lowest)
            {
                lowest = distance;
                bestX = x;
                bestY = y;
            }
        }

        return (bestX, bestY);
    }

    /// <summary>
    /// The chair height of the template (data1): 0 low, 1 medium, 2 high. A value above 2 is a data error that vmangos
    /// replaces with 0 at load (ObjectMgr.cpp:8108-8118, CheckAndFixGOChairHeightId).
    /// </summary>
    public static byte Height(GameObjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        uint height = template.GetData(1);
        return height <= (uint)(StandState.SitHighChair - StandState.SitLowChair) ? (byte)height : (byte)0;
    }

    /// <summary>The stand state of a seated user: SIT_LOW_CHAIR plus the chair height (GameObject.cpp:1529).</summary>
    public static StandState SeatedState(GameObjectTemplate template) => StandState.SitLowChair + Height(template);
}
