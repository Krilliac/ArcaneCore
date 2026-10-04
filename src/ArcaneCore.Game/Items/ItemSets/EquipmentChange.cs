namespace ArcaneCore.Game.Items;

/// <summary>
/// What <see cref="PlayerInventory.EquipmentChanged"/> reports. Placement and function are separate because a broken item still counts
/// for an item set (mangos "item set bonuses applied only at equip and removed at unequip, and still active for broken items") but not
/// for its stats or its Equip: spells (Player::_ApplyItemMods skips broken items).
/// </summary>
public enum EquipmentChange
{
    /// <summary>The item was put into an equipment slot (Player::EquipItem).</summary>
    Worn,

    /// <summary>The item was taken out of an equipment slot (Player::RemoveItem, DestroyItem).</summary>
    Removed,

    /// <summary>The worn item's stat mods were applied: worn and not broken (equip, or repaired).</summary>
    ModsApplied,

    /// <summary>The worn item's stat mods were removed: unequipped, or it just broke.</summary>
    ModsRemoved,
}
