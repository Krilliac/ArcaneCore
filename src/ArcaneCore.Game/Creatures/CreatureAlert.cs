using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>The stealth alert (vmangos CreatureAI::CanTriggerAlert / TriggerAlertDirect, AI/CreatureAI.cpp:349-385).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// vmangos CreatureAI::CanTriggerAlert: the creature is alive and not in combat, not stunned, confused or fleeing, not a civilian, not
    /// passive, and <paramref name="who"/> is a valid hostile target in line of sight; a creature alerts at most once per
    /// <see cref="CreatureOptions.StealthAlertCooldownMs"/> (10 s). Off with <see cref="CreatureOptions.StealthAlertEnabled"/>.
    /// </summary>
    public bool CanTriggerAlert(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if (!_options.StealthAlertEnabled || !creature.IsAlive || !_creatures.ContainsKey(creature.Guid) || creature.Combat.IsInCombat
            || (creature.UnitFlags & LostControl) != 0)
        {
            return false;
        }

        if (creature.Template.Civilian || (creature.Template.Behaviour & Kernel.WorldData.Creatures.CreatureBehaviourFlags.Civilian) != 0
            || creature.ReactState == CreatureReactState.Passive || !who.IsAlive || !Map.Combat.Hooks.CanAttack(creature, who)
            || !_ai.Hostility.IsHostile(creature, who))
        {
            return false;
        }

        if (creature.LastAlertAtMs is { } last && _clockMs - last < _options.StealthAlertCooldownMs)
        {
            return false;
        }

        return InLineOfSight(creature, who);
    }

    /// <summary>
    /// vmangos CreatureAI::TriggerAlertDirect: tell everyone who sees the creature (SMSG_AI_REACTION, alert), stop it and turn it to
    /// <paramref name="who"/>, and start the cooldown. Limits: vmangos then holds the creature still for 5 s (MoveDistract), which needs a
    /// movement generator this server does not have, so the creature simply carries on with its movement; the turn is the orientation field
    /// (no facing spline packet is sent, the client sees it with the next movement packet).
    /// </summary>
    public void TriggerAlert(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        SendAiReaction(creature, AiReaction.Alert);
        StopMoving(creature);
        creature.Orientation = MathF.Atan2(who.Y - creature.Y, who.X - creature.X);
        creature.LastAlertAtMs = _clockMs;
    }
}
