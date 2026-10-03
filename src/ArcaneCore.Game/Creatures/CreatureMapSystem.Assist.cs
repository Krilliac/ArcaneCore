using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Call for assistance and flee for assistance (vmangos Creature::CallAssistance, cmangos DoFleeToGetAssistance).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The point id a creature fleeing for assistance runs to its helper with.</summary>
    public const uint FleeForAssistancePointId = 0xFFFF_F1EE;

    private const UnitFlags NotAssistable = UnitFlags.NonAttackable2 | UnitFlags.NotAttackable1 | UnitFlags.NotSelectable;

    private readonly List<PendingAssist> _pendingAssists = [];

    /// <summary>Delayed assistance calls waiting for their delay (helper, enemy, due time on <see cref="ClockMs"/>).</summary>
    public int PendingAssistCount => _pendingAssists.Count;

    // --- assistance --------------------------------------------------------------------------

    /// <summary>
    /// vmangos Creature::CallAssistance: once per fight, every idle same-faction creature within
    /// <see cref="CreatureOptions.AssistanceRadius"/> that can see the caller joins after
    /// <see cref="CreatureOptions.AssistanceDelayMs"/> (helpers do not call further help).
    /// </summary>
    internal void CallAssistance(Creature creature, Unit enemy)
    {
        if (creature.CalledAssistance || _options.AssistanceRadius <= 0 || creature.Template.Civilian)
        {
            return;
        }

        creature.CalledAssistance = true;
        foreach (Creature helper in _creatures.Values)
        {
            if (CanAssist(helper, creature, enemy, _options.AssistanceRadius))
            {
                _pendingAssists.Add(new PendingAssist(helper, enemy, creature, _clockMs + _options.AssistanceDelayMs));
            }
        }
    }

    /// <summary>cmangos EventAI CALL_FOR_HELP: idle same-faction creatures within <paramref name="radius"/> join at once.</summary>
    public int CallForHelp(Creature creature, float radius)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Combat.Victim is not { } enemy || radius <= 0)
        {
            return 0;
        }

        int count = 0;
        foreach (Creature helper in _creatures.Values.ToArray())
        {
            if (CanAssist(helper, creature, enemy, radius))
            {
                helper.CalledAssistance = true;
                if (AttackStart(helper, enemy))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// vmangos Creature::CanAssistTo plus the assistance search filters: another living, idle,
    /// non-civilian creature with an AI that fights, attackable flags, within
    /// <paramref name="radius"/> of the caller (3D, both radii), allowed by the hostility seam
    /// (same faction by default), able to attack the enemy and in line of sight of the caller.
    /// </summary>
    public bool CanAssist(Creature helper, Creature caller, Unit enemy, float radius)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(enemy);
        float range = radius + helper.BoundingRadius + caller.BoundingRadius;
        return CanAssistNow(helper, caller, enemy)
            && DistanceSquared(helper, caller) <= range * range
            && InLineOfSight(helper, caller);
    }

    // CanAssistTo is rechecked when the delayed call executes. Radius and sight belong to
    // the initial neighbour search: the caller may have chased away during the delay.
    private bool CanAssistNow(Creature helper, Creature caller, Unit enemy)
    {
        if (ReferenceEquals(helper, caller) || !helper.IsAlive || helper.DeathState != CreatureDeathState.Alive
            || helper.IsEvading || helper.Combat.Victim is not null || helper.Combat.IsInCombat
            || helper.AI is null or NullCreatureAI || helper.Template.Civilian
            || (helper.UnitFlags & NotAssistable) != 0
            || (enemy is Player && (helper.UnitFlags & UnitFlags.ImmuneToPlayer) != 0))
        {
            return false;
        }

        return _ai.Hostility.CanAssist(helper, caller)
            && Map.Combat.Hooks.CanAttack(helper, enemy);
    }

    /// <summary>
    /// cmangos Creature::DoFleeToGetAssistance: run to the nearest creature that can help (within
    /// <see cref="CreatureOptions.FleeAssistanceRadius"/>) and call for help on arrival; with
    /// nobody to find, flee from the victim for <see cref="CreatureOptions.FleeDelayMs"/>.
    /// </summary>
    public void FleeForAssistance(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || creature.Combat.Victim is not { } enemy)
        {
            return;
        }

        Creature? nearest = null;
        float best = float.MaxValue;
        foreach (Creature helper in _creatures.Values)
        {
            if (CanAssist(helper, creature, enemy, _options.FleeAssistanceRadius))
            {
                float d = DistanceSquared(helper, creature);
                if (d < best)
                {
                    best = d;
                    nearest = helper;
                }
            }
        }

        if (nearest is not null)
        {
            creature.Motion.MovePoint(FleeForAssistancePointId, nearest.X, nearest.Y, nearest.Z, run: true);
        }
        else
        {
            creature.Motion.MoveFleeing(enemy, _options.FleeDelayMs);
        }
    }

    private readonly record struct PendingAssist(Creature Helper, Unit Enemy, Creature Caller, long DueMs);
}
