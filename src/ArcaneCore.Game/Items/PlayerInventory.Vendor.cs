using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>Why a vendor refused an item (vmangos SellResult values used by HandleSellItemOpcode).</summary>
public enum VendorSellError : byte
{
    None = 0,
    CantFindItem = 1,
    CantSellItem = 2,
}

/// <summary>
/// Vendor-side inventory operations: selling into the buyback slots, buying back, repairing and
/// durability loss (vmangos WorldSession::HandleSellItemOpcode / HandleBuybackItem,
/// Player::AddItemToBuyBackSlot / RemoveItemFromBuyBackSlot, DurabilityRepair(All),
/// DurabilityLossAll / DurabilityPointsLoss). Money is the caller's: these methods only move items.
/// </summary>
/// <remarks>
/// Buyback items are never saved (vmangos _SaveInventory deletes them): <see cref="AllItems"/>
/// and <see cref="CreateSnapshot"/> skip the buyback slots, so a sold item leaves the database
/// on the next save and returns with its own GUID when bought back.
/// </remarks>
public sealed partial class PlayerInventory
{
    /// <summary>vmangos: the timestamp is "now − login time + 30 h"; slots are compared by it.</summary>
    public const uint BuybackLifetimeSeconds = 30 * 3600;

    public static bool IsBuybackSlot(byte slot) => slot >= InventorySlots.BuybackStart && slot < InventorySlots.BuybackEnd;

    /// <summary>vmangos Player::GetItemFromBuyBackSlot.</summary>
    public Item? GetBuybackItem(byte slot) => IsBuybackSlot(slot) ? _items[slot] : null;

    /// <summary>PLAYER_FIELD_BUYBACK_PRICE_n of a buyback slot (0 when empty or not a buyback slot).</summary>
    public uint GetBuybackPrice(byte slot)
        => IsBuybackSlot(slot) ? Player?.GetUInt32(UpdateFields.PlayerFieldBuybackPrice1 + slot - InventorySlots.BuybackStart) ?? 0 : 0;

    /// <summary>PLAYER_FIELD_BUYBACK_TIMESTAMP_n of a buyback slot.</summary>
    public uint GetBuybackTimestamp(byte slot)
        => IsBuybackSlot(slot) ? Player?.GetUInt32(UpdateFields.PlayerFieldBuybackTimestamp1 + slot - InventorySlots.BuybackStart) ?? 0 : 0;

    /// <summary>
    /// The item half of vmangos HandleSellItemOpcode: the item must be the player's, carried
    /// (equipment, bags, backpack, keyring — never bank or buyback), not a non-empty bag, removable
    /// when worn, sellable (SellPrice &gt; 0); <paramref name="count"/> 0 sells the stack and more
    /// than the stack is refused. A partial stack is split. The sold part moves to a buyback slot
    /// priced SellPrice × count. <paramref name="money"/> is what the caller must pay out.
    /// </summary>
    public VendorSellError SellItem(ObjectGuid itemGuid, uint count, uint timestamp, out uint money)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        money = 0;
        if (GetItemByGuid(itemGuid) is not { } item)
        {
            return VendorSellError.CantFindItem;
        }

        byte bag = item.BagSlot;
        byte slot = item.Slot;
        if (item.OwnerGuid != _ownerGuid || IsInBank(item)
            || (item is Container container && !container.IsEmpty)
            || (bag == InventorySlots.Bag0 && slot < InventorySlots.BagEnd && CanUnequipItem(bag, slot, swap: false) != InventoryResult.Ok))
        {
            return VendorSellError.CantSellItem;
        }

        if (count == 0)
        {
            count = item.Count;
        }
        else if (count > item.Count)
        {
            return VendorSellError.CantSellItem;
        }

        if (item.Template.SellPrice == 0)
        {
            return VendorSellError.CantSellItem;
        }

