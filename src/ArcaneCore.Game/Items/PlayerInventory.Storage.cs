using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>vmangos Player::StoreItem: place an item per <paramref name="dest"/> (clones for all but the last position). Returns the item holding the last part.</summary>
    public Item StoreItem(IReadOnlyList<ItemPosCount> dest, Item item)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(item);
        if (dest.Count == 0)
        {
            throw new ArgumentException("no destination", nameof(dest));
        }

        Item last = item;
        for (int i = 0; i < dest.Count; i++)
        {
            last = StoreItemAt(dest[i].Bag, dest[i].Slot, item, dest[i].Count, clone: i < dest.Count - 1);
        }

        return last;
    }

    /// <summary>vmangos Player::StoreNewItem: create <paramref name="count"/> items of <paramref name="template"/> per <paramref name="dest"/> (from CanStoreItem).</summary>
    public Item StoreNewItem(IReadOnlyList<ItemPosCount> dest, ItemTemplate template, uint count)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(template);
        Item item = Item.Create(NextGuid(), template, _ownerGuid);
        item.Count = count;
        Item stored = StoreItem(dest, item);
        ItemCountChanged?.Invoke(template.Entry, (int)count);
        return stored;
    }

    /// <summary>vmangos Player::EquipItem: wear <paramref name="item"/> in own slot <paramref name="slot"/> (merging into a worn stack of the same item).</summary>
    public Item EquipItem(byte slot, Item item)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(item);
        if (_items[slot] is { } existing)
        {
            existing.Count += item.Count;
            Discard(item);
            return existing;
        }

        VisualizeItem(slot, item);
        if (slot < InventorySlots.EquipmentEnd)
        {
            ApplyMods(item, slot, apply: true);
        }

        SendCreateIfNeeded(item);
        return item;
    }

    /// <summary>
    /// vmangos Player::RemoveItem: take an item out of its slot without deleting it (it stays
    /// owned, "in hand", until stored, equipped or destroyed).
    /// </summary>
    public void RemoveItem(byte bag, byte slot)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        if (GetItem(bag, slot) is not { } item)
        {
            return;
        }

        if (bag == InventorySlots.Bag0)
        {
            if (slot < InventorySlots.EquipmentEnd)
            {
                ApplyMods(item, slot, apply: false);
                if (slot is InventorySlots.MainHand or InventorySlots.OffHand or InventorySlots.Ranged)
                    Player?.Combat.ResetExtraAttacks();
                SetVisibleItemSlot(slot, null);
            }

            _items[slot] = null;
            SetSlotField(slot, ObjectGuid.Empty);
        }
        else
        {
            ((Container)_items[bag]!).RemoveItem(slot);
        }

        item.ContainedIn = ObjectGuid.Empty;
        item.Slot = InventorySlots.NullSlot;
    }

    /// <summary>vmangos Player::DestroyItem: remove and delete an item (a bag takes its contents with it).</summary>
    public void DestroyItem(byte bag, byte slot)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        if (GetItem(bag, slot) is not { } item)
        {
            return;
        }

        if (item is Container container && bag == InventorySlots.Bag0)
        {
            for (byte i = 0; i < container.Size; i++)
            {
                if (container[i] is { } inner)
                {
                    container.RemoveItem(i);
                    ItemCountChanged?.Invoke(inner.Entry, -(int)inner.Count);
                    Discard(inner);
                }
            }
        }

        RemoveItem(bag, slot);
        ItemCountChanged?.Invoke(item.Entry, -(int)item.Count);
        Discard(item);
    }

    /// <summary>vmangos Player::DestroyItemCount(Item*): destroy up to <paramref name="count"/> of one stack; returns how many were destroyed.</summary>
    public uint DestroyItemCount(Item item, uint count)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(item);
        if (item.Count <= count)
        {
            uint destroyed = item.Count;
            DestroyItem(item.BagSlot, item.Slot);
            return destroyed;
        }

        item.Count -= count;
        ItemCountChanged?.Invoke(item.Entry, -(int)count);
        return count;
    }

    /// <summary>
    /// Destroy up to <paramref name="count"/> items of <paramref name="entry"/> (vmangos
    /// Player::DestroyItemCount(entry, …) order: backpack, keyring, bag contents, then worn items
    /// that may come off, then — when asked — the bank). Returns how many were destroyed.
    /// </summary>
    public uint DestroyItemCount(uint entry, uint count, bool includeBank = false)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        uint remaining = count;
        foreach (Item item in RemovalOrder(includeBank).Where(i => i.Entry == entry).ToList())
        {
            if (remaining == 0)
            {
                break;
            }

            if (item.Container is null && item.Slot < InventorySlots.BagEnd
                && CanUnequipItem(InventorySlots.Bag0, item.Slot, swap: false) != InventoryResult.Ok)
            {
                continue;
            }

            remaining -= DestroyItemCount(item, remaining);
        }

        return count - remaining;
    }

    /// <summary>Whether <paramref name="count"/> new items of <paramref name="entry"/> fit anywhere (vmangos CanStoreNewItem with NULL_BAG/NULL_SLOT).</summary>
    public InventoryResult CanStoreNewItem(uint entry, uint count, List<ItemPosCount> dest, out uint noSpaceCount)
    {
        ArgumentNullException.ThrowIfNull(dest);
        noSpaceCount = count;
        if (Templates.Find(entry) is not { } template)
        {
            return InventoryResult.ItemNotFound;
        }

        return CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, template, count, null, swap: false, out _, out noSpaceCount);
    }

    /// <summary>
    /// Add <paramref name="count"/> new items of <paramref name="entry"/> wherever they fit (all
    /// or nothing) and tell the client with SMSG_ITEM_PUSH_RESULT (vmangos StoreNewItem +
    /// SendNewItem). On failure nothing changes; the caller reports the result (SendEquipError).
    /// </summary>
    public InventoryResult AddItem(uint entry, uint count, out Item? item, bool received = false, bool created = false, bool showInChat = true)
    {
        item = null;
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return InventoryResult.CantDoRightNow;
        }

        var dest = new List<ItemPosCount>();
        InventoryResult result = CanStoreNewItem(entry, count, dest, out _);
        if (result != InventoryResult.Ok)
        {
            return result;
        }

        item = StoreNewItem(dest, Templates.Find(entry)!, count);
        if (Player is { IsInWorld: true } player)
        {
            player.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(player.Guid, item, count, received, created, showInChat));
        }

        return InventoryResult.Ok;
    }

    /// <summary>
    /// vmangos Player::AddStartingItems: every playercreateinfo_item row through
    /// StoreNewItemInBestSlots (equip one by one, else store in the backpack), then a pass over
    /// the backpack equipping what could not be worn before and re-storing the rest; a piece of
    /// ammo found there becomes the selected ammo (Player.cpp:570-575).
    /// </summary>
    public void AddStartingItems()
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        foreach (StartingItem starting in Templates.StartingItems((byte)Race, (byte)Class))
        {
            StoreNewItemInBestSlots(starting.ItemId, starting.Amount);
        }

        for (byte i = InventorySlots.ItemStart; i < InventorySlots.ItemEnd; i++)
        {
            if (_items[i] is not { } item)
            {
                continue;
            }

            if (CanEquipItem(InventorySlots.NullSlot, out byte eDest, item.Template, item, swap: false, notLoading: false) == InventoryResult.Ok)
            {
                RemoveItem(InventorySlots.Bag0, i);
                EquipItem(eDest, item);
            }
            else
            {
                var dest = new List<ItemPosCount>();
                if (CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out _) == InventoryResult.Ok
                    && !(dest.Count == 1 && dest[0].Bag == InventorySlots.Bag0 && dest[0].Slot == i))
                {
                    RemoveItem(InventorySlots.Bag0, i);
                    StoreItem(dest, item);
                }

                // "if this is ammo then use it" (Player.cpp:570-575).
                if (CanUseAmmo(item.Entry) == InventoryResult.Ok)
                {
                    SetAmmo(item.Entry);
                }
            }
        }

        _loaded = true;
    }

    /// <summary>vmangos Player::StoreNewItemInBestSlots.</summary>
    public bool StoreNewItemInBestSlots(uint entry, uint amount)
    {
        if (Player is { CanMutateQuestSettlementState: false })
        {
            return false;
        }

        if (Templates.Find(entry) is not { } template)
        {
            return false;
        }

        while (amount > 0)
        {
            if (CanEquipItem(InventorySlots.NullSlot, out byte eDest, template, null, swap: false, notLoading: false) != InventoryResult.Ok)
            {
                break;
            }

            Item item = EquipItem(eDest, Item.Create(NextGuid(), template, _ownerGuid));
            AutoUnequipOffhandIfNeeded();
            if (amount > 1 && amount <= template.MaxStackSize())
            {
                item.Count = amount;
                ItemCountChanged?.Invoke(entry, (int)amount);
                return true;
            }

            ItemCountChanged?.Invoke(entry, 1);
            amount--;
        }

        if (amount == 0)
        {
            return true;
        }

        var dest = new List<ItemPosCount>();
        if (CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, template, amount, null, swap: false, out _, out _) == InventoryResult.Ok)
        {
            StoreNewItem(dest, template, amount);
            return true;
        }

        return false;
    }

    private IEnumerable<Item> RemovalOrder(bool includeBank)
    {
        IEnumerable<Item> Own(byte begin, byte end)
        {
            for (byte s = begin; s < end; s++)
            {
                if (_items[s] is { } item)
                {
                    yield return item;
                }
            }
        }

        IEnumerable<Item> BagContents(byte begin, byte end)
        {
            for (byte s = begin; s < end; s++)
            {
                if (_items[s] is Container bag)
                {
                    foreach (Item inner in bag.Items)
                    {
                        yield return inner;
                    }
                }
            }
        }

        IEnumerable<Item> order = Own(InventorySlots.ItemStart, InventorySlots.ItemEnd)
            .Concat(Own(InventorySlots.KeyringStart, InventorySlots.KeyringEnd))
            .Concat(BagContents(InventorySlots.BagStart, InventorySlots.BagEnd))
            .Concat(Own(0, InventorySlots.BagEnd));
        return includeBank
            ? order.Concat(Own(InventorySlots.BankItemStart, InventorySlots.BankItemEnd)).Concat(BagContents(InventorySlots.BankBagStart, InventorySlots.BankBagEnd))
            : order;
    }

    private bool CanLoadIntoOwnSlot(ItemTemplate template, byte slot)
    {
        if (slot >= InventorySlots.KeyringEnd || _items[slot] is not null)
        {
            return false;
        }

        if (slot < InventorySlots.EquipmentEnd)
        {
            return template.AllowedEquipSlots(Class, canDualWield: true).Contains(slot);
        }

        if (slot < InventorySlots.BagEnd || (slot >= InventorySlots.BankBagStart && slot < InventorySlots.BankBagEnd))
        {
            return template.IsBag();
        }

        if (slot >= InventorySlots.KeyringStart)
        {
            return (BagFamily)template.BagFamily == BagFamily.Keys;
        }

        return true;
    }

    private void PlaceInOwnSlot(byte slot, Item item)
    {
        _items[slot] = item;
        SetSlotField(slot, item.Guid);
        item.ContainedIn = _ownerGuid;
        item.OwnerGuid = _ownerGuid;
        item.Slot = slot;
        item.Container = null;
        item.Inventory = this;
        if (slot < InventorySlots.EquipmentEnd)
        {
            SetVisibleItemSlot(slot, item);
        }

        if (item is Container bag)
        {
            foreach (Item inner in bag.Items)
            {
                inner.Inventory = this;
            }
        }
    }

    /// <summary>vmangos Player::_StoreItem.</summary>
    private Item StoreItemAt(byte bag, byte slot, Item item, uint count, bool clone)
    {
        Item? existing = GetItem(bag, slot);
        if (existing is null)
        {
            if (clone)
            {
                item = item.Clone(NextGuid(), count, _ownerGuid);
            }
            else
            {
                item.Count = count;
            }

            BindOnStore(item, bag, slot);
            if (bag == InventorySlots.Bag0)
            {
                PlaceInOwnSlot(slot, item);
            }
            else
            {
                ((Container)_items[bag]!).StoreItem(slot, item);
                item.Inventory = this;
            }

            SendCreateIfNeeded(item);
            return item;
        }

        BindOnStore(existing, bag, slot);
        existing.Count += count;
        if (!clone)
        {
            Discard(item);
        }

        return existing;
    }

    private static void BindOnStore(Item item, byte bag, byte slot)
    {
        var bonding = (ItemBonding)item.Template.Bonding;
        if (bonding is ItemBonding.WhenPickedUp or ItemBonding.QuestItem
            || (bonding == ItemBonding.WhenEquipped && InventorySlots.IsBagPos(bag, slot)))
        {
            item.SetBinding(true);
        }
    }

    /// <summary>vmangos Player::VisualizeItem: bind on equip, place, show.</summary>
    private void VisualizeItem(byte slot, Item item)
    {
        if ((ItemBonding)item.Template.Bonding is ItemBonding.WhenEquipped or ItemBonding.WhenPickedUp or ItemBonding.QuestItem)
        {
            item.SetBinding(true);
        }

        PlaceInOwnSlot(slot, item);
    }

    /// <summary>
    /// vmangos Player::AutoUnequipOffhandIfNeed: with a two-hander worn, the off hand goes to the
    /// bags. vmangos mails it when nothing fits; there is no mail yet, so it stays worn (CanEquipItem
    /// already refuses a two-hander whose off hand would not fit, so that is not reached in practice).
    /// </summary>
    /// <summary>
    /// vmangos Player::AutoUnequipWeaponsIfNeed (Player.cpp:19697-19710): main hand, off hand and ranged items the
    /// player can no longer use (a weapon skill was lost) go back to the bags. The reference mails an item that
    /// finds no bag space (AutoUnequipItemFromSlot, Player.cpp:19720-19745); there is no mail system here, so such
    /// an item stays equipped.
    /// </summary>
    public void AutoUnequipWeaponsIfNeeded()
    {
        foreach (byte slot in new[] { InventorySlots.MainHand, InventorySlots.OffHand, InventorySlots.Ranged })
        {
            if (_items[slot] is not { } item || CanUseItem(item, notLoading: false) == InventoryResult.Ok)
            {
                continue;
            }

            var dest = new List<ItemPosCount>();
            if (CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out _) == InventoryResult.Ok)
            {
                RemoveItem(InventorySlots.Bag0, slot);
                StoreItem(dest, item);
            }
        }
    }

    private void AutoUnequipOffhandIfNeeded()
    {
        if (!IsTwoHandUsed || _items[InventorySlots.OffHand] is not { } offHand)
        {
            return;
        }

        var dest = new List<ItemPosCount>();
        if (CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, offHand, swap: false, out _) == InventoryResult.Ok)
        {
            RemoveItem(InventorySlots.Bag0, InventorySlots.OffHand);
            StoreItem(dest, offHand);
        }
    }

    private uint NextGuid() => (GuidAllocator ?? throw new InvalidOperationException("no item GUID allocator set")).Next();

    /// <summary>The stat hook, kept symmetric: an item counts once while worn and unbroken (vmangos _ApplyItemMods skips broken items).</summary>
    private void ApplyMods(Item item, byte slot, bool apply)
    {
        if (Player is null)
        {
            return;
        }

        if (apply)
        {
            if (item.MaxDurability > 0 && item.Durability == 0)
            {
                return;
            }

            if (_modsApplied.Add(item))
            {
                StatsApplier.Apply(Player, item, slot, apply: true);
                EquipSpellSink?.OnItemEquipped(Player, item, slot, apply: true);
                for (int enchantmentSlot = 0; enchantmentSlot < Item.EnchantmentValues / 3; enchantmentSlot++)
                    EnchantmentSink?.ApplyEnchantment(Player, item, enchantmentSlot, apply: true);
            }
        }
        else if (_modsApplied.Remove(item))
        {
            EquipSpellSink?.OnItemEquipped(Player, item, slot, apply: false);
            for (int enchantmentSlot = 0; enchantmentSlot < Item.EnchantmentValues / 3; enchantmentSlot++)
                EnchantmentSink?.ApplyEnchantment(Player, item, enchantmentSlot, apply: false);
            StatsApplier.Apply(Player, item, slot, apply: false);
        }
    }

    private void SetSlotField(byte slot, ObjectGuid guid) => Player?.SetUInt64(UpdateFields.PlayerFieldInvSlotHead + (slot * 2), guid.Value);

    /// <summary>
    /// vmangos Player::SetVisibleItemSlot: PLAYER_VISIBLE_ITEM_n (stride 12): creator GUID, entry,
    /// 7 enchantment ids, random property (int16 in the low half) and suffix factor.
    /// </summary>
    private void SetVisibleItemSlot(byte slot, Item? item)
    {
        if (Player is not { } player)
        {
            return;
        }

        int offset = slot * VisibleItemStride;
        player.SetUInt64(UpdateFields.PlayerVisibleItem1Creator + offset, item?.Creator.Value ?? 0);
        player.SetUInt32(UpdateFields.PlayerVisibleItem10 + offset, item?.Entry ?? 0);
        for (int i = 0; i < 7; i++)
        {
            player.SetUInt32(UpdateFields.PlayerVisibleItem10 + offset + 1 + i, item?.EnchantmentId(i) ?? 0);
        }

        int properties = UpdateFields.PlayerVisibleItem1Properties + offset;
        if (item is null)
        {
            player.SetUInt32(properties, 0);
        }
        else
        {
            player.SetUInt16(properties, 0, unchecked((ushort)(short)item.RandomPropertyId));
        }

        player.SetUInt32(properties + 1, item?.SuffixFactor ?? 0);
    }

    internal void RefreshVisibleEnchantment(Item item, int enchantmentSlot, uint? displayedId = null)
    {
        if (Player is not { } player || item.Slot >= InventorySlots.EquipmentEnd || (uint)enchantmentSlot >= 2u)
            return;
        player.SetUInt32(UpdateFields.PlayerVisibleItem10 + item.Slot * VisibleItemStride + 1 + enchantmentSlot,
            displayedId ?? item.EnchantmentId(enchantmentSlot));
    }

    /// <summary>A new item reaches the owner as its own create block, queued before the player's field changes (vmangos Item::SendCreateUpdateToPlayer).</summary>
    private void SendCreateIfNeeded(Item item)
    {
        if (Player is { IsInWorld: true } player && !item.SentToClient)
        {
            WriteCreate(player.PendingUpdates, item, 0);
        }
    }

    private void WriteCreate(UpdateData updates, Item item, uint nowMs)
    {
        PacketWriter block = updates.BeginBlock();
        UpdateBlockWriter.WriteCreateBlock(block, item, Player!, isNewObject: false, nowMs);
        updates.EndBlock();
        item.ClearChangedFields();
        item.SentToClient = true;
        if (item is Container bag)
        {
            foreach (Item inner in bag.Items)
            {
                WriteCreate(updates, inner, nowMs);
            }
        }
    }

    /// <summary>Forget an item; the client is told to drop it after anything already queued (vmangos Item::DestroyForPlayer).</summary>
    private void Discard(Item item)
    {
        if (item.SentToClient && Player is { IsInWorld: true } player)
        {
            player.PendingUpdates.Flush((opcode, payload) => player.Session.Send(opcode, payload), 0);
            var writer = new PacketWriter(8);
            writer.WriteUInt64(item.Guid.Value);
            player.Session.Send(WorldOpcode.SmsgDestroyObject, writer.ToArray());
        }

        _modsApplied.Remove(item);
        item.SentToClient = false;
        item.Inventory = null;
        item.Container = null;
    }
}
