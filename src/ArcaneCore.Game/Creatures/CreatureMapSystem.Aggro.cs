using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stealth;
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
    /// unit in this map (a player that is not a GM, or, with <c>Creatures:CreatureAggroOnCreatures</c>, another creature that is not
    /// evading: mangos AggressorAI::MoveInLineOfSight takes any unit, Object/AggressorAI.cpp:70-95); proximity aggro is allowed for it;
    /// the creature can initiate an attack on a unit that is not already its victim; the vertical distance (bounding radii taken
    /// off, flyers exempt) is within 3 yd; the plain 3D distance is inside the aggro radius (no bounding radii, strictly less); the
    /// hostility seam calls it an enemy (the reputation lane's answer for players, the faction templates between creatures); and it
    /// is in line of sight.
    /// </summary>
    public bool CanAggroOnSight(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        return IsAggroTarget(creature, who)
            && IsProximityAggroAllowedFor(creature, who)
            && CanInitiateAttack(creature)
            && IsInAggroReach(creature, who)
            && Map.Combat.Hooks.CanAttack(creature, who)
            && _ai.Hostility.IsHostile(creature, who)
            && CanSeeForAggro(creature, who);
    }

    /// <summary>
    /// mangos GuardAI::MoveInLineOfSight (Object/GuardAI.cpp:65-85): a guard without a victim attacks a unit in its aggro radius that
    /// is hostile to players as such (a mob in town), that its own hostility calls an enemy (an opposing-faction or Hated player, a
    /// contested-PvP player for a contested guard), or, with <c>Creatures:GuardsDefendFriendlies</c>, that is fighting a creature the
    /// guard is friendly to (the clause both references keep commented out). The common gates of <see cref="CanAggroOnSight"/> apply
    /// (alive, in control, can initiate, 3 yd vertical limit, attackable, line of sight); the reference guard does not add threat to a
    /// second target in dungeons, so a guard with a victim ignores everyone else.
    /// </summary>
    public bool CanGuardAggroOnSight(Creature guard, Unit who)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(who);
        if (guard.Combat.Victim is not null || !IsAggroTarget(guard, who) || !IsProximityAggroAllowedFor(guard, who)
            || !CanInitiateAttack(guard) || !IsInAggroReach(guard, who) || !Map.Combat.Hooks.CanAttack(guard, who))
        {
            return false;
        }

        bool enemy = _ai.Hostility.IsHostileToPlayers(who)
            || _ai.Hostility.IsHostile(guard, who)
            || (_options.GuardsDefendFriendlies && who.Combat.Victim is Creature friend && !ReferenceEquals(friend, guard)
                && friend.IsAlive && ReferenceEquals(friend.Map, Map) && _ai.Hostility.IsFriendly(guard, friend));
        return enemy && CanSeeForAggro(guard, who);
    }

    /// <summary>
    /// The unit half of the on-sight gates: a living player that is not a GM, or a living creature of this map that is not the
    /// creature itself (mangos Unit::IsTargetableForAttack; the evade and flag checks are the combat hooks'), and the creature's own
    /// state (alive, in this system, not evading, not out of control, not already fighting the unit).
    /// </summary>
    private bool IsAggroTarget(Creature creature, Unit who)
    {
        switch (who)
        {
            case Player player when player.IsGameMaster:
                return false;
            case Player:
                break;
            case Creature other when !_options.CreatureAggroOnCreatures || ReferenceEquals(other, creature) || other.IsInEvadeMode:
                return false;
            case Creature:
                break;
            default:
                return false;
        }

        return who.IsAlive && ReferenceEquals(who.Map, Map)
            && creature.IsAlive && !creature.IsEvading && _creatures.ContainsKey(creature.Guid) && (creature.UnitFlags & LostControl) == 0
            && !ReferenceEquals(creature.Combat.Victim, who);
    }

    /// <summary>The vertical limit (bounding radii taken off, INHABIT_AIR exempt) and the aggro radius (plain distance, strictly inside).</summary>
    private bool IsInAggroReach(Creature creature, Unit who)
    {
        float radii = creature.BoundingRadius + who.BoundingRadius;
        bool canFly = (creature.Template.InhabitType & 0x04) != 0; // INHABIT_AIR
        float dz = MathF.Max(0f, MathF.Abs(creature.Z - who.Z) - radii);
        if (!canFly && dz > CreatureAggro.MaxZDistance)
        {
            return false;
        }

        float range = GetAttackDistance(creature, who) + (_options.AggroUsesBoundingRadius ? radii : 0f);
        return DistanceSquared(creature, who) < range * range;
    }

    /// <summary>Stealth and invisibility (players only: creature stealth is not modelled) and line of sight.</summary>
    private bool CanSeeForAggro(Creature creature, Unit who)
        => (who is not Player player || StealthServices.Find(Map) is not ICreatureVisibility visibility || visibility.CanCreatureSee(creature, player, out _))
            && InLineOfSight(creature, who);
}
