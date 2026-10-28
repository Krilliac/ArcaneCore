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

    /// <summary>Spell 9036 (Ghost: SPELL_AURA_GHOST), cast on spawn by ghost NPCs such as Gaeriyan (9299) and Franclorn Forgewright (8888).</summary>
    public const uint SpellGhostAura = 9036;

    /// <summary>Ghost NPCs whose EventAI casts spell 9036 (Ghost) on spawn (vmangos IsInvisibleForAlive).</summary>
    public const uint GaeriyanEntry = 9299, FranclornForgewrightEntry = 8888, FranclornSpiritEntry = 9542;

    /// <summary>
    /// Whether <paramref name="target"/> is invisible to the living and visible to ghosts (vmangos <c>Unit::IsInvisibleForAlive</c>,
    /// Unit.cpp:7703-7710, and <c>Creature::IsVisibleInGridForPlayer</c>, Creature.cpp:2495): spirit healers and guides, units carrying
    /// <c>UNIT_VIS_FLAGS_GHOST</c> (<see cref="GhostAuras.VisFlagGhost"/>) or the Ghost aura (<see cref="SpellGhostAura"/>), and the
    /// ghost NPCs Gaeriyan (9299), Franclorn Forgewright (8888) and Franclorn's Spirit (9542).
    /// </summary>
    public static bool IsGhostCreature(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if ((target.GetUInt32(UpdateFields.UnitNpcFlags) & SpiritServiceFlags) != 0)
        {
            return true;
        }

        if ((target.GetByte(UpdateFields.UnitFieldBytes1, GhostAuras.VisFlagByte) & GhostAuras.VisFlagGhost) != 0)
        {
            return true;
        }

        uint entry = target.GetUInt32(UpdateFields.ObjectFieldEntry);
        if (entry is GaeriyanEntry or FranclornForgewrightEntry or FranclornSpiritEntry)
        {
            return true;
        }

        return target.Values.Slice(UpdateFields.UnitFieldAura, 48).Contains(SpellGhostAura);
    }

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

        bool targetGhost = target.Combat.DeathState == DeathState.Dead;
        bool viewerLiving = viewer.Combat.DeathState != DeathState.Dead;

        // The living see the living whatever the raid says: answered before the raid lookup, which builds the target's member list and is
        // otherwise paid by nearly every pair of a visibility pass (the answer is the one the steps below give).
        if (viewerLiving && !targetGhost)
        {
            return true;
        }

        // "Ghost visibility by raid members."
        if (groups?.Invoke() is { } resolver && resolver.IsMember(target, viewer.Guid, raid: true))
        {
            return true;
        }

        if (viewerLiving)
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

        bool viewerLiving = viewer.Combat.DeathState != DeathState.Dead;
        if (viewerLiving)
        {
            // IsInvisibleForAlive differs between a living viewer and a living ghost/spirit-service creature: the living cannot see it.
            return !(viewer.IsAlive && target.IsAlive && IsGhostCreature(target));
        }

        if ((target.GetUInt32(UpdateFields.UnitNpcFlags) & (uint)NpcFlags.SpiritGuide) != 0)
        {
            return IsSpiritGuideFriendlyTo(viewer, target);
        }

        if (IsGhostCreature(target))
        {
            return true;
        }

        float range = CreatureRange * (aggroRate?.Invoke() ?? 1f);
        return target.IsAlive && WithinOfCorpse(viewer, target, range);
    }

    public static bool IsSpiritGuideFriendlyTo(Player viewer, Unit target)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(target);
        if ((viewer.Map ?? target.Map)?.Combat?.Hooks is { } hooks && !ReferenceEquals(hooks, CombatHooks.Default))
        {
            if (hooks is FactionCombatHooks fc)
            {
                if (fc.Factions.Find(target.FactionTemplate) is not null && fc.Factions.Find(viewer.FactionTemplate) is not null)
                {
                    return hooks.IsFriendly(target, viewer) && !hooks.IsHostileTo(target, viewer);
                }
            }
            else if (hooks is Reputation.ReputationCombatHooks { Fallback: FactionCombatHooks repFc })
            {
                if (repFc.Factions.Find(target.FactionTemplate) is not null && repFc.Factions.Find(viewer.FactionTemplate) is not null)
                {
                    return repFc.IsFriendly(target, viewer) && !hooks.IsHostileTo(target, viewer);
                }
            }
            else
            {
                return hooks.IsFriendly(target, viewer) && !hooks.IsHostileTo(target, viewer);
            }
        }

        uint entry = target.GetUInt32(UpdateFields.ObjectFieldEntry);
        if (entry is 13117 or 13177) return viewer.Team == Team.Horde;
        if (entry is 13116 or 13176) return viewer.Team == Team.Alliance;
        uint faction = target.FactionTemplate;
        if (faction == 0 || faction == viewer.FactionTemplate) return true;
        if (faction is 2 or 5 or 6 or 29 or 67 or 83 or 85 or 104 or 105 or 106 or 116 or 714 or 1214 or 1315 or 1515)
            return viewer.Team == Team.Horde;
        if (faction is 1 or 3 or 4 or 8 or 11 or 12 or 35 or 55 or 56 or 57 or 64 or 80 or 84 or 115 or 715 or 1216 or 1314 or 1514)
            return viewer.Team == Team.Alliance;
        return true;
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
