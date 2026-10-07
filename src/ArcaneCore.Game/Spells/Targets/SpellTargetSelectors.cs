using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Targets;

/// <summary>
/// Implicit targets the built-in switch of <c>SpellSystem.SelectEffectTargets</c> does not cover and
/// that the shaman and paladin spell data needs: the four caster-relative summon locations 41-44, 47
/// and TARGET_UNIT_RAID_AND_CLASS 61. Values are the 1.12.1 TARGET_* ids of vmangos
/// <c>SpellDefines.h:96-116</c>; the shaman totem summons (67 castable ids) and the Greater Blessings
/// (8 ids) in classic-db use exactly these.
/// </summary>
public static class SpellTargetSelectors
{
    /// <summary>TARGET_LOCATION_CASTER_FRONT_RIGHT (vmangos adds 1.75 pi to the caster orientation).</summary>
    public const uint LocationCasterFrontRight = 41;

    /// <summary>TARGET_LOCATION_CASTER_BACK_RIGHT (+1.25 pi).</summary>
    public const uint LocationCasterBackRight = 42;

    /// <summary>TARGET_LOCATION_CASTER_BACK_LEFT (+0.75 pi).</summary>
    public const uint LocationCasterBackLeft = 43;

    /// <summary>TARGET_LOCATION_CASTER_FRONT_LEFT (+0.25 pi).</summary>
    public const uint LocationCasterFrontLeft = 44;

    /// <summary>TARGET_LOCATION_CASTER_FRONT (+0).</summary>
    public const uint LocationCasterFront = 47;

    /// <summary>TARGET_UNIT_RAID_AND_CLASS: the explicit target's same-class group members in range.</summary>
    public const uint UnitRaidAndClass = 61;

    /// <summary>
    /// vmangos Spell.cpp:2977-3022: unless the client already sent a destination, the destination is
    /// <c>radius</c> yards from the caster at orientation + the target's angle (radius 0 when the effect
    /// has no radius index, "we don't want to use max spell range here"); the unit list falls back to
    /// the caster. The collision services constrain the path, model hit and ground height.
    /// </summary>
    internal static List<(Unit Unit, float Multiplier)> SelectCasterRelativeLocation(SpellCast cast, SpellEffectInfo effect, float angleOffset)
    {
        if (!cast.Targets.HasDest)
        {
            Unit caster = cast.Caster;
            float radius = effect.Radius;
            float angle = caster.Orientation + angleOffset;
            cast.Targets.Mask |= SpellCastTargetFlags.DestLocation;
            var point = SummonPosition.Resolve(caster, radius, angle);
            cast.Targets.Dest = (point.X, point.Y, point.Z);
        }

        return [(cast.Caster, 1.0f)];
    }

    /// <summary>
    /// vmangos Spell.cpp:2940-2960: when the explicit target is a grouped player, every member of its
    /// group (all sub-groups) of the same class within the effect radius that the caster is not
    /// hostile to; otherwise just the explicit target.
    /// </summary>
    internal static List<(Unit Unit, float Multiplier)> SelectRaidAndClass(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? unitTarget)
    {
        if (unitTarget is null)
        {
            return [];
        }

        if (unitTarget is not Player targetPlayer || system.Groups.GetGroupMembers(targetPlayer, raid: true) is not { Count: > 1 } members)
        {
            return [(unitTarget, 1.0f)];
        }

        float radius = effect.Radius;
        var picked = new List<(Unit, float)>();
        foreach (ObjectGuid guid in members)
        {
            if (system.Units.Find(targetPlayer, guid) is not { } member || !ReferenceEquals(member.Map, targetPlayer.Map)
                || member.Class != targetPlayer.Class || system.Relations.IsHostile(cast.Caster, member))
            {
                continue;
            }

            // WorldObject::IsWithinDist with SizeFactor::BoundingRadius (Object.cpp:1738-1752).
            float dx = targetPlayer.X - member.X;
            float dy = targetPlayer.Y - member.Y;
            float dz = targetPlayer.Z - member.Z;
            float reach = radius + targetPlayer.BoundingRadius + member.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) < reach * reach)
            {
                picked.Add((member, 1.0f));
            }
        }

        return picked;
    }
}
