using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// EventAI actions that change the creature's (or a target's) unit state: faction, model, mount, fields, flags, sheath, stand state, react
// state and the template. mangos-classic src/game/AI/EventAI/CreatureEventAI.cpp ProcessAction (:665-1380); parameters CreatureEventAI.h:89-159.
// A creature's respawn re-initialises every one of these from its template (Creature.InitializeFields).

/// <summary>
/// ACTION_T_SET_FACTION (2): FactionId, Flags. A faction template id makes it the creature's faction until it respawns
/// (SetFactionTemporary); 0 gives the template's faction back (ClearTemporaryFaction, :732-740). classic-db z2815 uses no restore flags.
/// </summary>
public sealed class SetFactionAction : EventAiActionHandler
{
    public override byte ActionType => 2;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Me.FactionTemplate = action.Param1 != 0 ? (uint)action.Param1 : context.Me.Template.Faction;
        return true;
    }
}

/// <summary>
/// ACTION_T_MORPH_TO_ENTRY_OR_MODEL (3): CreatureId, ModelId. A creature id takes a model of that template (ChooseDisplayId), else the model
/// id is used; both 0 gives the native model back (DeMorph, :741-761).
/// </summary>
public sealed class MorphAction : EventAiActionHandler
{
    public override byte ActionType => 3;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        Creature me = context.Me;
        if (action.Param1 != 0)
        {
            if (context.System?.Content.FindTemplate((uint)action.Param1) is { } template)
            {
                me.DisplayId = Creature.ChooseDisplayId(template, new Random(context.Random(0, int.MaxValue - 1)));
            }
        }
        else if (action.Param2 != 0)
        {
            me.DisplayId = (uint)action.Param2;
        }
        else
        {
            me.DisplayId = me.NativeDisplayId;
        }

        return true;
    }
}

/// <summary>
/// ACTION_T_MOUNT_TO_ENTRY_OR_MODEL (43): CreatureId, ModelId. Mounts the model of the creature template (MountEntry) or the model id
/// (Mount); both 0 dismounts (Unmount, :1124-1141): UNIT_FIELD_MOUNTDISPLAYID.
/// </summary>
public sealed class MountAction : EventAiActionHandler
{
    public override byte ActionType => 43;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        uint model = 0;
        if (action.Param1 != 0)
        {
            if (context.System?.Content.FindTemplate((uint)action.Param1) is { } template)
            {
                model = Creature.ChooseDisplayId(template, new Random(context.Random(0, int.MaxValue - 1)));
            }
        }
        else
        {
            model = (uint)Math.Max(0, action.Param2);
        }

        context.Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, model);
        return true;
    }
}

/// <summary>
/// ACTION_T_SET_UNIT_FIELD (17): FieldNumber, Value, Target. Sets a unit update field of the target; a field below UNIT start or past
/// UNIT_END fails (:875-889).
/// </summary>
public sealed class SetUnitFieldAction : EventAiActionHandler
{
    public override byte ActionType => 17;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param1 < UpdateFields.ObjectEnd || action.Param1 >= UpdateFields.UnitEnd)
        {
            return false;
        }

        if (context.SelectTarget(action.Param3, invocation, out _) is { } target)
        {
            target.SetUInt32(action.Param1, unchecked((uint)action.Param2));
        }

        return true;
    }
}

/// <summary>ACTION_T_SET_UNIT_FLAG (18): Flags, Target; the flags are added to UNIT_FIELD_FLAGS of the target (:890-895).</summary>
public sealed class SetUnitFlagAction : EventAiActionHandler
{
    public override byte ActionType => 18;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param2, invocation, out _) is { } target)
        {
            target.UnitFlags |= (UnitFlags)unchecked((uint)action.Param1);
        }

        return true;
    }
}

/// <summary>ACTION_T_REMOVE_UNIT_FLAG (19): Flags, Target; the flags are removed from UNIT_FIELD_FLAGS of the target (:896-901).</summary>
public sealed class RemoveUnitFlagAction : EventAiActionHandler
{
    public override byte ActionType => 19;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (context.SelectTarget(action.Param2, invocation, out _) is { } target)
        {
            target.UnitFlags &= ~(UnitFlags)unchecked((uint)action.Param1);
        }

        return true;
    }
}

/// <summary>ACTION_T_SET_SHEATH (40): Sheath (0 none, 1 melee, 2 ranged): UNIT_FIELD_BYTES_2 byte 0 (SetSheath, :1109-1113).</summary>
public sealed class SetSheathAction : EventAiActionHandler
{
    public override byte ActionType => 40;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Me.SetByte(UpdateFields.UnitFieldBytes2, 0, (byte)Math.Clamp(action.Param1, 0, 2));
        return true;
    }
}

/// <summary>ACTION_T_SET_STAND_STATE (47): StandState (SetStandState, :1159-1163).</summary>
public sealed class SetStandStateAction : EventAiActionHandler
{
    public override byte ActionType => 47;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Me.StandState = (StandState)(byte)action.Param1;
        return true;
    }
}

/// <summary>
/// ACTION_T_SET_REACT_STATE (50): ReactState (0 passive, 1 defensive, 2 aggressive; :1208-1213). A passive creature does not aggro on sight
/// and does not fight back; the respawn takes the template's state back.
/// </summary>
public sealed class SetReactStateAction : EventAiActionHandler
{
    public override byte ActionType => 50;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        if (action.Param1 is < 0 or > 2)
        {
            return false;
        }

        context.Me.ReactState = (CreatureReactState)(byte)action.Param1;
        return true;
    }
}

/// <summary>
/// ACTION_T_UPDATE_TEMPLATE (36): Entry. The creature becomes that template (UpdateEntry, keeping its health percent) until it respawns; its own
/// entry fails (:1078-1086).
/// </summary>
public sealed class UpdateTemplateAction : EventAiActionHandler
{
    public override byte ActionType => 36;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
        => action.Param1 > 0 && context.System is { } system && system.UpdateEntry(context.Me, (uint)action.Param1);
}

/// <summary>
/// ACTION_T_SET_DEATH_PREVENTION (42): State. While on, damage never takes the creature below 1 health (cmangos UnitAI::DamageTaken with
/// m_deathPrevention: damage = health - 1; here the unit's invincibility threshold of 1, the same clamp vmangos uses for
/// SET_INVINCIBILITY_HP_LEVEL).
/// </summary>
public sealed class SetDeathPreventionAction : EventAiActionHandler
{
    public override byte ActionType => 42;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        context.Me.InvincibilityHpThreshold = action.Param1 != 0 ? 1u : 0u;
        return true;
    }
}
