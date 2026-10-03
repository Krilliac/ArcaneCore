using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The ammunition slot of a player: PLAYER_AMMO_ID and the ammo damage per second derived from
/// it. A port of vmangos Player::CanUseAmmo / SetAmmo / RemoveAmmo (Player.cpp:10100-10158) and
/// _ApplyAmmoBonuses / CheckAmmoCompatibility (Player.cpp:7514-7569).
/// <para>
/// vmangos caches the ammo DPS in <c>m_ammoDPS</c> and refreshes it whenever the ammo or the
/// ranged weapon changes. Here the value is derived from the update field and the equipped weapon
/// on each call (<see cref="CurrentDps"/>), so it can never be stale; the stats area adds it to
/// the ranged damage fields (vmangos StatSystem.cpp:440-443: <c>+= ammoDps * attackSpeed</c>).
/// </para>
/// </summary>
public static class PlayerAmmo
{
    /// <summary>The item entry in PLAYER_AMMO_ID (0 = none). The field is not cleared when the stack runs out, as in vmangos.</summary>
    public static uint CurrentAmmoId(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.GetUInt32(UpdateFields.PlayerAmmoId);
    }

    /// <summary>
    /// The ranged-slot weapon (vmangos Player::GetWeaponForAttack(RANGED_ATTACK, nonbroken, false)):
    /// a broken item (durability 0 of a non-zero maximum) is skipped when <paramref name="nonBroken"/> is set.
    /// </summary>
    public static Item? RangedWeapon(Player player, bool nonBroken)
    {
        ArgumentNullException.ThrowIfNull(player);
        Item? item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged);
        if (item is null || (nonBroken && item.MaxDurability > 0 && item.Durability == 0))
        {
            return null;
        }

        return item;
    }

    /// <summary>
    /// vmangos Player::CanUseAmmo: dead players are refused, the item must exist, have inventory
    /// type INVTYPE_AMMO, and pass the equipment requirements (class, race, skill, level ...).
    /// </summary>
    public static InventoryResult CanUseAmmo(Player player, uint item)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsAlive)
        {
            return InventoryResult.YouAreDead;
        }

        ItemTemplate? template = player.Inventory.Templates.Find(item);
        if (template is null)
        {
            return InventoryResult.ItemNotFound;
        }

        return AmmoRules.IsAmmoItem(template) ? player.Inventory.CanUseItem(template) : InventoryResult.OnlyAmmoCanGoHere;
    }

    /// <summary>
    /// vmangos Player::SetAmmo: nothing for 0 or the ammo already set; a refusal is sent to the
    /// client as an equip error and leaves the field alone; otherwise the field is written.
    /// Returns true when the field changed.
    /// </summary>
    public static bool SetAmmo(Player player, uint item)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (item == 0 || CurrentAmmoId(player) == item)
        {
            return false;
        }

        InventoryResult result = CanUseAmmo(player, item);
        if (result != InventoryResult.Ok)
        {
            player.Inventory.SendEquipError(result, null, null, 0, item);
            return false;
        }

        player.SetUInt32(UpdateFields.PlayerAmmoId, item);
        return true;
    }

    /// <summary>vmangos Player::RemoveAmmo: clear the ammo field. Returns true when it was set.</summary>
    public static bool RemoveAmmo(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        bool had = CurrentAmmoId(player) != 0;
        player.SetUInt32(UpdateFields.PlayerAmmoId, 0);
        return had;
    }

    /// <summary>
    /// The ammo DPS currently in effect (vmangos m_ammoDPS): (min + max) / 2 of the ammo's first
    /// damage entry when the ammo is a projectile that fits the equipped, unbroken ranged weapon
    /// (Player::CheckAmmoCompatibility), else 0.
    /// </summary>
    public static float CurrentDps(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint ammoId = CurrentAmmoId(player);
        if (ammoId == 0)
        {
            return 0.0f;
        }

        ItemTemplate? ammo = player.Inventory.Templates.Find(ammoId);
        RangedWeaponKind weapon = AmmoRules.Classify(RangedWeapon(player, nonBroken: true)?.Template);
        return AmmoRules.IsProjectile(ammo) && AmmoRules.AmmoMatchesWeapon(weapon, ammo) ? AmmoRules.AmmoDps(ammo) : 0.0f;
    }

    /// <summary>
    /// The ammo a new character starts with (vmangos Player::AddStartingItems, Player.cpp:560-575):
    /// among the starting items that are not worn, the last one the character may use as ammo
    /// (<c>CanUseAmmo</c>: INVTYPE_AMMO and the equipment requirements of a fresh character).
    /// Returns 0 when there is none.
    /// </summary>
    public static uint SelectStartingAmmo(IItemTemplateStore templates, byte race, byte cls, byte level)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var offline = new PlayerInventory(ObjectGuid.Empty, (Race)race, (Class)cls, level) { Templates = templates };
        uint selected = 0;
        foreach (StartingItem starting in templates.StartingItems(race, cls))
        {
            ItemTemplate? template = templates.Find(starting.ItemId);
            if (AmmoRules.IsAmmoItem(template) && offline.CanUseItem(template!) == InventoryResult.Ok)
            {
                selected = starting.ItemId;
            }
        }

        return selected;
    }
}
