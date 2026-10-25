using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature equipment: the three virtual item slots (main hand, off hand, ranged) that show the weapons, shield and ranged item a
/// creature holds (UNIT_VIRTUAL_ITEM_SLOT_DISPLAY and UNIT_VIRTUAL_ITEM_INFO; cmangos/vmangos <c>Creature::SetVirtualItem</c>).
/// Thread affinity: world thread.
/// </summary>
public sealed partial class Creature
{
    /// <summary>Main hand, off hand and ranged: the three virtual item slots of a unit.</summary>
    public const int VirtualItemSlotCount = 3;

    private readonly Func<uint, ItemTemplate?>? _itemResolver;

    /// <summary>
    /// The <c>creature_equip_template</c> id a running game event forces (<c>game_event_creature_data.equipment_id</c>), or 0 for the
    /// template's own. Like the other event overrides it outlives a respawn while the event runs.
    /// </summary>
    public uint EventEquipmentId { get; private set; }

    /// <summary>
    /// Write one virtual item slot (cmangos <c>Creature::SetVirtualItem</c>): the item's display id, then the info bytes class, subclass,
    /// material and inventory type, with the sheath type in the first byte of the next word. A null item empties the slot.
    /// </summary>
    public void SetVirtualItem(int slot, ItemTemplate? item)
    {
        if (item is null)
        {
            ClearVirtualItem(slot);
            return;
        }

        SetVirtualItem(slot, item.DisplayId, (byte)item.Class, (byte)item.SubClass, (byte)item.Material, (byte)item.InventoryType, (byte)item.Sheath);
    }

    /// <summary>Write one virtual item slot from its raw parts (scripts that equip an item the item store does not carry).</summary>
    public void SetVirtualItem(int slot, uint display, byte itemClass, byte subclass, byte material, byte inventoryType, byte sheath)
    {
        int info = VirtualItemInfoIndex(slot);
        SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, display);
        SetByte(info, 0, itemClass);
        SetByte(info, 1, subclass);
        SetByte(info, 2, material);
        SetByte(info, 3, inventoryType);
        SetUInt32(info + 1, sheath);
    }

    /// <summary>Empty one virtual item slot: the display and both info words become 0.</summary>
    public void ClearVirtualItem(int slot)
    {
        int info = VirtualItemInfoIndex(slot);
        SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, 0);
        SetUInt32(info, 0);
        SetUInt32(info + 1, 0);
    }

    /// <summary>The three slots as nine words (display, info, sheath word per slot), for a later <see cref="RestoreVirtualItems"/>.</summary>
    internal uint[] SnapshotVirtualItems()
    {
        var saved = new uint[VirtualItemSlotCount * 3];
        for (int slot = 0; slot < VirtualItemSlotCount; slot++)
        {
            saved[slot * 3] = GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot);
            saved[(slot * 3) + 1] = GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2));
            saved[(slot * 3) + 2] = GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2) + 1);
        }

        return saved;
    }

    internal void RestoreVirtualItems(uint[] saved)
    {
        for (int slot = 0; slot < VirtualItemSlotCount; slot++)
        {
            SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, saved[slot * 3]);
            SetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2), saved[(slot * 3) + 1]);
            SetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2) + 1, saved[(slot * 3) + 2]);
        }
    }

    /// <summary>
    /// Put the creature's equipment in the three slots: the running game event's equipment id, else the template's
    /// <see cref="CreatureTemplate.EquipmentTemplateId"/>, resolved through <c>creature_equip_template</c> and the world item store. A
    /// slot with no item (no equipment id, an unknown id, an item the store does not know) is emptied, so a respawn or an entry change
    /// never keeps the previous equipment. A DB script's saved default is dropped: what this writes is the default again.
    /// </summary>
    internal void ApplyEquipment()
    {
        ScriptEquipmentDefault = null;
        CreatureEquipment? set = Content.FindEquipment(EventEquipmentId != 0 ? EventEquipmentId : Template.EquipmentTemplateId);
        for (int slot = 0; slot < VirtualItemSlotCount; slot++)
        {
            uint item = set?.ItemOf(slot) ?? 0;
            SetVirtualItem(slot, item == 0 ? null : _itemResolver?.Invoke(item));
        }
    }

    /// <summary>Set the equipment id a game event forces (0: none) and show it at once on a living creature; a dead one shows it from its respawn.</summary>
    internal void SetEventEquipment(uint equipmentId)
    {
        EventEquipmentId = equipmentId;
        if (DeathState == CreatureDeathState.Alive)
        {
            ApplyEquipment();
        }
    }

    private static int VirtualItemInfoIndex(int slot)
    {
        if ((uint)slot >= VirtualItemSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "a unit has 3 virtual item slots (main hand, off hand, ranged)");
        }

        return UpdateFields.UnitVirtualItemInfo + (slot * 2);
    }
}
