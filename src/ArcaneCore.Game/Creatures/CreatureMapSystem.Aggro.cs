using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>Proximity aggro: the aggro radius and the on-sight attack rules (vmangos Creature::GetAttackDistance, BasicAI::MoveInLineOfSight).</summary>
public sealed partial class CreatureMapSystem
{
    private const UnitFlags LostControl = UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing;

    /// <summary>
    /// vmangos Creature::GetAttackDistance (Objects/Creature.cpp:2193-2240): the template's detection range (18 by default)
    /// minus the level difference (at most 25 levels counted below), never under <c>min(detection, 5)</c>, times
    /// <see cref="CreatureOptions.AggroRate"/>. See <see cref="CreatureAggro.GetAttackDistance"/>.
    /// </summary>
    public float GetAttackDistance(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        return CreatureAggro.GetAttackDistance(creature.Template.Detection, creature.Level, target.Level, _options.AggroRate);
    }

    /// <summary>
    /// vmangos Creature::CanInitiateAttack (Objects/Creature.cpp:2650-2671): alive, not stunned, not spawning or unselectable,
    /// react state aggressive, not temporarily pacified. (IsNeutralToAll is covered by the hostility seam answering "not
    /// hostile" for a faction nobody is an enemy of.)
    /// </summary>
    public bool CanInitiateAttack(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.IsAlive
            && (creature.UnitFlags & (UnitFlags.Stunned | UnitFlags.Spawning | UnitFlags.NotSelectable)) == 0
            && creature.ReactState == CreatureReactState.Aggressive
            && !creature.IsTempPacified;
    }

    /// <summary>
    /// vmangos BasicAI::IsProximityAggroAllowedFor (AI/BasicAI.cpp:30-47): a creature that only attacks PvP-flagged players
    /// skips the others; one with a victim only picks up further targets in instanceable maps or with NO_LEASH_EVADE.
    /// </summary>
    public bool IsProximityAggroAllowedFor(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if ((creature.Template.Behaviour & CreatureBehaviourFlags.OnlyAttackPvpEnabling) != 0
            && target is Player && (target.UnitFlags & UnitFlags.Pvp) == 0)
        {
            return false;
        }

        Unit? victim = creature.Combat.Victim;
        return victim is null
            || ReferenceEquals(victim, target)
            || Map.Combat.Hooks.IsInstanceable(Map.MapId)
            || (creature.Template.Behaviour & CreatureBehaviourFlags.NoLeashEvade) != 0;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> attacks <paramref name="who"/> on sight (vmangos CallAIMoveLOS + BasicAI::MoveInLineOfSight,
    /// AI/BasicAI.cpp:49-77): the creature is alive, not evading and not out of control; the target is an attackable living
    /// player (not GM) in this map; proximity aggro is allowed for it; the creature can initiate an attack on a unit that is
    /// not already its victim; the vertical distance (bounding radii taken off, flyers exempt) is within 3 yd; the plain 3D
    /// distance is inside the aggro radius (no bounding radii, strictly less); the hostility seam calls it an enemy; and it is in
    /// line of sight. Creature-versus-creature aggro is not modelled (docs/areas/creature-ai.md).
    /// </summary>
    public bool CanAggroOnSight(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Player player || player.IsGameMaster || !player.IsAlive || !ReferenceEquals(player.Map, Map))
        {
            return false;
        }

        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid) || (creature.UnitFlags & LostControl) != 0
            || !IsProximityAggroAllowedFor(creature, player)
            || ReferenceEquals(creature.Combat.Victim, player) || !CanInitiateAttack(creature))
        {
            return false;
        }

        float radii = creature.BoundingRadius + player.BoundingRadius;
        bool canFly = (creature.Template.InhabitType & 0x04) != 0; // INHABIT_AIR
        float dz = MathF.Max(0f, MathF.Abs(creature.Z - player.Z) - radii);
        if (!canFly && dz > CreatureAggro.MaxZDistance)
        {
            return false;
        }

        float range = GetAttackDistance(creature, player) + (_options.AggroUsesBoundingRadius ? radii : 0f);
        if (DistanceSquared(creature, player) >= range * range)
        {
            return false;
        }

        return Map.Combat.Hooks.CanAttack(creature, player)
            && _ai.Hostility.IsHostile(creature, player)
            && InLineOfSight(creature, player);
    }
}
