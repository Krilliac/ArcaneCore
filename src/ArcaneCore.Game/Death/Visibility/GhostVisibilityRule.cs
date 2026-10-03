using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Death.Visibility;

/// <summary>
/// Who sees whom between the living and the dead (vmangos <c>Player::IsVisibleInGridForPlayer</c>, Player.cpp:18710-18743,
/// <c>Creature::IsVisibleInGridForPlayer</c>, Creature.cpp:2477-2509, and the spirit-service rule of
/// <c>Unit::IsVisibleForOrDetect</c>, Unit.cpp:6366-6369 with <c>IsInvisibleForAlive</c>, Unit.cpp:7703-7710). "Ghost" is the
/// DEAD state (a released spirit); a player still waiting at its body is in the CORPSE state and counts as living here.
/// <list type="bullet">
/// <item><description>A game master sees everything (a player target only when the master's security is at least its own).</description></item>
/// <item><description>Players: members of the same raid see each other at any distance; a living viewer sees living targets and
/// not ghosts, and a ghost sees friendly ghosts (same team here) and the living players within 100 yd of its own corpse.</description></item>
/// <item><description>Creatures: a living viewer cannot see a spirit healer or guide (npc flag 0x20 / 0x40), and sees every other
/// creature as before; a ghost sees the living creatures within (20 + 25) times the aggro rate of its corpse, and the spirit
/// services (vmangos: <c>IsVisibleForDead</c> and the static flag VISIBLE_TO_GHOSTS that the healer templates carry).</description></item>
/// </list>
/// Limits: creature template flags CREATURE_FLAG_EXTRA_INVISIBLE and the static flag VISIBLE_TO_GHOSTS of other creatures, and the
/// corpse decay timer that keeps dead creatures visible to the living, are not in the content, so a living viewer sees every creature
/// but the spirit services and a ghost sees only what is listed above. The ghost aura 9036 that vmangos puts on the healers' template
/// addon is not imported; the npc flags stand in for it. Other objects (game objects, corpses) are not restricted.
/// </summary>
/// <param name="groups">Raid membership (the spell system's group resolver); null means nobody is grouped.</param>
/// <param name="aggroRate">The creature aggro rate (<c>Rate.Creature.Aggro</c>, default 1) that scales the 45-yard range of the creature rule.</param>
public sealed class GhostVisibilityRule(Func<ISpellGroupResolver?>? groups = null, Func<float>? aggroRate = null) : IVisibilityRule
{
    /// <summary>vmangos: a ghost sees living players this far from its corpse (Nostalrius fix, "distance is fixed at 100m").</summary>
    public const float PlayerRange = 100f;

    /// <summary>vmangos: 20 (aggro distance for the same level) + 25 (the most that a lower level adds), scaled by the aggro rate.</summary>
    public const float CreatureRange = 20f + 25f;

    private const uint SpiritServiceFlags = (uint)(NpcFlags.SpiritHealer | NpcFlags.SpiritGuide);

    /// <inheritdoc />
    public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
    {
        if (ReferenceEquals(viewer, target) || target is not Unit unit)
        {
            return true;
        }

        return unit is Player player ? CanSeePlayer(viewer, player) : CanSeeCreature(viewer, unit);
    }

    private bool CanSeePlayer(Player viewer, Player target)
    {
        // Game masters see everything, ghosts included, unless the target is a higher GM.
        if (viewer.IsGameMaster && target.Session.Security <= viewer.Session.Security)
        {
            return true;
        }

        // "Ghost visibility by raid members."
        if (groups?.Invoke() is { } resolver && resolver.GetGroupMembers(target, raid: true).Contains(viewer.Guid))
        {
            return true;
        }

        bool targetGhost = target.Combat.DeathState == DeathState.Dead;
        if (viewer.Combat.DeathState != DeathState.Dead)
        {
            // A living player sees living players and players waiting at their body, not ghosts.
            return !targetGhost;
        }

        // A ghost sees the friendly ghosts, and the living near its own body.
        if (targetGhost && viewer.Map?.Combat.Hooks.IsFriendly(target, viewer) == true)
        {
            return true;
        }

        return target.IsAlive && WithinOfCorpse(viewer, target, PlayerRange);
    }

    private bool CanSeeCreature(Player viewer, Unit target)
    {
        if (viewer.IsGameMaster)
        {
            return true;
        }

        bool spiritService = (target.GetUInt32(UpdateFields.UnitNpcFlags) & SpiritServiceFlags) != 0;
        bool viewerLiving = viewer.IsAlive || viewer.Combat.DeathTimer > 0;
        if (viewerLiving)
        {
            // IsInvisibleForAlive differs between a living viewer and a living spirit service: the living cannot see it.
            return !(viewer.IsAlive && target.IsAlive && spiritService);
        }

        if (spiritService)
        {
            return true;
        }

        float range = CreatureRange * (aggroRate?.Invoke() ?? 1f);
        return target.IsAlive && WithinOfCorpse(viewer, target, range);
    }

    /// <summary>WorldObject::IsWithinDistInMap(corpse, dist): the viewer's body, on this map, within the distance (3D, less both radii).</summary>
    private static bool WithinOfCorpse(Player viewer, Unit target, float range)
    {
        if (viewer.Combat.Corpse is not { } corpse || !ReferenceEquals(corpse.Map, target.Map))
        {
            return false;
        }

        float dx = corpse.X - target.X;
        float dy = corpse.Y - target.Y;
        float dz = corpse.Z - target.Z;
        float max = range + corpse.BoundingRadius + target.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) < max * max;
    }
}
