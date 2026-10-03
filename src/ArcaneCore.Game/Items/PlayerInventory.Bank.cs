using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>Bank shortcuts, equip-to-slot and read item (vmangos ItemHandler.cpp:92-106, 417-441, 911-986).</summary>
public sealed partial class PlayerInventory
{
    /// <summary>WorldSession::HandleAutoBankItemOpcode: move the item at (bag, slot) to the first fitting bank place.</summary>
    public void AutoBankItem(byte srcBag, byte srcSlot)
    {
        if (Player is { CanMutateQuestSettlementState: false } || GetItem(srcBag, srcSlot) is not { } item)
        {
            return;
        }

        if (!BankUsable)
        {
            SendEquipError(InventoryResult.TooFarAwayFromBank, item, null);
            return;
        }

        MoveWithBankRules(item, srcBag, srcSlot, toBank: true);
    }

    /// <summary>
    /// WorldSession::HandleAutoStoreBankItemOpcode: an item in the bank goes to the inventory, any
    /// other item goes to the bank.
    /// </summary>
    public void AutoStoreBankItem(byte srcBag, byte srcSlot)
    {
        if (Player is { CanMutateQuestSettlementState: false } || GetItem(srcBag, srcSlot) is not { } item)
        {
            return;
        }

        if (!BankUsable)
        {
            SendEquipError(InventoryResult.TooFarAwayFromBank, item, null);
            return;
        }

        MoveWithBankRules(item, srcBag, srcSlot, toBank: !InventorySlots.IsBankPos(srcBag, srcSlot));
    }

    private void MoveWithBankRules(Item item, byte srcBag, byte srcSlot, bool toBank)
    {
        var dest = new List<ItemPosCount>();
        InventoryResult msg;
        byte bagSlot;
        if (toBank)
        {
            msg = CanBankItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out bagSlot);
        }
        else
        {
            msg = CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out bagSlot);
        }

        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, item, null, bagSlot);
            return;
        }

        if (toBank && dest.Count == 1 && dest[0].Bag == srcBag && dest[0].Slot == srcSlot)
        {
            // vmangos: "no-op: placed in same slot", only the gray item state is cleared.
            SendEquipError(InventoryResult.None, item, null);
            return;
        }

        RemoveItem(srcBag, srcSlot);
        StoreItem(dest, item);
    }

    /// <summary>
    /// WorldSession::HandleAutoEquipItemSlotOpcode: swap the item with <paramref name="itemGuid"/>
    /// into equipment slot <paramref name="dstSlot"/>; non-equipment slots and an item already
    /// there are ignored (anti-cheat in vmangos).
    /// </summary>
    public void AutoEquipItemSlot(ObjectGuid itemGuid, byte dstSlot)
    {
        if (!InventorySlots.IsEquipmentPos(InventorySlots.Bag0, dstSlot))
        {
            return;
        }

        Item? item = GetItemByGuid(itemGuid);
        if (item is null || (item.Container is null && item.Slot == dstSlot))
        {
            return;
        }

        SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, dstSlot);
    }

    /// <summary>WorldSession::HandleReadItemOpcode: true when the item is readable now (reply SMSG_READ_ITEM_OK).</summary>
    public bool TryReadItem(byte bag, byte slot)
    {
        if (Player is not { } player)
        {
            return false;
        }

        if (GetItem(bag, slot) is not { } item || item.Template.PageText == 0)
        {
            SendEquipError(InventoryResult.ItemNotFound, null, null);
            return false;
        }

        InventoryResult msg = CanUseItem(item);
        if (msg == InventoryResult.Ok)
        {
            player.Session.Send(WorldOpcode.SmsgReadItemOk, ItemMiscPackets.ReadItemOk(item.Guid));
            return true;
        }

        player.Session.Send(WorldOpcode.SmsgReadItemFailed, ItemMiscPackets.ReadItemFailed(item.Guid));
        SendEquipError(msg, item, null);
        return false;
    }
}
