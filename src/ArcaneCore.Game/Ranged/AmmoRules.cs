using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Ranged;

/// <summary>The kind of a ranged weapon, by item sub-class (vmangos ItemPrototype.h ItemSubclassWeapon).</summary>
public enum RangedWeaponKind
{
    /// <summary>Not a ranged weapon (or no weapon).</summary>
    None = 0,

    /// <summary>ITEM_SUBCLASS_WEAPON_BOW (2): takes arrows.</summary>
    Bow,

    /// <summary>ITEM_SUBCLASS_WEAPON_GUN (3): takes bullets.</summary>
    Gun,

    /// <summary>ITEM_SUBCLASS_WEAPON_THROWN (16): the weapon itself is the projectile.</summary>
    Thrown,

    /// <summary>ITEM_SUBCLASS_WEAPON_CROSSBOW (18): takes arrows.</summary>
    Crossbow,

    /// <summary>ITEM_SUBCLASS_WEAPON_WAND (19): needs no projectile.</summary>
    Wand,
}

/// <summary>
/// Ammunition rules, a port of vmangos Player::CanUseAmmo (template half), Player::
/// CheckAmmoCompatibility / _ApplyAmmoBonuses (Player.cpp:7514-7569, 10100-10123) and the
/// weapon/ammo matrix of Spell::CheckCast (Spell.cpp:7390-7455) and Spell::TakeAmmo
/// (Spell.cpp:5129-5171). Pure functions of item templates.
/// </summary>
public static class AmmoRules
{
    /// <summary>ITEM_SUBCLASS_ARROW.</summary>
    public const uint ArrowSubClass = 2;

    /// <summary>ITEM_SUBCLASS_BULLET.</summary>
    public const uint BulletSubClass = 3;

    /// <summary>ITEM_FLAG_EXOTIC (ItemPrototype.h:67).</summary>
    public const uint ExoticFlag = 0x00000008;

    /// <summary>
    /// Ranged spells that take no ammunition (Spell::TakeAmmo): Blind 2094, Net-o-Matic 13099 and
    /// 13119, Expose Weakness 23577.
    /// </summary>
    public static IReadOnlySet<uint> NoAmmoSpellIds { get; } = new HashSet<uint> { 2094, 13099, 13119, 23577 };

    /// <summary>The ranged weapon kind of <paramref name="weapon"/> (None unless it is an ITEM_CLASS_WEAPON bow, gun, thrown, crossbow or wand).</summary>
    public static RangedWeaponKind Classify(ItemTemplate? weapon)
    {
        if (weapon is null || weapon.Class != (uint)ItemClass.Weapon)
        {
            return RangedWeaponKind.None;
        }

        return weapon.SubClass switch
        {
            2 => RangedWeaponKind.Bow,
            3 => RangedWeaponKind.Gun,
            16 => RangedWeaponKind.Thrown,
            18 => RangedWeaponKind.Crossbow,
            19 => RangedWeaponKind.Wand,
            _ => RangedWeaponKind.None,
        };
    }

    /// <summary>Bows, crossbows and guns draw from the ammo slot (Spell::CheckCast: thrown and wands do not).</summary>
    public static bool UsesAmmoSlot(RangedWeaponKind kind) => kind is RangedWeaponKind.Bow or RangedWeaponKind.Crossbow or RangedWeaponKind.Gun;

    /// <summary>Player::CanUseAmmo (template half): the item must have inventory type INVTYPE_AMMO.</summary>
    public static bool IsAmmoItem(ItemTemplate? item) => item is not null && item.GetInventoryType() == InventoryType.Ammo;

    /// <summary>The item is class ITEM_CLASS_PROJECTILE (arrows, bullets).</summary>
    public static bool IsProjectile(ItemTemplate? item) => item is not null && item.Class == (uint)ItemClass.Projectile;

    /// <summary>The item carries ITEM_FLAG_EXOTIC (what a spell with SPELL_ATTR_NEED_EXOTIC_AMMO demands).</summary>
    public static bool IsExotic(ItemTemplate? item) => item is not null && (item.Flags & ExoticFlag) != 0;

    /// <summary>
    /// Player::CheckAmmoCompatibility and the sub-class matrix of Spell::CheckCast: bows and
    /// crossbows take arrows, guns take bullets, every other weapon takes nothing. The ammo must
    /// be an ITEM_CLASS_PROJECTILE.
    /// </summary>
    public static bool AmmoMatchesWeapon(RangedWeaponKind weapon, ItemTemplate? ammo)
    {
        if (ammo is null || !IsProjectile(ammo))
        {
            return false;
        }

        return weapon switch
        {
            RangedWeaponKind.Bow or RangedWeaponKind.Crossbow => ammo.SubClass == ArrowSubClass,
            RangedWeaponKind.Gun => ammo.SubClass == BulletSubClass,
            _ => false,
        };
    }

    /// <summary>The damage per second a projectile adds to ranged damage: (min + max) / 2 of its first damage entry (Player.cpp:7527).</summary>
    public static float AmmoDps(ItemTemplate? ammo)
        => ammo is null || ammo.Damages.Count == 0 ? 0.0f : (ammo.Damages[0].Min + ammo.Damages[0].Max) / 2.0f;
}
