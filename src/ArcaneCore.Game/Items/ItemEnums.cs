namespace ArcaneCore.Game.Items;

/// <summary>Item inventory types (vmangos ItemPrototype.h InventoryType).</summary>
public enum InventoryType : uint
{
    NonEquip = 0,
    Head = 1,
    Neck = 2,
    Shoulders = 3,
    Body = 4,
    Chest = 5,
    Waist = 6,
    Legs = 7,
    Feet = 8,
    Wrists = 9,
    Hands = 10,
    Finger = 11,
    Trinket = 12,
    Weapon = 13,
    Shield = 14,
    Ranged = 15,
    Cloak = 16,
    TwoHandWeapon = 17,
    Bag = 18,
    Tabard = 19,
    Robe = 20,
    WeaponMainHand = 21,
    WeaponOffHand = 22,
    Holdable = 23,
    Ammo = 24,
    Thrown = 25,
    RangedRight = 26,
    Quiver = 27,
    Relic = 28,
}

/// <summary>Item classes (vmangos ItemPrototype.h ItemClass).</summary>
public enum ItemClass : uint
{
    Consumable = 0,
    Container = 1,
    Weapon = 2,
    Gem = 3,
    Armor = 4,
    Reagent = 5,
    Projectile = 6,
    TradeGoods = 7,
    Generic = 8,
    Recipe = 9,
    Money = 10,
    Quiver = 11,
    Quest = 12,
    Key = 13,
    Permanent = 14,
    Junk = 15,
}

/// <summary>Sub-classes this area needs (vmangos ItemPrototype.h ItemSubclass* enums).</summary>
public static class ItemSubClasses
{
    public const uint Container = 0;
    public const uint SoulContainer = 1;
    public const uint HerbContainer = 2;
    public const uint EnchantingContainer = 3;

    public const uint QuiverQuiver = 2;
    public const uint QuiverAmmoPouch = 3;

    public const uint WeaponFist = 13;

    public const uint ArmorMisc = 0;
    public const uint ArmorShield = 6;
    public const uint ArmorLibram = 7;
    public const uint ArmorIdol = 8;
    public const uint ArmorTotem = 9;
}

/// <summary>Bag families (vmangos ItemPrototype.h BagFamily).</summary>
public enum BagFamily : uint
{
    None = 0,
    Arrows = 1,
    Bullets = 2,
    SoulShards = 3,
    Herbs = 6,
    EnchantingSupplies = 7,
    EngineeringSupplies = 8,
    Keys = 9,
}

/// <summary>Binding types (vmangos ItemPrototype.h ItemBondingType).</summary>
public enum ItemBonding : uint
{
    None = 0,
    WhenPickedUp = 1,
    WhenEquipped = 2,
    WhenUse = 3,
    QuestItem = 4,
}

/// <summary>item_template.flags (vmangos ItemPrototype.h:64-84 ItemPrototypeFlags; UniqueEquipped is a vmangos server-side extension).</summary>
[Flags]
public enum ItemTemplateFlags : uint
{
    None = 0,
    NoPickup = 0x00000001,
    Conjured = 0x00000002,
    Lootable = 0x00000004,
    Exotic = 0x00000008,
    Deprecated = 0x00000010,
    Indestructible = 0x00000020,
    PlayerCast = 0x00000040,
    NoEquipCooldown = 0x00000080,
    IntBonusInstead = 0x00000100,
    Wrapper = 0x00000200,
    IgnoreBagSpace = 0x00000400,
    PartyLoot = 0x00000800,
    BriefSpellEffects = 0x00001000,
    Charter = 0x00002000,
    HasText = 0x00004000,
    NoDisenchant = 0x00008000,
    RealDuration = 0x00010000,
    NoCreator = 0x00020000,
    UniqueEquipped = 0x00080000,
}

/// <summary>ITEM_FIELD_FLAGS bits (vmangos ItemDefines.h ItemDynFlags).</summary>
[Flags]
public enum ItemDynFlags : uint
{
    None = 0,
    Bound = 0x00000001,
    Translated = 0x00000002,
    Unlocked = 0x00000004,
    Wrapped = 0x00000008,
    Readable = 0x00000200,
}

/// <summary>Stat types of item_template stat_typeN (vmangos ItemPrototype.h ItemModType).</summary>
public enum ItemStatType : uint
{
    Mana = 0,
    Health = 1,
    Agility = 3,
    Strength = 4,
    Intellect = 5,
    Spirit = 6,
    Stamina = 7,
}

/// <summary>Skill-line ids an item's proficiency maps to (vmangos SharedDefines.h SkillType).</summary>
public static class ItemSkills
{
    public const uint Swords = 43;
    public const uint Axes = 44;
    public const uint Bows = 45;
    public const uint Guns = 46;
    public const uint Maces = 54;
    public const uint TwoHandedSwords = 55;
    public const uint Staves = 136;
    public const uint TwoHandedMaces = 160;
    public const uint Unarmed = 162;
    public const uint TwoHandedAxes = 172;
    public const uint Daggers = 173;
    public const uint Thrown = 176;
    public const uint Crossbows = 226;
    public const uint Wands = 228;
    public const uint Polearms = 229;
    public const uint Assassination = 253;
    public const uint PlateMail = 293;
    public const uint Fishing = 356;
    public const uint Mail = 413;
    public const uint Leather = 414;
    public const uint Cloth = 415;
    public const uint Shield = 433;
    public const uint FistWeapons = 473;
}
