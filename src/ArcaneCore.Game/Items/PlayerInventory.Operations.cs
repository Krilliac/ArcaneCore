using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>The client-driven inventory operations (vmangos ItemHandler.cpp and Player::SwapItem / SplitItem).</summary>
public sealed partial class PlayerInventory
{
    /// <summary>vmangos Player::SwapItem: move, merge/fill or swap the items at two positions.</summary>
    public void SwapItem(byte srcBag, byte srcSlot, byte dstBag, byte dstSlot)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return;
        }

        Item? src = GetItem(srcBag, srcSlot);
        Item? dst = GetItem(dstBag, dstSlot);
        if (src is null)
        {
            return;
        }

        // The dead may only rearrange their bags.
        if (Player is { IsAlive: false } && !(InventorySlots.IsInventoryPos(srcBag, srcSlot) && InventorySlots.IsInventoryPos(dstBag, dstSlot)))
        {
            SendEquipError(InventoryResult.YouAreDead, src, dst);
            return;
        }

        bool srcIsBagPos = InventorySlots.IsBagPos(srcBag, srcSlot);
        bool dstIsBagPos = InventorySlots.IsBagPos(dstBag, dstSlot);
        if (InventorySlots.IsEquipmentPos(srcBag, srcSlot) || srcIsBagPos)
        {
            // A bag may move to an empty bag slot or swap with an (empty) bag.
            InventoryResult msg = CanUnequipItem(srcBag, srcSlot, !srcIsBagPos || dstIsBagPos || dst is Container { IsEmpty: true });
            if (msg != InventoryResult.Ok)
            {
                SendEquipError(msg, src, dst);
                return;
            }
        }

        // Never put a bag inside itself.
        if (srcIsBagPos && srcSlot == dstBag)
        {
            SendEquipError(InventoryResult.NonemptyBagOverOtherBag, src, dst);
            return;
        }

        if (dstIsBagPos && dstSlot == srcBag)
        {
            SendEquipError(InventoryResult.NonemptyBagOverOtherBag, dst, src);
            return;
        }

        if (dst is not null && (InventorySlots.IsEquipmentPos(dstBag, dstSlot) || dstIsBagPos))
        {
            InventoryResult msg = CanUnequipItem(dstBag, dstSlot, !dstIsBagPos || srcIsBagPos || src is Container { IsEmpty: true });
            if (msg != InventoryResult.Ok)
            {
                SendEquipError(msg, src, dst);
                return;
            }
        }

        if (dst is null)
        {
            MoveToEmpty(src, srcBag, srcSlot, dstBag, dstSlot);
            return;
        }

        if (!src.IsBag && !dst.IsBag && TryMerge(src, dst, srcBag, srcSlot, dstBag, dstSlot))
        {
            return;
        }

        Exchange(src, dst, srcBag, srcSlot, dstBag, dstSlot, srcIsBagPos, dstIsBagPos);
    }

    /// <summary>
    /// WorldSession::HandleAutoEquipItemOpcode: wear the item at (bag, slot), putting what was
    /// worn there into the source position (or wherever it fits). Returns true when the item was
    /// put into a bag slot.
    /// </summary>
    public bool AutoEquipItem(byte srcBag, byte srcSlot)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return false;
        }

        if (GetItem(srcBag, srcSlot) is not { } src)
        {
            return false;
        }

        InventoryResult msg = CanEquipItem(InventorySlots.NullSlot, out byte dest, src.Template, src, swap: !src.IsBag);
        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, src, null);
            return false;
        }

        if (src.Container is null && src.Slot == dest)
        {
            return false; // vmangos: "prevent equip in same slot, only at cheat"
        }

        if (_items[dest] is not { } worn)
        {
            RemoveItem(srcBag, srcSlot);
            EquipItem(dest, src);
            AutoUnequipOffhandIfNeeded();
            return InventorySlots.IsBagPos(InventorySlots.Bag0, dest);
        }

        msg = CanUnequipItem(InventorySlots.Bag0, dest, !src.IsBag);
        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, worn, src);
            return false;
        }

        var sSrc = new List<ItemPosCount>();
        byte eSrc = 0;
        byte bagSlot = 0;
        if (InventorySlots.IsInventoryPos(srcBag, srcSlot))
        {
            msg = CanStoreItem(srcBag, srcSlot, sSrc, worn, swap: true, out bagSlot);
            if (msg != InventoryResult.Ok)
            {
                sSrc.Clear();
                msg = CanStoreItem(srcBag, InventorySlots.NullSlot, sSrc, worn, swap: true, out bagSlot);
            }

            if (msg != InventoryResult.Ok)
            {
                sSrc.Clear();
                msg = CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, sSrc, worn, swap: true, out bagSlot);
            }
        }
        else if (InventorySlots.IsBankPos(srcBag, srcSlot))
        {
            msg = CanBankItem(srcBag, srcSlot, sSrc, worn, swap: true, out bagSlot);
            if (msg != InventoryResult.Ok)
            {
                sSrc.Clear();
                msg = CanBankItem(srcBag, InventorySlots.NullSlot, sSrc, worn, swap: true, out bagSlot);
            }

            if (msg != InventoryResult.Ok)
            {
                sSrc.Clear();
                msg = CanBankItem(InventorySlots.NullBag, InventorySlots.NullSlot, sSrc, worn, swap: true, out bagSlot);
            }
        }
        else if (InventorySlots.IsEquipmentPos(srcBag, srcSlot))
        {
            msg = CanEquipItem(srcSlot, out eSrc, worn.Template, worn, swap: true);
            if (msg == InventoryResult.Ok)
            {
                msg = CanUnequipItem(InventorySlots.Bag0, eSrc, swap: true);
            }
        }

        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, worn, src, bagSlot);
            return false;
        }

        RemoveItem(InventorySlots.Bag0, dest);
        RemoveItem(srcBag, srcSlot);
        EquipItem(dest, src);
        if (InventorySlots.IsEquipmentPos(srcBag, srcSlot))
        {
            EquipItem(eSrc, worn);
        }
        else
        {
            StoreItem(sSrc, worn);
        }

        AutoUnequipOffhandIfNeeded();
        return InventorySlots.IsBagPos(InventorySlots.Bag0, dest);
    }

    /// <summary>WorldSession::HandleAutoStoreBagItemOpcode: move an item into a bag (any free place in it).</summary>
    public void AutoStoreBagItem(byte srcBag, byte srcSlot, byte dstBag)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return;
        }

        if (GetItem(srcBag, srcSlot) is not { } item)
        {
            return;
        }

        if (!IsValidPosition(dstBag, InventorySlots.NullSlot, explicitPos: false))
        {
            SendEquipError(InventoryResult.ItemDoesntGoToSlot, null, null);
            return;
        }

        if (InventorySlots.IsEquipmentPos(srcBag, srcSlot) || InventorySlots.IsBagPos(srcBag, srcSlot))
        {
            InventoryResult unequip = CanUnequipItem(srcBag, srcSlot, !InventorySlots.IsBagPos(srcBag, srcSlot));
            if (unequip != InventoryResult.Ok)
            {
                SendEquipError(unequip, item, null);
                return;
            }
        }

        var dest = new List<ItemPosCount>();
        bool toBank = dstBag >= InventorySlots.BankBagStart && dstBag < InventorySlots.BankBagEnd;
        InventoryResult msg;
        byte bagSlot;
        if (toBank)
        {
            msg = CanBankItem(dstBag, InventorySlots.NullSlot, dest, item, swap: false, out bagSlot);
        }
        else
        {
            msg = CanStoreItem(dstBag, InventorySlots.NullSlot, dest, item, swap: false, out bagSlot);
        }

        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, item, null, bagSlot);
            return;
        }

        if (dest.Count == 1 && dest[0].Bag == srcBag && dest[0].Slot == srcSlot)
        {
            // vmangos: "no-op... just remove gray item state".
            SendEquipError(InventoryResult.None, item, null);
            return;
        }

        RemoveItem(srcBag, srcSlot);
        StoreItem(dest, item);
    }

    /// <summary>vmangos Player::SplitItem: move <paramref name="count"/> of a stack to a new item at the destination.</summary>
    public void SplitItem(byte srcBag, byte srcSlot, byte dstBag, byte dstSlot, uint count)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return;
        }

        if (GetItem(srcBag, srcSlot) is not { } src)
        {
            SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        // vmangos Player::SplitItem (Player.cpp:10976-10981): "prevent split looting item"; the new stack would roll its own loot.
        if (src.HasGeneratedLoot)
        {
            SendEquipError(InventoryResult.CouldntSplitItems, src, null);
            return;
        }

        // vmangos: "not let split all items (can be only at cheating)" / "not let split more existing items".
        if (src.Count == count)
        {
            SendEquipError(InventoryResult.CouldntSplitItems, src, null);
            return;
        }

        if (src.Count < count)
        {
            SendEquipError(InventoryResult.TriedToSplitMoreThanCount, src, null);
            return;
        }

        Item split = src.Clone(NextGuid(), count, _ownerGuid);

        // The source shrinks before the checks (max-count) and is restored on failure.
        src.Count -= count;
        var dest = new List<ItemPosCount>();
        InventoryResult msg;
        byte bagSlot = 0;
        byte eDest = 0;
        if (InventorySlots.IsInventoryPos(dstBag, dstSlot))
        {
            msg = CanStoreItem(dstBag, dstSlot, dest, split, swap: false, out bagSlot);
        }
        else if (InventorySlots.IsBankPos(dstBag, dstSlot))
        {
            msg = CanBankItem(dstBag, dstSlot, dest, split, swap: false, out bagSlot);
        }
        else if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            msg = CanEquipItem(dstSlot, out eDest, split.Template, split, swap: false);
        }
        else
        {
            msg = InventoryResult.ItemDoesntGoToSlot;
        }

        if (msg != InventoryResult.Ok)
        {
            src.Count += count;
            SendEquipError(msg, src, null, bagSlot);
            return;
        }

        if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            EquipItem(eDest, split);
            AutoUnequipOffhandIfNeeded();
        }
        else
        {
            StoreItem(dest, split);
        }
    }

    /// <summary>
    /// WorldSession::HandleDestroyItemOpcode: worn items and bags must be able to come off; an
    /// indestructible item answers CANT_DROP_SOULBOUND; a count of 0 destroys the whole stack.
    /// </summary>
    public void DestroyItemRequest(byte bag, byte slot, byte count)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return;
        }

        if (InventorySlots.IsEquipmentPos(bag, slot) || InventorySlots.IsBagPos(bag, slot))
        {
            InventoryResult msg = CanUnequipItem(bag, slot, swap: false);
            if (msg != InventoryResult.Ok)
            {
                SendEquipError(msg, GetItem(bag, slot), null);
                return;
            }
        }

        if (GetItem(bag, slot) is not { } item)
        {
            SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        if (item.Template.HasFlag(ItemTemplateFlags.Indestructible))
        {
            SendEquipError(InventoryResult.CantDropSoulbound, null, null);
            return;
        }

        if (count != 0)
        {
            DestroyItemCount(item, count);
        }
        else
        {
            DestroyItem(bag, slot);
        }
    }

    private void MoveToEmpty(Item src, byte srcBag, byte srcSlot, byte dstBag, byte dstSlot)
    {
        var dest = new List<ItemPosCount>();
        if (InventorySlots.IsInventoryPos(dstBag, dstSlot) || InventorySlots.IsBankPos(dstBag, dstSlot))
        {
            InventoryResult msg = InventorySlots.IsInventoryPos(dstBag, dstSlot)
                ? CanStoreItem(dstBag, dstSlot, dest, src, swap: false, out byte bagSlot)
                : CanBankItem(dstBag, dstSlot, dest, src, swap: false, out bagSlot);
            if (msg != InventoryResult.Ok)
            {
                SendEquipError(msg, src, null, bagSlot);
                return;
            }

            RemoveItem(srcBag, srcSlot);
            StoreItem(dest, src);
            AutoUnequipOffhandIfNeeded();
        }
        else if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            InventoryResult msg = CanEquipItem(dstSlot, out byte eDest, src.Template, src, swap: false);
            if (msg != InventoryResult.Ok)
            {
                SendEquipError(msg, src, null);
                return;
            }

            RemoveItem(srcBag, srcSlot);
            EquipItem(eDest, src);
            AutoUnequipOffhandIfNeeded();
        }
    }

    /// <summary>vmangos SwapItem "attempt merge to / fill target item"; false when a merge is not possible (try a swap).</summary>
    private bool TryMerge(Item src, Item dst, byte srcBag, byte srcSlot, byte dstBag, byte dstSlot)
    {
        var sDest = new List<ItemPosCount>();
        byte eDest = 0;
        InventoryResult msg;
        if (InventorySlots.IsInventoryPos(dstBag, dstSlot))
        {
            msg = CanStoreItem(dstBag, dstSlot, sDest, src, swap: false, out _);
        }
        else if (InventorySlots.IsBankPos(dstBag, dstSlot))
        {
            msg = CanBankItem(dstBag, dstSlot, sDest, src, swap: false, out _);
        }
        else if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            msg = CanEquipItem(dstSlot, out eDest, src.Template, src, swap: false);
        }
        else
        {
            return false;
        }

        if (msg != InventoryResult.Ok)
        {
            return false;
        }

        uint max = src.Template.MaxStackSize();
        if (src.Count + dst.Count <= max)
        {
            RemoveItem(srcBag, srcSlot);
            if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
            {
                EquipItem(eDest, src);
                AutoUnequipOffhandIfNeeded();
            }
            else
            {
                StoreItem(sDest, src);
            }
        }
        else
        {
            src.Count = src.Count + dst.Count - max;
            dst.Count = max;
        }

        return true;
    }

    /// <summary>vmangos SwapItem "impossible merge/fill, do real swap", including the bag-contents exchange.</summary>
    private void Exchange(Item src, Item dst, byte srcBag, byte srcSlot, byte dstBag, byte dstSlot, bool srcIsBagPos, bool dstIsBagPos)
    {
        // src into dst's position
        var sDest = new List<ItemPosCount>();
        byte eDest = 0;
        byte bagSlot = 0;
        InventoryResult msg = InventoryResult.Ok;
        if (InventorySlots.IsInventoryPos(dstBag, dstSlot))
        {
            msg = CanStoreItem(dstBag, dstSlot, sDest, src, swap: true, out bagSlot);
        }
        else if (InventorySlots.IsBankPos(dstBag, dstSlot))
        {
            msg = CanBankItem(dstBag, dstSlot, sDest, src, swap: true, out bagSlot);
        }
        else if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            msg = CanEquipItem(dstSlot, out eDest, src.Template, src, swap: true);
            if (msg == InventoryResult.Ok)
            {
                msg = CanUnequipItem(InventorySlots.Bag0, eDest, swap: true);
            }
        }

        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, src, dst, bagSlot);
            return;
        }

        // dst into src's position
        var sDest2 = new List<ItemPosCount>();
        byte eDest2 = 0;
        if (InventorySlots.IsInventoryPos(srcBag, srcSlot))
        {
            msg = CanStoreItem(srcBag, srcSlot, sDest2, dst, swap: true, out bagSlot);
        }
        else if (InventorySlots.IsBankPos(srcBag, srcSlot))
        {
            msg = CanBankItem(srcBag, srcSlot, sDest2, dst, swap: true, out bagSlot);
        }
        else if (InventorySlots.IsEquipmentPos(srcBag, srcSlot))
        {
            msg = CanEquipItem(srcSlot, out eDest2, dst.Template, dst, swap: true);
            if (msg == InventoryResult.Ok)
            {
                msg = CanUnequipItem(InventorySlots.Bag0, eDest2, swap: true);
            }
        }

        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, dst, src, bagSlot);
            return;
        }

        // An empty bag outside the bag slots trades places with a full one: the contents move over.
        if (src is Container srcContainer && dst is Container dstContainer)
        {
            Container? emptyBag = null;
            Container? fullBag = null;
            if (srcContainer.IsEmpty && !srcIsBagPos)
            {
                (emptyBag, fullBag) = (srcContainer, dstContainer);
            }
            else if (dstContainer.IsEmpty && !dstIsBagPos)
            {
                (emptyBag, fullBag) = (dstContainer, srcContainer);
            }

            if (emptyBag is not null && fullBag is not null)
            {
                int contents = 0;
                foreach (Item inner in fullBag.Items)
                {
                    if (!inner.Template.CanGoIntoBag(emptyBag.Template))
                    {
                        SendEquipError(InventoryResult.NonemptyBagOverOtherBag, src, dst);
                        return;
                    }

                    contents++;
                }

                if (contents > emptyBag.Size)
                {
                    SendEquipError(InventoryResult.ItemsCantBeSwapped, src, dst);
                    return;
                }

                byte target = 0;
                for (byte i = 0; i < fullBag.Size; i++)
                {
                    if (fullBag[i] is { } inner)
                    {
                        fullBag.RemoveItem(i);
                        emptyBag.StoreItem(target++, inner);
                    }
                }
            }
        }

        RemoveItem(dstBag, dstSlot);
        RemoveItem(srcBag, srcSlot);

        if (InventorySlots.IsEquipmentPos(dstBag, dstSlot))
        {
            EquipItem(eDest, src);
        }
        else
        {
            StoreItem(sDest, src);
        }

        if (InventorySlots.IsEquipmentPos(srcBag, srcSlot))
        {
            EquipItem(eDest2, dst);
        }
        else
        {
            StoreItem(sDest2, dst);
        }

        AutoUnequipOffhandIfNeeded();
    }
}
