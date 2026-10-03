using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Attack start, victim selection, the leash and combat movement (vmangos CreatureAI::AttackStart, Creature::SelectHostileTarget, IsOutOfThreatArea).</summary>
public sealed partial class CreatureMapSystem
{
    // --- combat --------------------------------------------------------------------------------

    /// <summary>
    /// vmangos CreatureAI::AttackStart: melee (unless the AI turned it off), zero threat so the
    /// target is on the threat list, both sides in combat, then on the first entry the combat
    /// start point, the AI's aggro hook and the assistance call; chase when combat movement is on.
    /// </summary>
    public bool AttackStart(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (!_creatures.ContainsKey(creature.Guid) || !creature.IsAlive || creature.IsEvading
            || !target.IsAlive || !ReferenceEquals(target.Map, Map) || !Map.Combat.Hooks.CanAttack(creature, target)
            || creature.ReactState == CreatureReactState.Passive) // vmangos CreatureAI::AttackStart (AI/CreatureAI.cpp:67-70)
        {
            return false;
        }

        bool melee = creature.AI?.MeleeEnabled ?? true;
        if (!ReferenceEquals(creature.Combat.Victim, target))
        {
            if (!Map.Combat.Attack(creature, target, melee))
            {
                return false;
            }

            SendAiReaction(creature);
        }

        creature.Combat.Threat.AddThreat(target, 0f);
        Map.Combat.SetInCombatState(creature, 0);
        Map.Combat.SetInCombatState(target, 0);
        if (!creature.HasAggroed)
        {
            creature.HasAggroed = true;
            creature.PacifiedMs = 0; // vmangos Creature::SetInCombatWith... enter combat clears the temporary pacify (Creature.cpp:3665)
            creature.CombatStart = new CreatureHome(creature.X, creature.Y, creature.Z, creature.Orientation);
            creature.AI?.OnAggro(target);
            if (!creature.IsAlive || creature.IsEvading)
            {
                return true; // the aggro script killed or reset it
            }

            CallAssistance(creature, target);
        }

        ApplyCombatMovement(creature);
        return true;
    }

    /// <summary>
    /// vmangos Creature::EnterCombatWithTarget (Objects/Creature.cpp:4078-4087), what proximity aggro calls: a creature without a victim
    /// starts attacking <paramref name="target"/> through its AI; one that already fights somebody else only adds the target to its
    /// threat list.
    /// </summary>
    public void EnterCombatWithTarget(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (creature.Combat.Victim is null)
        {
            if (creature.AI is { } ai)
            {
                ai.AttackStart(target);
            }
            else
            {
                AttackStart(creature, target);
            }
        }
        else if (!ReferenceEquals(creature.Combat.Victim, target) && target.IsAlive && ReferenceEquals(target.Map, Map))
        {
            creature.Combat.Threat.AddThreat(target, 0f);
            Map.Combat.SetInCombatState(target, 0);
        }
    }

    /// <summary>
    /// vmangos Creature::SendAIReaction(AI_REACTION_HOSTILE) from Unit::Attack (Objects/Unit.cpp:4535-4537): the creature tells everyone
    /// who sees it that it attacks, and the client plays its aggro sound. SMSG_AI_REACTION: guid, u32 reaction (gtker wow_messages
    /// smsg_ai_reaction.wowm).
    /// </summary>
    private void SendAiReaction(Creature creature)
    {
        if (_options.SendAiReaction)
        {
            Map.BroadcastToObservers(creature, WorldOpcode.SmsgAiReaction, CreatureAiReactionPackets.Build(creature.Guid, AiReaction.Hostile));
        }
    }

    /// <summary>
    /// vmangos Creature::SelectHostileTarget: the victim from the threat list (110 % in melee /
    /// 130 % at range to take aggro), skipping dead, unattackable and leashed targets. Nobody
    /// left means evade. Returns whether the creature still has a victim.
    /// </summary>
    public bool SelectHostileTarget(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid))
        {
            return false;
        }

        UnitCombat combat = creature.Combat;
        Unit? victim = combat.HasThreatList
            ? combat.Threat.SelectVictim(u => IsValidHostileTarget(creature, u), u => MapCombat.CanReachWithMeleeAutoAttack(creature, u))
            : null;
        if (victim is null)
        {
            if (combat.IsInCombat || combat.Victim is not null || combat.HasThreatList)
            {
                EnterEvadeMode(creature);
            }

            return false;
        }

        if (!ReferenceEquals(combat.Victim, victim) && Map.Combat.Attack(creature, victim, creature.AI?.MeleeEnabled ?? true))
        {
            SendAiReaction(creature);
        }

        ApplyCombatMovement(creature);
        return true;
    }

    /// <summary>
    /// vmangos Creature::IsOutOfThreatArea: a target farther than max(ThreatRadius, aggro radius)
    /// from where the fight began is out of reach (no leash on instanceable maps).
    /// </summary>
    public bool IsOutOfThreatArea(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (Map.Combat.Hooks.IsInstanceable(Map.MapId))
        {
            return false;
        }

        CreatureHome anchor = creature.CombatStart ?? creature.Home;
        float radius = Math.Max(_options.ThreatRadius, GetAttackDistance(creature, target));
        var delta = new Vector3(target.X - anchor.X, target.Y - anchor.Y, target.Z - anchor.Z);
        return delta.LengthSquared() > radius * radius;
    }

    /// <summary>
    /// Turn chasing on or off for the current victim (cmangos EventAI COMBAT_MOVEMENT): with it
    /// on the creature chases; off it stops where it is. Flight, point and home moves keep running.
    /// </summary>
    public void ApplyCombatMovement(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading)
        {
            return;
        }

        MovementGeneratorType current = creature.Motion.CurrentType;
        if (current is MovementGeneratorType.Fleeing or MovementGeneratorType.Point or MovementGeneratorType.Home)
        {
            return;
        }

        if (creature.Combat.Victim is { } victim && (creature.AI?.CombatMovement ?? true))
        {
            creature.Motion.MoveChase(victim);
        }
        else if (current == MovementGeneratorType.Chase)
        {
            creature.Motion.Remove(MovementGeneratorType.Chase);
            StopMoving(creature);
        }
    }

    /// <summary>Turn auto attack on or off against <paramref name="victim"/> (cmangos EventAI AUTO_ATTACK).</summary>
    public void SetMelee(Creature creature, Unit victim, bool melee)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(victim);
        if (melee)
        {
            Map.Combat.Attack(creature, victim, melee: true);
        }
        else if (creature.Combat.IsMeleeAttacking)
        {
            creature.Combat.IsMeleeAttacking = false;
            CombatPackets.SendToSet(creature, WorldOpcode.SmsgAttackstop, CombatPackets.AttackStop(creature.Guid, victim.Guid, false));
        }
    }

    private bool IsValidHostileTarget(Creature creature, Unit target)
        => target.IsAlive && ReferenceEquals(target.Map, Map)
            && Map.Combat.Hooks.CanAttack(creature, target)
            && !IsOutOfThreatArea(creature, target);
}
