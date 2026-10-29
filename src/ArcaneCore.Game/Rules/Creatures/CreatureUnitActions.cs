using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Rules.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

// The creature host's unit-state, movement, instance and script actions (see CreatureActions.cs for the contract).
// mangos-classic CreatureEventAI.cpp ProcessAction :665-1380.

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetFaction, "creature.set_faction",
    Description = "A faction template becomes the creature's faction until it respawns; 0 gives the template's faction back.")]
[RuleParam(1, "faction_template", RuleParamKind.NonNegative)]
public sealed class CreatureSetFactionAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int faction = context.Action.Param(1);
        runner.Me.FactionTemplate = faction != 0 ? (uint)faction : runner.Me.Template.Faction;
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionMorph, "creature.morph",
    Description = "Take a model of the creature template (entry), else the model id; both 0 gives the native model back.")]
[RuleParam(1, "entry", RuleParamKind.NonNegative, Description = "Creature entry whose model to take (0 none).")]
[RuleParam(2, "model_id", RuleParamKind.NonNegative, Description = "Display id (used when entry is 0).")]
public sealed class CreatureMorphAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        Creature me = runner.Me;
        int entry = context.Action.Param(1);
        int model = context.Action.Param(2);
        if (entry != 0)
        {
            if (runner.System?.Content.FindTemplate((uint)entry) is { } template)
            {
                me.DisplayId = Creature.ChooseDisplayId(template, new Random(runner.Context.Random(0, int.MaxValue - 1)));
            }
        }
        else if (model != 0)
        {
            me.DisplayId = (uint)model;
        }
        else
        {
            me.DisplayId = me.NativeDisplayId;
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionMount, "creature.mount",
    Description = "Mount the model of a creature template (entry) or a display id; both 0 dismounts.")]
[RuleParam(1, "entry", RuleParamKind.NonNegative, Description = "Creature entry whose model to mount (0 none).")]
[RuleParam(2, "model_id", RuleParamKind.NonNegative, Description = "Display id (used when entry is 0).")]
public sealed class CreatureMountAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        uint model = 0;
        int entry = context.Action.Param(1);
        if (entry != 0)
        {
            if (runner.System?.Content.FindTemplate((uint)entry) is { } template)
            {
                model = Creature.ChooseDisplayId(template, new Random(runner.Context.Random(0, int.MaxValue - 1)));
            }
        }
        else
        {
            model = (uint)Math.Max(0, context.Action.Param(2));
        }

        runner.Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, model);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetUnitField, "creature.set_unit_field", Description = "Set a unit update field of the target.")]
[RuleParam(1, "field", RuleParamKind.Int, Description = "Update field index (UNIT range).")]
[RuleParam(2, "value", RuleParamKind.Int)]
public sealed class CreatureSetUnitFieldAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int field = context.Action.Param(1);
        if (field < UpdateFields.ObjectEnd || field >= UpdateFields.UnitEnd)
        {
            return false;
        }

        target?.SetUInt32(field, unchecked((uint)context.Action.Param(2)));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionAddUnitFlags, "creature.add_unit_flags", Description = "Add flags to UNIT_FIELD_FLAGS of the target.")]
[RuleParam(1, "flags", RuleParamKind.Int)]
public sealed class CreatureAddUnitFlagsAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is not null)
        {
            target.UnitFlags |= (UnitFlags)unchecked((uint)context.Action.Param(1));
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRemoveUnitFlags, "creature.remove_unit_flags", Description = "Remove flags from UNIT_FIELD_FLAGS of the target.")]
[RuleParam(1, "flags", RuleParamKind.Int)]
public sealed class CreatureRemoveUnitFlagsAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is not null)
        {
            target.UnitFlags &= ~(UnitFlags)unchecked((uint)context.Action.Param(1));
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRemoveAuras, "creature.remove_auras", Description = "The target loses the auras of the spell.")]
[RuleParam(1, "spell_id", RuleParamKind.NonNegative)]
public sealed class CreatureRemoveAurasAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is not null)
        {
            runner.System?.AiServices.UnitSpells?.RemoveAuras(target, (uint)context.Action.Param(1));
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionRangedMovement, "creature.ranged_movement", Description = "Chase the victim at this distance from now on (the angle is not modelled).")]
[RuleParam(1, "distance", RuleParamKind.Int)]
[RuleParam(2, "angle", RuleParamKind.Int)]
public sealed class CreatureRangedMovementAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Context.Ai.AttackDistance = Math.Max(0, context.Action.Param(1));
        if (runner.Context.Victim is not null)
        {
            runner.System?.ApplyCombatMovement(runner.Me);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetInstanceData, "creature.set_instance_data",
    Description = "The instance script of the creature's map stores data under a field; fails without an instance script.")]
