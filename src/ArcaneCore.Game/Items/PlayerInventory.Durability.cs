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
