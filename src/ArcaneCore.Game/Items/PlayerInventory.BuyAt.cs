using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>
/// Where a bought item goes when the client names a position (CMSG_BUY_ITEM_IN_SLOT): the
/// placement half of vmangos <c>Player::BuyItemFromVendor</c>
/// (D:\refs\vmangos\src\game\Objects\Player.cpp:18453-18496).
/// </summary>
public sealed partial class PlayerInventory
{
    /// <summary>
    /// The placement checks of BuyItemFromVendor without changing anything: an inventory position
    /// (or no position at all) must be storable there (CanStoreNewItem(bag, slot)), an equipment
    /// position needs a count of one and a legal wearer slot (CanEquipNewItem), anything else is
    /// <see cref="InventoryResult.ItemDoesntGoToSlot"/> (Player.cpp:18455-18496).
    /// </summary>
    public InventoryResult CheckAddItemAt(byte bag, byte slot, uint entry, uint count)
    {
        if (Templates.Find(entry) is not { } template)
        {
            return InventoryResult.ItemNotFound;
        }

        if (IsStorePosition(bag, slot))
        {
            return CanStoreItem(bag, slot, [], template, count, null, swap: false, out _, out _);
        }

        if (InventorySlots.IsEquipmentPos(bag, slot))
        {
            // Player.cpp:18471-18475.
            return count != 1
                ? InventoryResult.ItemCantBeEquipped
                : CanEquipItem(slot, out _, template, null, swap: false);
        }

        return InventoryResult.ItemDoesntGoToSlot;
    }

    /// <summary>
    /// Create <paramref name="count"/> new items of <paramref name="entry"/> at the named position and
    /// tell the client (StoreNewItem or EquipNewItem + AutoUnequipOffhandIfNeed, then SendNewItem;
    /// Player.cpp:18455-18496, 18521). All or nothing: on a refusal nothing changes and the result is
    /// returned for the caller's SendEquipError.
    /// </summary>
    public InventoryResult AddItemAt(byte bag, byte slot, uint entry, uint count, out Item? item, bool received = false)
    {
        item = null;
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return InventoryResult.CantDoRightNow;
        }

        InventoryResult check = CheckAddItemAt(bag, slot, entry, count);
        if (check != InventoryResult.Ok)
        {
            return check;
        }

        ItemTemplate template = Templates.Find(entry)!;
        if (!IsStorePosition(bag, slot))
        {
            CanEquipItem(slot, out byte wearer, template, null, swap: false);
            item = EquipItem(wearer, Item.Create(NextGuid(), template, _ownerGuid));
            ItemCountChanged?.Invoke(entry, 1);
            AutoUnequipOffhandIfNeeded();
        }
        else
        {
            var dest = new List<ItemPosCount>();
            CanStoreItem(bag, slot, dest, template, count, null, swap: false, out _, out _);
            item = StoreNewItem(dest, template, count);
        }

        if (Player is { IsInWorld: true } player)
        {
            player.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(player.Guid, item, count, received, false, true));
        }

        return InventoryResult.Ok;
    }

    /// <summary>Player.cpp:18455: no position at all, or an inventory position.</summary>
    private static bool IsStorePosition(byte bag, byte slot)
        => (bag == InventorySlots.NullBag && slot == InventorySlots.NullSlot) || InventorySlots.IsInventoryPos(bag, slot);

    /// <summary>
    /// The position byte CMSG_BUY_ITEM_IN_SLOT's bag GUID names (HandleBuyItemInSlotOpcode,
    /// ItemHandler.cpp:661-683): the player's own GUID is the backpack side
    /// (<see cref="InventorySlots.Bag0"/>), otherwise the slot of the equipped bag with that GUID;
    /// null when no such bag is worn (the packet is then ignored).
    /// </summary>
    public byte? FindBagSlot(ObjectGuid bagGuid)
    {
        if (Player is { } player && bagGuid == player.Guid)
        {
            return InventorySlots.Bag0;
        }

        for (byte slot = InventorySlots.BagStart; slot < InventorySlots.BagEnd; slot++)
        {
            if (_items[slot] is { } bag && bag.Guid == bagGuid)
            {
                return slot;
            }
        }

        return null;
    }
}