        ulong total = (ulong)item.Template.SellPrice * count;
        money = total > uint.MaxValue ? uint.MaxValue : (uint)total;
        Item sold;
        if (count < item.Count)
        {
            sold = item.Clone(NextGuid(), count, _ownerGuid);
            item.Count -= count;
        }
        else
        {
            RemoveItem(bag, slot);
            sold = item;
        }

        ItemCountChanged?.Invoke(item.Entry, -(int)count);
        AddItemToBuybackSlot(sold, money, timestamp);
        return VendorSellError.None;
    }

    /// <summary>Where the buyback item of <paramref name="slot"/> would go (vmangos CanStoreItem(NULL_BAG, NULL_SLOT, …)).</summary>
    public InventoryResult CanRestoreBuyback(byte slot, List<ItemPosCount> dest)
    {
        ArgumentNullException.ThrowIfNull(dest);
        return GetBuybackItem(slot) is { } item
            ? CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out _)
            : InventoryResult.ItemNotFound;
    }

    /// <summary>
    /// vmangos HandleBuybackItem after the money check: clear the slot and store the item per
    /// <paramref name="dest"/> (from <see cref="CanRestoreBuyback"/>). Returns the stored item.
    /// </summary>
    public Item RestoreBuyback(byte slot, IReadOnlyList<ItemPosCount> dest)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        Item item = GetBuybackItem(slot) ?? throw new InvalidOperationException($"buyback slot {slot} is empty");
        ClearBuybackSlot(slot);
        uint count = item.Count;
        Item stored = StoreItem(dest, item);
        ItemCountChanged?.Invoke(stored.Entry, (int)count);
        return stored;
    }

    /// <summary>
    /// The items vmangos Player::DurabilityRepairAll walks (equipment, equipped bags and backpack,
    /// then the contents of equipped bags; not the bank or keyring), or the one carried item with
    /// <paramref name="itemGuid"/> (vmangos DurabilityRepair by position: any position the player
    /// can address, the bank included).
    /// </summary>
    public IReadOnlyList<Item> RepairCandidates(ObjectGuid itemGuid)
    {
        if (!itemGuid.IsEmpty)
        {
            return GetItemByGuid(itemGuid) is { } single ? [single] : [];
        }

        var items = new List<Item>();
        for (byte slot = 0; slot < InventorySlots.ItemEnd; slot++)
        {
            if (_items[slot] is { } item)
            {
                items.Add(item);
            }
        }

        for (byte bag = InventorySlots.BagStart; bag < InventorySlots.BagEnd; bag++)
        {
            if (_items[bag] is Container container)
            {
                items.AddRange(container.Items);
            }
        }

        return items;
    }

    /// <summary>
    /// Restore an item's durability (the end of vmangos Player::DurabilityRepair): a worn item that
    /// was broken gets its stats back.
    /// </summary>
    public void RepairDurability(Item item)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(item);
        uint before = item.Durability;
        if (item.MaxDurability == 0 || before == item.MaxDurability)
        {
            return;
        }

        item.Durability = item.MaxDurability;
        if (before == 0 && item.Container is null && item.Slot < InventorySlots.EquipmentEnd && ReferenceEquals(_items[item.Slot], item))
        {
            ApplyMods(item, item.Slot, apply: true);
        }
    }

    /// <summary>
    /// vmangos Player::DurabilityLossAll: every worn item and, with <paramref name="inventory"/>,
    /// the backpack and equipped-bag contents lose max(1, int(max × percent)) points.
    /// </summary>
    public void DurabilityLossAll(double percent, bool inventory)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (_items[slot] is { } item)
            {
                DurabilityLoss(item, percent);
            }
        }

        if (!inventory)
        {
            return;
        }

        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (_items[slot] is { } item)
            {
                DurabilityLoss(item, percent);
            }
        }

        for (byte bag = InventorySlots.BagStart; bag < InventorySlots.BagEnd; bag++)
        {
            if (_items[bag] is Container container)
            {
                foreach (Item inner in container.Items.ToList())
                {
                    DurabilityLoss(inner, percent);
                }
            }
        }
    }

    /// <summary>vmangos Player::DurabilityLoss.</summary>
    public void DurabilityLoss(Item item, double percent)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.MaxDurability == 0)
        {
            return;
        }

        int points = (int)(item.MaxDurability * percent);
        DurabilityPointsLoss(item, Math.Max(points, 1));
    }

    /// <summary>vmangos Player::DurabilityPointsLoss: a worn item that breaks loses its stats.</summary>
    public void DurabilityPointsLoss(Item item, int points)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Options.DurabilityLossEnable)
        {
            return; // DurabilityLoss.Enable, the first statement of Player.cpp:4866.
        }

        uint max = item.MaxDurability;
        uint before = item.Durability;
        long after = Math.Clamp((long)before - points, 0, max);
        if (max == 0 || after == before)
        {
            return;
        }

        bool equipped = item.Container is null && item.Slot < InventorySlots.EquipmentEnd && ReferenceEquals(_items[item.Slot], item);
        if (after == 0 && before > 0 && equipped)
        {
            // The stat maintainer rejects broken items, so remove contributions while
            // the item still has its old positive durability (Player.cpp:4884-4886).
            ApplyMods(item, item.Slot, apply: false);
        }

        item.Durability = (uint)after;
        if (before == 0 && after > 0 && equipped)
        {
            // Signed negative points can repair a broken item; reapply only after it
            // becomes usable again (Player.cpp:4890-4892).
            ApplyMods(item, item.Slot, apply: true);
        }
    }

    /// <summary>
    /// vmangos Player::AddItemToBuyBackSlot: the first empty buyback slot, else the one with the
    /// oldest timestamp (whose item is deleted); the slot's GUID, price and timestamp fields follow.
    /// </summary>
    private void AddItemToBuybackSlot(Item item, uint price, uint timestamp)
    {
        byte target = InventorySlots.BuybackStart;
        uint oldest = uint.MaxValue;
        bool foundEmpty = false;
        for (byte slot = InventorySlots.BuybackStart; slot < InventorySlots.BuybackEnd; slot++)
        {
            if (_items[slot] is null)
            {
                target = slot;
                foundEmpty = true;
                break;
            }

            uint time = GetBuybackTimestamp(slot);
            if (time < oldest)
            {
                oldest = time;
                target = slot;
            }
        }

        if (!foundEmpty && _items[target] is { } evicted)
        {
            ClearBuybackSlot(target);
            Discard(evicted);
        }

        _items[target] = item;
        SetSlotField(target, item.Guid);
        item.ContainedIn = _ownerGuid;
        item.OwnerGuid = _ownerGuid;
        item.Slot = target;
        item.Container = null;
        item.Inventory = this;
        int index = target - InventorySlots.BuybackStart;
        Player?.SetUInt32(UpdateFields.PlayerFieldBuybackPrice1 + index, price);
        Player?.SetUInt32(UpdateFields.PlayerFieldBuybackTimestamp1 + index, timestamp);
        SendCreateIfNeeded(item);
    }

    /// <summary>vmangos Player::RemoveItemFromBuyBackSlot (the item is kept by the caller).</summary>
    private void ClearBuybackSlot(byte slot)
    {
        if (_items[slot] is { } item)
        {
            item.Slot = InventorySlots.NullSlot;
            item.ContainedIn = ObjectGuid.Empty;
        }

        _items[slot] = null;
        SetSlotField(slot, ObjectGuid.Empty);
        int index = slot - InventorySlots.BuybackStart;
        Player?.SetUInt32(UpdateFields.PlayerFieldBuybackPrice1 + index, 0);
        Player?.SetUInt32(UpdateFields.PlayerFieldBuybackTimestamp1 + index, 0);
    }
}