[RuleParam(1, "field", RuleParamKind.Int)]
[RuleParam(2, "data", RuleParamKind.Int)]
public sealed class CreatureSetInstanceDataAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.Context.InstanceData is not { } instance)
        {
            return false;
        }

        instance.SetData(unchecked((uint)context.Action.Param(1)), unchecked((uint)context.Action.Param(2)));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetInstanceData64, "creature.set_instance_data64",
    Description = "The instance script stores the target's guid under a field; fails without a target or an instance script.")]
[RuleParam(1, "field", RuleParamKind.Int)]
public sealed class CreatureSetInstanceData64Action : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (target is null || runner.Context.InstanceData is not { } instance)
        {
            return false;
        }

        instance.SetData64(unchecked((uint)context.Action.Param(1)), target.Guid.Value);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionUpdateTemplate, "creature.update_template",
    Description = "The creature becomes another template (health percent kept) until it respawns; its own entry fails.")]
[RuleParam(1, "entry", RuleParamKind.NonNegative)]
public sealed class CreatureUpdateTemplateAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
        => context.Action.Param(1) > 0 && runner.System is { } system && system.UpdateEntry(runner.Me, (uint)context.Action.Param(1));
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetSheath, "creature.set_sheath", Description = "Set the weapon sheath (0 none, 1 melee, 2 ranged).")]
[RuleParam(1, "sheath", RuleParamKind.Int)]
public sealed class CreatureSetSheathAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Me.SetByte(UpdateFields.UnitFieldBytes2, 0, (byte)Math.Clamp(context.Action.Param(1), 0, 2));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionDespawn, "creature.despawn", Description = "The creature dies without a kill and its corpse goes after the delay (0 at once).")]
[RuleParam(1, "delay_ms", RuleParamKind.NonNegative)]
public sealed class CreatureDespawnAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.ForcedDespawn(runner.Me, (uint)Math.Max(0, context.Action.Param(1)));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetDeathPrevention, "creature.set_death_prevention", Description = "While on, damage never takes the creature below 1 health.")]
[RuleParam(1, "enabled", RuleParamKind.Bool)]
public sealed class CreatureSetDeathPreventionAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Me.InvincibilityHpThreshold = context.Action.Param(1) != 0 ? 1u : 0u;
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetStandState, "creature.set_stand_state", Description = "Set the stand state.")]
[RuleParam(1, "stand_state", RuleParamKind.Int)]
public sealed class CreatureSetStandStateAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Me.StandState = (StandState)(byte)context.Action.Param(1);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionChangeMovement, "creature.change_movement",
    Description = "Change the movement: idle, a wander around where the creature stands, or a waypoint path; unsupported types fail.")]
