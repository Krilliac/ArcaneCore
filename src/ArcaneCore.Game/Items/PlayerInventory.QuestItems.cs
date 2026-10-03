namespace ArcaneCore.Game.Items;

/// <summary>Quest source-item removal checks (vmangos Player::CanUnequipItems, Player.cpp:8326-8397).</summary>
public sealed partial class PlayerInventory
{
    /// <summary>
    /// Whether <paramref name="count"/> of <paramref name="entry"/> can be removed without taking off a
    /// worn item that cannot come off now (in combat, disarmed, logging out). Worn copies count only when
    /// they can be unequipped; the backpack, keyring and bag contents always count; the bank does not.
    /// Returns <see cref="InventoryResult.Ok"/> as soon as enough removable copies are found, otherwise the
    /// last unequip refusal (Ok when there simply are not enough).
    /// </summary>
    public InventoryResult CanUnequipItems(uint entry, uint count)
    {
        uint found = 0;
        InventoryResult refusal = InventoryResult.Ok;

        for (byte slot = 0; slot < InventorySlots.BagEnd; slot++)
        {
            if (_items[slot] is { } worn && worn.Entry == entry)
            {
                InventoryResult result = CanUnequipItem(InventorySlots.Bag0, slot, swap: false);
                if (result == InventoryResult.Ok)
                {
                    found += worn.Count;
                    if (found >= count)
                    {
                        return InventoryResult.Ok;
                    }
                }
                else
                {
                    refusal = result;
                }
            }
        }

        foreach ((byte begin, byte end) in new (byte, byte)[] { (InventorySlots.ItemStart, InventorySlots.ItemEnd), (InventorySlots.KeyringStart, InventorySlots.KeyringEnd) })
        {
            for (byte slot = begin; slot < end; slot++)
            {
                if (_items[slot] is { } item && item.Entry == entry)
                {
                    found += item.Count;
                    if (found >= count)
                    {
                        return InventoryResult.Ok;
                    }
                }
            }
        }

        for (byte bag = InventorySlots.BagStart; bag < InventorySlots.BagEnd; bag++)
        {
            if (_items[bag] is Container container)
            {
                for (int slot = 0; slot < container.Size; slot++)
                {
                    if (container[slot] is { } item && item.Entry == entry)
                    {
                        found += item.Count;
                        if (found >= count)
                        {
                            return InventoryResult.Ok;
                        }
                    }
                }
            }
        }

        return refusal;
    }
}
