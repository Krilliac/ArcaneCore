using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>Item-template rules (vmangos ItemPrototype helpers and Item.cpp free functions).</summary>
public static class ItemTemplateRules
{
    private static readonly uint[] WeaponSkills =
    [
        ItemSkills.Axes, ItemSkills.TwoHandedAxes, ItemSkills.Bows, ItemSkills.Guns, ItemSkills.Maces,
        ItemSkills.TwoHandedMaces, ItemSkills.Polearms, ItemSkills.Swords, ItemSkills.TwoHandedSwords, 0,
        ItemSkills.Staves, 0, 0, ItemSkills.Unarmed, 0,
        ItemSkills.Daggers, ItemSkills.Thrown, ItemSkills.Assassination, ItemSkills.Crossbows, ItemSkills.Wands,
        ItemSkills.Fishing,
    ];

    private static readonly uint[] ArmorSkills =
        [0, ItemSkills.Cloth, ItemSkills.Leather, ItemSkills.Mail, ItemSkills.PlateMail, 0, ItemSkills.Shield, 0, 0, 0];

    public static InventoryType GetInventoryType(this ItemTemplate t) => (InventoryType)t.InventoryType;

    /// <summary>vmangos Item::IsBag: the inventory type is INVTYPE_BAG.</summary>
    public static bool IsBag(this ItemTemplate t) => t.InventoryType == (uint)InventoryType.Bag;

    /// <summary>vmangos ItemPrototype::GetMaxStackSize.</summary>
    public static uint MaxStackSize(this ItemTemplate t) => t.Stackable;

    public static bool HasFlag(this ItemTemplate t, ItemTemplateFlags flag) => (t.Flags & (uint)flag) != 0;

    /// <summary>vmangos ItemPrototype::CanChangeEquipStateInCombat: relics, shields, held items, weapons, projectiles.</summary>
    public static bool CanChangeEquipStateInCombat(this ItemTemplate t)
        => t.GetInventoryType() is InventoryType.Relic or InventoryType.Shield or InventoryType.Holdable
        || (ItemClass)t.Class is ItemClass.Weapon or ItemClass.Projectile;

    /// <summary>vmangos ItemPrototype::GetProficiencySkill (weapon and armor sub-class skills).</summary>
    public static uint ProficiencySkill(this ItemTemplate t) => (ItemClass)t.Class switch
    {
        ItemClass.Weapon => t.SubClass < WeaponSkills.Length ? WeaponSkills[t.SubClass] : 0,
        ItemClass.Armor => t.SubClass < ArmorSkills.Length ? ArmorSkills[t.SubClass] : 0,
        _ => 0,
    };

    /// <summary>
    /// vmangos ItemPrototype::GetAllowedEquipSlots: up to four candidate slots
    /// (<see cref="InventorySlots.NullSlot"/> when unused). One-hand weapons offer the off hand
    /// only to a dual-wielder; relics only fit the class that uses them.
    /// </summary>
    public static byte[] AllowedEquipSlots(this ItemTemplate t, Class playerClass, bool canDualWield)
    {
        byte n = InventorySlots.NullSlot;
        return t.GetInventoryType() switch
        {
            InventoryType.Head => [InventorySlots.Head],
            InventoryType.Neck => [InventorySlots.Neck],
            InventoryType.Shoulders => [InventorySlots.Shoulders],
            InventoryType.Body => [InventorySlots.Body],
            InventoryType.Chest or InventoryType.Robe => [InventorySlots.Chest],
            InventoryType.Waist => [InventorySlots.Waist],
            InventoryType.Legs => [InventorySlots.Legs],
            InventoryType.Feet => [InventorySlots.Feet],
            InventoryType.Wrists => [InventorySlots.Wrists],
            InventoryType.Hands => [InventorySlots.Hands],
            InventoryType.Finger => [InventorySlots.Finger1, InventorySlots.Finger2],
            InventoryType.Trinket => [InventorySlots.Trinket1, InventorySlots.Trinket2],
            InventoryType.Cloak => [InventorySlots.Back],
            InventoryType.Weapon => [InventorySlots.MainHand, canDualWield ? InventorySlots.OffHand : n],
            InventoryType.Shield or InventoryType.WeaponOffHand or InventoryType.Holdable => [InventorySlots.OffHand],
            InventoryType.Ranged or InventoryType.Thrown or InventoryType.RangedRight => [InventorySlots.Ranged],
            InventoryType.TwoHandWeapon or InventoryType.WeaponMainHand => [InventorySlots.MainHand],
            InventoryType.Tabard => [InventorySlots.Tabard],
            InventoryType.Bag => [InventorySlots.BagStart, InventorySlots.BagStart + 1, InventorySlots.BagStart + 2, InventorySlots.BagStart + 3],
            InventoryType.Relic => [RelicSlot(t.SubClass, playerClass)],
            _ => [],
        };
    }

    /// <summary>
    /// vmangos ItemCanGoIntoBag (Item.cpp): a special bag never takes another bag; general
    /// containers take anything; soul/herb/enchanting bags, quivers and ammo pouches take only
    /// their bag family.
    /// </summary>
    public static bool CanGoIntoBag(this ItemTemplate item, ItemTemplate bag)
    {
        if (bag.BagFamily != (uint)BagFamily.None && item.ContainerSlots != 0)
        {
            return false;
        }

        var family = (BagFamily)item.BagFamily;
        return (ItemClass)bag.Class switch
        {
            ItemClass.Container => bag.SubClass switch
            {
                ItemSubClasses.Container => true,
                ItemSubClasses.SoulContainer => family == BagFamily.SoulShards,
                ItemSubClasses.HerbContainer => family == BagFamily.Herbs,
                ItemSubClasses.EnchantingContainer => family == BagFamily.EnchantingSupplies,
                _ => false,
            },
            ItemClass.Quiver => bag.SubClass switch
            {
                ItemSubClasses.QuiverQuiver => family == BagFamily.Arrows,
                ItemSubClasses.QuiverAmmoPouch => family == BagFamily.Bullets,
                _ => false,
            },
            _ => false,
        };
    }

    /// <summary>A general-purpose bag (vmangos _CanStoreItem_InBag "non_specialized": container class, sub-class 0).</summary>
    public static bool IsGeneralBag(this ItemTemplate bag)
        => (ItemClass)bag.Class == ItemClass.Container && bag.SubClass == ItemSubClasses.Container;

    private static byte RelicSlot(uint subClass, Class playerClass) => (subClass, playerClass) switch
    {
        (ItemSubClasses.ArmorLibram, Class.Paladin) => InventorySlots.Ranged,
        (ItemSubClasses.ArmorIdol, Class.Druid) => InventorySlots.Ranged,
        (ItemSubClasses.ArmorTotem, Class.Shaman) => InventorySlots.Ranged,
        (ItemSubClasses.ArmorMisc, Class.Warlock) => InventorySlots.Ranged,
        _ => InventorySlots.NullSlot,
    };
}
