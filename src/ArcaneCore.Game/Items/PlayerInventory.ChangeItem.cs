using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// SPELL_EFFECT_SUMMON_CHANGE_ITEM (34; vmangos Spell::EffectSummonChangeItem, SpellEffects.cpp): <paramref name="old"/> becomes one item of
    /// <paramref name="entry"/> in the same place. The permanent and temporary enchantments carry over and the new item takes the old one's
    /// durability loss. An inventory or bank item is stored in its slot (CanStoreItem with swap), a worn one is equipped in its slot
    /// (CanEquipItem with swap). Returns the new item, or null when the old item is not owned here or the new one does not fit there.
    /// </summary>
    public Item? ChangeItem(Item old, uint entry)
    {
        ArgumentNullException.ThrowIfNull(old);
        Player?.EnsureQuestSettlementMutationAllowed();
        if (old.OwnerGuid != _ownerGuid || Templates.Find(entry) is not { } template)
        {
            return null;
        }

        byte bag = old.BagSlot;
        byte slot = old.Slot;
        Item item = Item.Create(NextGuid(), template, _ownerGuid);
        for (int enchant = 0; enchant <= 1; enchant++)
        {
            for (int field = 0; field < 3; field++)
            {
                int index = UpdateFields.ItemFieldEnchantment + (enchant * 3) + field;
                item.SetUInt32(index, old.GetUInt32(index));
            }
        }

        if (old.MaxDurability > 0 && old.Durability < old.MaxDurability && item.MaxDurability > 0)
        {
            // Spell::EffectSummonChangeItem (SpellEffects.cpp:2244-2248) then Player::DurabilityLoss
            // (Player.cpp:4831-4835): the same share of the new maximum, truncated, and at least one point.
            // A disabled durability loss leaves the new item whole, because DurabilityPointsLoss returns first.
            double lost = 1 - (old.Durability / (double)old.MaxDurability);
            DurabilityLoss(item, lost);
        }

        if (InventorySlots.IsEquipmentPos(bag, slot) && slot < InventorySlots.EquipmentEnd)
        {
            if (CanEquipItem(slot, out byte equipAt, template, item, swap: true) != InventoryResult.Ok)
            {
                return null;
            }

            DestroyItem(bag, slot);
            Item worn = EquipItem(equipAt, item);
            AutoUnequipWeaponsIfNeeded();
            // vmangos Spell::EffectSummonChangeItem follows EquipItem with Player::AutoUnequipOffhandIfNeed: a two-hander
            // that replaced the worn main hand sends the off hand to the bags.
            AutoUnequipOffhandIfNeeded();
            return worn;
        }

        var dest = new List<ItemPosCount>();
        if (CanStoreItem(bag, slot, dest, item, swap: true, out _) != InventoryResult.Ok || dest.Count == 0)
        {
            return null;
        }

        DestroyItem(bag, slot);
        Item stored = StoreItem(dest, item);
        ItemCountChanged?.Invoke(template.Entry, 1);
        return stored;
    }
}
