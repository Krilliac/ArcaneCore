using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>
/// The player's ammo selection (PLAYER_AMMO_ID) and its rules: vmangos Player::CanUseAmmo,
/// SetAmmo, RemoveAmmo (Player.cpp:10100-10160), CheckAmmoCompatibility and the ammo DPS
/// (7514-7570), Spell::TakeAmmo (Spell.cpp:5130-5175) and Spell::WriteAmmoToPacket (4563-4587).
/// </summary>
public sealed partial class PlayerInventory
{
    /// <summary>Weapon sub-classes (vmangos ItemSubclassWeapon) the ammo rules look at.</summary>
    private const uint WeaponBow = 2;
    private const uint WeaponGun = 3;
    private const uint WeaponCrossbow = 18;
    private const uint WeaponWand = 19;

    /// <summary>Projectile sub-classes (vmangos ItemSubclassProjectile).</summary>
    private const uint ProjectileArrow = 2;
    private const uint ProjectileBullet = 3;

    /// <summary>Ranged attacks that take no ammo (Spell.cpp:5139-5147: Blind, Net-o-Matic x2, Expose Weakness).</summary>
    private static readonly HashSet<uint> AmmoExemptSpells = [2094, 13099, 13119, 23577];

    private uint _ammoId; // used only while the inventory has no player (shadow inventories)

    /// <summary>PLAYER_AMMO_ID: the selected ammo item entry (0 = none).</summary>
    public uint AmmoId => Player?.GetUInt32(UpdateFields.PlayerAmmoId) ?? _ammoId;

    /// <summary>
    /// Raised after PLAYER_AMMO_ID changed (ranged (autorepeat lane)): the ammo DPS is part of the ranged damage fields (vmangos
    /// Player::_ApplyAmmoBonuses, Player.cpp:7514-7535), so the stat system recomputes them.
    /// </summary>
    public event Action<PlayerInventory>? AmmoChanged;

    private void WriteAmmoId(uint entry)
    {
        uint before = AmmoId;
        if (Player is { } player)
        {
            player.SetUInt32(UpdateFields.PlayerAmmoId, entry);
        }
        else
        {
            _ammoId = entry;
        }

        if (before != entry)
        {
            AmmoChanged?.Invoke(this);
        }
    }

    /// <summary>Put a stored ammo selection back without checks (vmangos LoadFromDB, Player.cpp:14703).</summary>
    public void RestoreAmmo(uint entry) => WriteAmmoId(entry);

    /// <summary>vmangos Player::CanUseAmmo (Player.cpp:10100-10123).</summary>
    public InventoryResult CanUseAmmo(uint entry)
    {
        if (Player is { IsAlive: false })
        {
            return InventoryResult.YouAreDead;
        }

        if (Templates.Find(entry) is not { } template)
        {
            return InventoryResult.ItemNotFound;
        }

        if (template.InventoryType != (uint)InventoryType.Ammo)
        {
            return InventoryResult.OnlyAmmoCanGoHere;
        }

        return CanUseItem(template);
    }

    /// <summary>vmangos Player::SetAmmo (Player.cpp:10125-10148): a refused ammo is reported with SMSG_INVENTORY_CHANGE_FAILURE.</summary>
    public void SetAmmo(uint entry)
    {
        if (entry == 0 || AmmoId == entry)
        {
            return;
        }

        InventoryResult msg = CanUseAmmo(entry);
        if (msg != InventoryResult.Ok)
        {
            SendEquipError(msg, null, null, 0, entry);
            return;
        }

        WriteAmmoId(entry);
    }

    /// <summary>vmangos Player::RemoveAmmo (Player.cpp:10150-10158).</summary>
    public void RemoveAmmo() => WriteAmmoId(0);