[RuleParam(1, "movement_type", RuleParamKind.Int)]
[RuleParam(2, "wander_or_path", RuleParamKind.Int)]
[RuleParam(3, "flags", RuleParamKind.Int)]
public sealed class CreatureChangeMovementAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
        => runner.System is { } system && system.ChangeMovement(runner.Me, unchecked((uint)context.Action.Param(1)),
            unchecked((uint)context.Action.Param(2)), unchecked((uint)context.Action.Param(3)));
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetReactState, "creature.set_react_state", Description = "Set the react state (0 passive, 1 defensive, 2 aggressive); another value fails.")]
[RuleParam(1, "react_state", RuleParamKind.Int)]
public sealed class CreatureSetReactStateAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int state = context.Action.Param(1);
        if (state is < 0 or > 2)
        {
            return false;
        }

        runner.Me.ReactState = (CreatureReactState)(byte)state;
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionPauseWaypoints, "creature.pause_waypoints", Description = "Pause (1) or resume (0) the waypoint movement.")]
[RuleParam(1, "pause", RuleParamKind.Bool)]
public sealed class CreaturePauseWaypointsAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.PauseWaypoints(runner.Me, context.Action.Param(1) != 0);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionDespawnGuardians, "creature.despawn_guardians", Description = "The creature's guardians of the entry go (0: every guardian).")]
[RuleParam(1, "entry", RuleParamKind.NonNegative)]
public sealed class CreatureDespawnGuardiansAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.DespawnGuardians(runner.Me, (uint)Math.Max(0, context.Action.Param(1)));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetRangedMode, "creature.set_ranged_mode",
    Description = "Enter or leave ranged (caster) mode: type 0 off, 3 no-melee; the chase distance applies.")]
[RuleParam(1, "mode_type", RuleParamKind.Int)]
[RuleParam(2, "chase_distance", RuleParamKind.Int)]
public sealed class CreatureSetRangedModeAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
        => runner.Context.Ai.SetRangedMode(context.Action.Param(1) != 0, context.Action.Param(2), context.Action.Param(1));
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetWalk, "creature.set_walk",
    Description = "0 run and 1 walk set how scripted moves go; 2 and 3 (chase speeds) change nothing here.")]
[RuleParam(1, "walk_type", RuleParamKind.Int)]
public sealed class CreatureSetWalkAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        int type = context.Action.Param(1);
        if (type is 0 or 1)
        {
            runner.System?.SetScriptRun(runner.Me, run: type == 0);
        }

        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetFacing, "creature.set_facing",
    Description = "Face the target; with reset, face the reset orientation (the target is then not needed).")]
[RuleParam(1, "reset", RuleParamKind.Bool)]
public sealed class CreatureSetFacingAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        if (runner.System is not { } system)
        {
            return false;
        }

        Creature me = runner.Me;
        if (context.Action.Param(1) != 0)
        {
            CreatureHome reset = me.Motion.Default.GetResetPosition(me) ?? me.Home;
            system.SetFacingTo(me, reset.Orientation);
            return true;
        }

        if (target is null)
        {
            return false;
        }

        system.SetFacingTo(me, MathF.Atan2(target.Y - me.Y, target.X - me.X));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetImmobilized, "creature.set_immobilized", Description = "Root or free the creature; a combat-only root ends at the next reset.")]
[RuleParam(1, "apply", RuleParamKind.Bool)]
[RuleParam(2, "combat_only", RuleParamKind.Bool)]
public sealed class CreatureSetImmobilizedAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.System?.SetAiImmobilized(runner.Me, context.Action.Param(1) != 0, context.Action.Param(2) != 0);
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetImmunitySet, "creature.set_immunity_set", Description = "Switch the creature to a creature_immunities set of its entry.")]
[RuleParam(1, "set_id", RuleParamKind.NonNegative)]
public sealed class CreatureSetImmunitySetAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        runner.Me.UpdateImmunitySet((uint)context.Action.Param(1));
        return true;
    }
}

[RulePrimitive(RuleHostKind.Creature, CreatureRuleIds.ActionSetFollowMovement, "creature.set_follow_movement", Description = "0 holds a follow movement still, 1 lets it follow again.")]
[RuleParam(1, "state", RuleParamKind.Int)]
public sealed class CreatureSetFollowMovementAction : CreatureAction
{
    internal override bool Run(CreatureRuleRunner runner, in RuleActionContext context, Unit? target)
    {
        Creature me = runner.Me;
        me.FollowMovementDisabled = context.Action.Param(1) == 0;
        if (me.FollowMovementDisabled && me.IsMoving && me.Motion.CurrentType == MovementGeneratorType.Follow)
        {
            runner.System?.StopMoving(me);
        }

        return true;
    }
}
