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
    /// vmangos CreatureAI::TriggerAlertDirect (AI/CreatureAI.cpp:376-385): tell everyone who sees the creature (SMSG_AI_REACTION, alert),
    /// stop it, turn it to <paramref name="who"/> (a facing spline) and hold it there for <see cref="StealthAlertDistractMs"/> (MoveDistract),
    /// and start the cooldown.
    /// </summary>
    public void TriggerAlert(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        SendAiReaction(creature, AiReaction.Alert);
        StopMoving(creature);
        SetFacingTo(creature, MathF.Atan2(who.Y - creature.Y, who.X - creature.X));
        creature.Motion.MoveDistract(StealthAlertDistractMs);
        creature.LastAlertAtMs = _clockMs;
    }

    /// <summary>vmangos TriggerAlertDirect: <c>MoveDistract(5 * IN_MILLISECONDS)</c>.</summary>
    public const uint StealthAlertDistractMs = 5000;

    /// <summary>
    /// vmangos Spell::EffectDistract (Spells/SpellEffects.cpp:2632-2649), the creature half: a creature in combat, or one that cannot react
    /// (stunned, confused or fleeing; UNIT_STATE_CAN_NOT_REACT), is not distracted; otherwise it turns to <paramref name="angle"/> and stands
    /// for <paramref name="durationMs"/> (MoveDistract), then turns back to its spawn facing. Returns whether it was distracted.
    /// </summary>
    public bool Distract(Creature creature, float angle, uint durationMs)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || !_creatures.ContainsKey(creature.Guid) || creature.Combat.IsInCombat || (creature.UnitFlags & LostControl) != 0)
        {
            return false;
        }

        SetFacingTo(creature, angle);
        creature.Motion.MoveDistract(durationMs);
        return true;
    }

    /// <summary>
    /// vmangos Unit::SetFacingTo (Objects/Unit.cpp:2785-2794): the orientation, and a facing spline with no path (SMSG_MONSTER_MOVE, the
    /// angle move type) so the observers see the turn. The spline keeps the creature's walk or run mode.
    /// </summary>
    public void SetFacingTo(Creature creature, float angle)
    {
        ArgumentNullException.ThrowIfNull(creature);
        angle = Creature.NormalizeOrientation(angle);
        creature.Orientation = angle;
        MovePath(creature, [new System.Numerics.Vector3(creature.X, creature.Y, creature.Z)], !creature.Movement.HasFlag(MovementFlags.WalkMode),
            SplineFacing.ToAngle(angle));
    }
}
