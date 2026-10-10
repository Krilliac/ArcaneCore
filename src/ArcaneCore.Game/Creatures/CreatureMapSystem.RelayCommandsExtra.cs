using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The relay / DB-script commands z2815's <c>dbscripts_on_relay</c> uses that the main switch did not handle: MOVE_DYNAMIC (37),
/// SET_HOVER (39), SET_EQUIPMENT_SLOTS (42) and SET_GOSSIP_MENU (52), re-implemented from cmangos ScriptMgr.cpp's
/// ScriptAction::ExecuteDbscriptCommand and the field layout of ScriptMgr.h.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// SCRIPT_COMMAND_MOVE_DYNAMIC (37): the creature source moves to a point worked out from the target. maxDist (datalong) 0: the
    /// target's contact point, fixedDist (datalong3) beyond both bounding radii (COMMAND_ADDITIONAL: fixedDist from the target, no radii);
    /// otherwise a random point between minDist (datalong2) and maxDist of the target, in direction <c>o</c> when it is not 0
    /// (COMMAND_ADDITIONAL: relative to the source's facing), never below the target's height. dataint is the forced movement (1 walk,
    /// 2 run), dataint2 the relay started on arrival, dataint3 bit 1 clears the pushed movement first. A source in combat, a missing
    /// target, or maxDist 0 with the source its own target do nothing, as in cmangos.
    /// </summary>
    private void RelayMoveDynamic(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is not Creature mover)
        {
            ReportRelay(step, "MOVE_DYNAMIC by a non-creature");
            return;
        }

        if (target is null || mover.Combat.IsInCombat)
        {
            return;
        }

        bool additional = (step.DataFlags & FlagCommandAdditional) != 0;
        float x, y, z;
        if (step.DataLong == 0)
        {
            if (ReferenceEquals(target, mover))
            {
                return;
            }

            float dx = mover.X - target.X;
            float dy = mover.Y - target.Y;
            float angle = dx == 0f && dy == 0f ? mover.Orientation : MathF.Atan2(dy, dx);
            float distance = step.DataLong3 + (additional ? 0f : target.BoundingRadius + mover.BoundingRadius);
            x = target.X + (MathF.Cos(angle) * distance);
            y = target.Y + (MathF.Sin(angle) * distance);
            z = target.Z;
        }
        else
        {
            float orientation = additional ? mover.Orientation + step.Orientation + (2 * MathF.PI) : step.Orientation;
            float angle = orientation == 0f ? (float)(_random.NextDouble() * 2 * Math.PI) : orientation;
            float min = Math.Min(step.DataLong2, step.DataLong);
            float distance = min + ((float)_random.NextDouble() * (step.DataLong - min));
            x = target.X + (MathF.Cos(angle) * distance);
            y = target.Y + (MathF.Sin(angle) * distance);
            z = target.Z;
        }

        if ((step.DataInt3 & 0x1) != 0)
        {
            mover.Motion.Clear();
        }

        if (step.DataInt2 > 0)
        {
            _arrivalRelays[mover] = ((uint)step.DataInt2, target.Guid);
        }

        bool run = step.DataInt switch
        {
            1 => false,
            2 => true,
            _ => mover.ScriptRun,
        };
        mover.Motion.MovePoint(RelayMovePointId, x, y, z, run);
    }

    /// <summary>
    /// SCRIPT_COMMAND_SET_HOVER (39): datalong 1 sets, 0 clears MOVEFLAG_HOVER on the creature source. COMMAND_ADDITIONAL (the fly
    /// animation byte flag) is reported and the hover still applied; no z2815 row uses it.
    /// </summary>
    private void RelaySetHover(RelayScriptStep step, WorldObject? source)
    {
        if (source is not Creature hoverer)
        {
            ReportRelay(step, "SET_HOVER by a non-creature");
            return;
        }

        if ((step.DataFlags & FlagCommandAdditional) != 0)
        {
            ReportRelay(step, "SET_HOVER fly animation flag");
        }

        SetScriptHover(hoverer, step.DataLong != 0);
    }

    /// <summary>
    /// SCRIPT_COMMAND_SET_EQUIPMENT_SLOTS (42): datalong set puts back the creature's own equipment; otherwise dataint, dataint2 and dataint3
    /// are the main hand, off hand and ranged item entries, a negative value leaving the slot alone and 0 emptying it (cmangos
    /// Creature::SetVirtualItem: the item's display, class, subclass, material, inventory type and sheath). An item the world does not know
    /// is reported and its slot left as it was.
    /// </summary>
    private void RelaySetEquipment(RelayScriptStep step, WorldObject? source)
    {
        if (source is not Creature wearer)
        {
            ReportRelay(step, "SET_EQUIPMENT_SLOTS by a non-creature");
            return;
        }

        if (step.DataLong != 0)
        {
            if (wearer.ScriptEquipmentDefault is { } saved)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    wearer.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, saved[slot * 3]);
                    wearer.SetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2), saved[(slot * 3) + 1]);
                    wearer.SetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2) + 1, saved[(slot * 3) + 2]);
                }
            }

            return;
        }

        int[] items = [step.DataInt, step.DataInt2, step.DataInt3];
        for (int slot = 0; slot < 3; slot++)
        {
            if (items[slot] < 0)
            {
                continue;
            }

            ItemTemplate? proto = null;
            if (items[slot] != 0)
            {
                proto = _ai.ItemTemplateOf?.Invoke((uint)items[slot]);
                if (proto is null)
                {
                    ReportRelay(step, $"SET_EQUIPMENT_SLOTS with item {items[slot]} the world does not know");
                    continue;
                }
            }

            wearer.ScriptEquipmentDefault ??= SnapshotEquipment(wearer);
            int info = UpdateFields.UnitVirtualItemInfo + (slot * 2);
            if (proto is null)
            {
                wearer.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, 0);
                wearer.SetUInt32(info, 0);
                wearer.SetUInt32(info + 1, 0);
                continue;
            }

            wearer.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, proto.DisplayId);
            wearer.SetByte(info, 0, (byte)proto.Class);
            wearer.SetByte(info, 1, (byte)proto.SubClass);
            wearer.SetByte(info, 2, (byte)proto.Material);
            wearer.SetByte(info, 3, (byte)proto.InventoryType);
            wearer.SetByte(info + 1, 0, (byte)proto.Sheath);
        }
    }

    private static uint[] SnapshotEquipment(Creature creature)
    {
        var saved = new uint[9];
        for (int slot = 0; slot < 3; slot++)
        {
            saved[slot * 3] = creature.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot);
            saved[(slot * 3) + 1] = creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2));
            saved[(slot * 3) + 2] = creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2) + 1);
        }

        return saved;
    }

    /// <summary>SCRIPT_COMMAND_SET_GOSSIP_MENU (52): the creature TARGET's default gossip menu becomes datalong (cmangos SetDefaultGossipMenuId).</summary>
    private void RelaySetGossipMenu(RelayScriptStep step, WorldObject? target)
    {
        if (target is not Creature npc)
        {
            ReportRelay(step, "SET_GOSSIP_MENU on a non-creature");
            return;
        }

        npc.ScriptGossipMenuId = step.DataLong;
    }
}
