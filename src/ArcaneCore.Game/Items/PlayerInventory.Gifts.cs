using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Items;

/// <summary>
/// Gift wrapping (vmangos WorldSession::HandleWrapItemOpcode, ItemHandler.cpp:1049-1139) and opening a gift (HandleOpenItemOpcode's wrapped
/// branch, SpellHandler.cpp:200-227). The wrapped item keeps its guid and every field; its entry becomes the wrapping paper's
/// <c>wrapped_gift</c> entry and its own entry and flags wait in <see cref="Item.GiftEntry"/> / <see cref="Item.GiftFlags"/> (vmangos
/// <c>character_gifts</c>), saved with the item (<c>item_instance.gift_entry</c>, <c>gift_flags</c>).
/// </summary>
public sealed partial class PlayerInventory
{
    /// <summary>
    /// CMSG_WRAP_ITEM: wrap the item at (<paramref name="itemBag"/>, <paramref name="itemSlot"/>) with the wrapping paper at (<paramref name="giftBag"/>,
    /// <paramref name="giftSlot"/>), refusing in vmangos order (each refusal is SMSG_INVENTORY_CHANGE_FAILURE): no paper, or one that is not a
    /// wrapper or names no gift item, ITEM_NOT_FOUND; no item ITEM_NOT_FOUND; the paper itself or an item already wrapped WRAPPED_CANT_BE_WRAPPED;
    /// worn EQUIPPED_; a bag BAGS_; soulbound BOUND_; stackable STACKABLE_; unique (max count) UNIQUE_; while <paramref name="casting"/> a spell
    /// CANT_DO_RIGHT_NOW (vmangos IsNonMeleeSpellCasted, the use-twice exploit fix). On success one paper is used up.
    /// </summary>
    public InventoryResult WrapItem(byte giftBag, byte giftSlot, byte itemBag, byte itemSlot, bool casting = false)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return InventoryResult.CantDoRightNow;
        }

        InventoryResult Fail(InventoryResult result, Item? item)
        {
            SendEquipError(result, item, null);
            return result;
        }

        Item? gift = GetItem(giftBag, giftSlot);
        if (gift is null)
        {
            return Fail(InventoryResult.ItemNotFound, null);
        }

        if ((gift.Template.Flags & (uint)ItemTemplateFlags.Wrapper) == 0 || gift.Template.WrappedGift == 0
            || Templates.Find(gift.Template.WrappedGift) is not { } wrappedTemplate)
        {
            // vmangos ObjectMgr::LoadItemPrototypes clears a wrapped_gift that names no item, so it fails the same check (ObjectMgr.cpp:4203-4211).
            return Fail(InventoryResult.ItemNotFound, gift);
        }

        if (GetItem(itemBag, itemSlot) is not { } item)
        {
            return Fail(InventoryResult.ItemNotFound, null);
        }

        if (ReferenceEquals(item, gift))
        {
            return Fail(InventoryResult.WrappedCantBeWrapped, item);   // not possible with a real client
        }

        if (InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot))
        {
            return Fail(InventoryResult.EquippedCantBeWrapped, item);
        }

        if (item.GetUInt64(UpdateFields.ItemFieldGiftcreator) != 0)
        {
            return Fail(InventoryResult.WrappedCantBeWrapped, item);
        }

        if (item.IsBag)
        {
            return Fail(InventoryResult.BagsCantBeWrapped, item);
        }

        if (item.IsSoulBound)
        {
            return Fail(InventoryResult.BoundCantBeWrapped, item);
        }

        if (Math.Max(1u, item.Template.MaxStackSize()) != 1)
        {
            return Fail(InventoryResult.StackableCantBeWrapped, item);
        }

        if (item.Template.MaxCount > 0)
        {
            return Fail(InventoryResult.UniqueCantBeWrapped, item);   // vmangos: "maybe not correct check (it is better than nothing)"
        }

        if (casting)
        {
            return Fail(InventoryResult.CantDoRightNow, item);
        }

        item.GiftEntry = item.Entry;
        item.GiftFlags = item.GetUInt32(UpdateFields.ItemFieldFlags);
        item.ChangeEntry(wrappedTemplate);
        item.SetUInt64(UpdateFields.ItemFieldGiftcreator, OwnerGuid.Value);
        item.SetUInt32(UpdateFields.ItemFieldFlags, (uint)ItemDynFlags.Wrapped);
        DestroyItemCount(gift, 1);
        return InventoryResult.Ok;
    }

    /// <summary>
    /// CMSG_OPEN_ITEM on a wrapped item (vmangos SpellHandler.cpp:200-227): the gift creator is cleared and the item's own entry and flags come
    /// back; the maximum durability follows the restored template (the stored gift row has none). A wrapped item whose contents are unknown
    /// (no stored entry, or an entry no template has) is destroyed, as vmangos does when its character_gifts row is missing.
    /// Returns false when <paramref name="item"/> is not wrapped.
    /// </summary>
    public bool OpenGift(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((item.DynamicFlags & ItemDynFlags.Wrapped) == 0)
        {
            return false;
        }

        if (item.GiftEntry == 0 || Templates.Find(item.GiftEntry) is not { } own)
        {
            DestroyItem(item.BagSlot, item.Slot);
            return true;
        }

        uint flags = item.GiftFlags;
        item.SetUInt64(UpdateFields.ItemFieldGiftcreator, 0);
        item.ChangeEntry(own);
        item.SetUInt32(UpdateFields.ItemFieldFlags, flags);
        item.SetUInt32(UpdateFields.ItemFieldMaxdurability, own.MaxDurability);
        item.Durability = Math.Min(item.Durability, own.MaxDurability);
        item.GiftEntry = 0;
        item.GiftFlags = 0;
        return true;
    }
}
