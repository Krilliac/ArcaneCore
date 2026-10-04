namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// vmangos Player::DurabilityPointsLossAll (Player.cpp:4838-4862): every worn item and, with
    /// <paramref name="inventory"/>, the backpack and equipped-bag contents lose
    /// <paramref name="points"/> durability. Bags themselves, the keyring and the bank are skipped.
    /// </summary>
    public void DurabilityPointsLossAll(int points, bool inventory)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        foreach (Item item in DurabilityCandidates(inventory))
        {
            DurabilityPointsLoss(item, points);
        }
    }

    /// <summary>vmangos Player::DurabilityPointLossForEquipSlot (Player.cpp:4896): one point off the item worn in <paramref name="slot"/>.</summary>
    public void DurabilityPointLossForEquipSlot(byte slot)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        if (slot < InventorySlots.EquipmentEnd && _items[slot] is { } item)
        {
            DurabilityPointsLoss(item, 1);
        }
    }

    /// <summary>
    /// The pool the hit-taken wear roll draws from (<c>MapCombat.RollHitTakenDurability</c>): fills <paramref name="slots"/>, in slot
    /// order, with every equipment slot that holds a piece of armor (<see cref="ItemClass.Armor"/>, so a shield counts and a weapon
    /// does not) whose template has a durability maximum, and returns how many there are. The reference core rolls over all 19
    /// slots and lets an empty slot (mangosserver <c>PlayerDurability.cpp:253-259</c>, the null check) or an item without durability
    /// (<c>PlayerDurability.cpp:213-228</c>: old and new durability both 0, nothing changes) absorb the roll; this pool leaves those
    /// out, so every successful percent roll wears armor. A broken piece (durability 0, maximum above 0) stays in the pool and its
    /// roll does nothing, as in the reference. Allocation free; <paramref name="slots"/> needs <see cref="InventorySlots.EquipmentEnd"/> entries.
    /// </summary>
    public int CollectWornArmorWithDurability(Span<byte> slots)
    {
        int count = 0;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (_items[slot] is { } item && item.Template.Class == (uint)ItemClass.Armor && item.Template.MaxDurability > 0)
            {
                slots[count++] = slot;
            }
        }

        return count;
    }

    /// <summary>Items in the order vmangos walks them: equipment, then backpack, then equipped-bag contents.</summary>
    private List<Item> DurabilityCandidates(bool inventory)
    {
        var items = new List<Item>();
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (_items[slot] is { } item)
            {
                items.Add(item);
            }
        }

        if (!inventory)
        {
            return items;
        }

        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
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
}