    /// <summary>
    /// vmangos Player::CheckAmmoCompatibility (Player.cpp:7538-7570): a non-broken ranged weapon
    /// that is a bow or crossbow takes arrows, one that is a gun takes bullets; nothing else
    /// (wands, thrown) takes ammo.
    /// </summary>
    public bool CheckAmmoCompatibility(ItemTemplate ammo)
    {
        ArgumentNullException.ThrowIfNull(ammo);
        if (RangedWeapon(nonBroken: true) is not { } weapon)
        {
            return false;
        }

        return weapon.Template.SubClass switch
        {
            WeaponBow or WeaponCrossbow => ammo.SubClass == ProjectileArrow,
            WeaponGun => ammo.SubClass == ProjectileBullet,
            _ => false,
        };
    }

    /// <summary>
    /// The ammo's contribution to ranged damage per second (vmangos m_ammoDPS, Player::_ApplyAmmoBonuses
    /// Player.cpp:7514-7535): the average of the ammo's first damage entry when the selected ammo
    /// fits the equipped ranged weapon, else 0. Computed from the current equipment, so unlike
    /// vmangos' cached value it can never be stale; the stats lane reads it for ranged damage.
    /// </summary>
    public float AmmoDps
    {
        get
        {
            uint ammoId = AmmoId;
            if (ammoId == 0 || Templates.Find(ammoId) is not { } ammo
                || (ItemClass)ammo.Class != ItemClass.Projectile || !CheckAmmoCompatibility(ammo))
            {
                return 0f;
            }

            ItemDamage damage = ammo.Damages.Count > 0 ? ammo.Damages[0] : default;
            return (damage.Min + damage.Max) / 2f;
        }
    }

    /// <summary>
    /// vmangos Spell::TakeAmmo (Spell.cpp:5130-5175) for a ranged attack cast by this player: wands
    /// and the exempt spells take nothing; a thrown weapon loses one from its stack, or one
    /// durability point when it does not stack; any other ranged weapon uses one of the selected ammo.
    /// </summary>
    public void ConsumeRangedAmmo(uint spellId)
    {
        if (AmmoExemptSpells.Contains(spellId) || RangedWeapon(nonBroken: true) is not { } weapon
            || weapon.Template.SubClass == WeaponWand)
        {
            return;
        }

        if (weapon.Template.InventoryType == (uint)InventoryType.Thrown)
        {
            if (weapon.Template.MaxStackSize() == 1)
            {
                DurabilityPointLossForEquipSlot(InventorySlots.Ranged);
            }
            else
            {
                DestroyItemCount(weapon, 1);
            }
        }
        else if (AmmoId is var ammo and not 0)
        {
            DestroyItemCount(ammo, 1);
        }
    }

    /// <summary>
    /// The projectile shown in a ranged cast's SMSG_SPELL_START / SMSG_SPELL_GO (vmangos
    /// Spell::WriteAmmoToPacket, Spell.cpp:4563-4587): a thrown weapon shows itself, otherwise the
    /// selected ammo; false (zeros) when no ranged weapon is equipped.
    /// </summary>
    public bool TryGetAmmoVisual(out uint displayId, out uint inventoryType)
    {
        displayId = 0;
        inventoryType = 0;
        if (RangedWeapon(nonBroken: false) is not { } weapon)
        {
            return false;
        }

        inventoryType = weapon.Template.InventoryType;
        if (inventoryType == (uint)InventoryType.Thrown)
        {
            displayId = weapon.Template.DisplayId;
        }
        else if (AmmoId is var ammoId and not 0 && Templates.Find(ammoId) is { } ammo)
        {
            displayId = ammo.DisplayId;
            inventoryType = ammo.InventoryType;
        }

        return true;
    }

    /// <summary>vmangos GetWeaponForAttack(RANGED_ATTACK): the item in the ranged slot when it is a weapon (and, optionally, not broken).</summary>
    private Item? RangedWeapon(bool nonBroken)
    {
        Item? item = _items[InventorySlots.Ranged];
        if (item is null || (ItemClass)item.Template.Class != ItemClass.Weapon)
        {
            return null;
        }

        return nonBroken && item.MaxDurability > 0 && item.Durability == 0 ? null : item;
    }
}
