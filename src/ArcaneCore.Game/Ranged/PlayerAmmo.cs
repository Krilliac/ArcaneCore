using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The ranged lane's view of a player's ammunition slot (PLAYER_AMMO_ID). The state and its rules are
/// <b>one implementation</b>, the item-mechanics lane's <see cref="PlayerInventory"/> (vmangos Player::CanUseAmmo /
/// SetAmmo / RemoveAmmo, Player.cpp:10100-10158; _ApplyAmmoBonuses / CheckAmmoCompatibility, :7514-7569); this class
/// only keeps the hunter lane's call shapes (player in, result out) and the ranged-weapon lookup.
/// <para>
/// The ammo DPS is derived on every read (<see cref="CurrentDps"/>) so it can never be stale; the stats area adds
/// it to the ranged damage fields (vmangos StatSystem.cpp:440-443: <c>+= ammoDps * attackSpeed</c>).
/// Persistence (the <c>character_item_state</c> table), CMSG_SET_AMMO and the starting ammo belong to the items lane.
/// </para>
/// </summary>
public static class PlayerAmmo
{
    /// <summary>The item entry in PLAYER_AMMO_ID (0 = none). The field is not cleared when the stack runs out, as in vmangos.</summary>
    public static uint CurrentAmmoId(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Inventory.AmmoId;
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

    /// <summary>vmangos Player::CanUseAmmo (see <see cref="PlayerInventory.CanUseAmmo"/>).</summary>
    public static InventoryResult CanUseAmmo(Player player, uint item)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Inventory.CanUseAmmo(item);
    }

    /// <summary>
    /// vmangos Player::SetAmmo: nothing for 0 or the ammo already set; a refusal is sent to the
    /// client as an equip error and leaves the field alone; otherwise the field is written.
    /// Returns true when the field changed.
    /// </summary>
    public static bool SetAmmo(Player player, uint item)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint before = player.Inventory.AmmoId;
        player.Inventory.SetAmmo(item);
        return player.Inventory.AmmoId != before;
    }

    /// <summary>vmangos Player::RemoveAmmo: clear the ammo field. Returns true when it was set.</summary>
    public static bool RemoveAmmo(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        bool had = player.Inventory.AmmoId != 0;
        player.Inventory.RemoveAmmo();
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
        return player.Inventory.AmmoDps;
    }
}
