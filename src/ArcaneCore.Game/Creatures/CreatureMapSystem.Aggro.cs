using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Proximity aggro: the aggro radius and the on-sight attack rules (vmangos Creature::GetAttackDistance, AggressorAI::MoveInLineOfSight).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>vmangos CREATURE_Z_ATTACK_RANGE: no aggro on a unit more than 3 yd above or below.</summary>
    public const float MaxAggroZDistance = 3.0f;

    /// <summary>vmangos: the aggro radius never exceeds base + 25 levels.</summary>
    public const int MaxAggroLevelDifference = 25;

    /// <summary>vmangos base aggro radius against a unit of the same level (yd).</summary>
    public const float BaseAggroRadius = 20.0f;

    /// <summary>vmangos minimum aggro radius (yd).</summary>
    public const float MinAggroRadius = 5.0f;

    // --- aggro ---------------------------------------------------------------------------------

    /// <summary>
    /// vmangos Creature::GetAttackDistance: 20 yd at equal level, one yard less per level the
    /// target is above the creature and one more per level below (at most 25 levels counted
    /// below), never under 5 yd, times <see cref="CreatureOptions.AggroRate"/>.
    /// </summary>
    public float GetAttackDistance(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        float rate = _options.AggroRate;
        if (rate <= 0)
        {
            return 0;
        }

        int levelDifference = Math.Max(target.Level - creature.Level, -MaxAggroLevelDifference);
        float distance = Math.Max(BaseAggroRadius - levelDifference, MinAggroRadius);
        return distance * rate;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> attacks <paramref name="who"/> on sight (vmangos
    /// AggressorAI::MoveInLineOfSight → Creature::CanInitiateAttack + IsHostileTo + range checks):
    /// alive, idle, not evading, not civilian, no NO_AGGRO extra flag; the target an attackable
    /// living player (not GM) the hostility seam calls an enemy; within the aggro radius (3D,
    /// both bounding radii) and 3 yd vertically; in line of sight.
    /// </summary>
    public bool CanAggroOnSight(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Player player || player.IsGameMaster || !player.IsAlive || !ReferenceEquals(player.Map, Map))
        {
            return false; // creature-versus-creature aggro is not modelled (docs/areas/creature-ai.md)
        }

        if (!creature.IsAlive || creature.IsEvading || creature.Combat.Victim is not null || !_creatures.ContainsKey(creature.Guid)
            || creature.Template.Civilian || (creature.Template.ExtraFlags & Creature.ExtraFlagNoAggro) != 0
            || (creature.UnitFlags & (UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer | UnitFlags.Pacified | UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return false;
        }

        float radii = creature.BoundingRadius + player.BoundingRadius;
        float dz = MathF.Abs(creature.Z - player.Z) - radii;
        if (dz > MaxAggroZDistance)
        {
            return false;
        }

        float range = GetAttackDistance(creature, player) + radii;
        if (DistanceSquared(creature, player) > range * range)
        {
            return false;
        }

        return Map.Combat.Hooks.CanAttack(creature, player)
            && _ai.Hostility.IsHostile(creature, player)
            && InLineOfSight(creature, player);
    }
}
