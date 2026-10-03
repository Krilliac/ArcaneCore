using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// Quivers and ammo pouches speed up ranged weapons (ranged (autorepeat lane)): the 24 quivers and pouches of classic-db carry an
/// ON_EQUIP item spell (14824-14829: aura 141 MOD_RANGED_AMMO_HASTE, +10 to +15 percent). vmangos casts a worn item's ON_EQUIP spells
/// when it is equipped and removes them when it is unequipped (Player::ApplyEquipSpell, called from _ApplyItemMods for every slot below
/// BAG_END, so for the four bag slots too, Player.cpp:6828-6833). This class is that rule for quivers only: a general ON_EQUIP engine
/// (weapon and armor "Equip:" spells) is a recorded limit of the lane.
/// <para>
/// The aura handler (<see cref="AttackSpeedAuras"/>) decides whether the haste applies: only to a player whose ranged weapon takes
/// ammunition, evaluated once when the aura is applied (thrown weapons carry ammo_type 4 and so do benefit; wands and no weapon do not).
/// <see cref="Attach"/> removes a leftover aura before casting, so a replay at login can never stack it.
/// </para>
/// </summary>
public sealed class QuiverHaste(SpellSystem spells)
{
    /// <summary>ITEM_SPELLTRIGGER_ON_EQUIP.</summary>
    private const uint OnEquipTrigger = 1;

    private static readonly ConditionalWeakTable<PlayerInventory, Action<Item, byte, bool>> s_subscriptions = new();

    private readonly SpellSystem _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    /// <summary>
    /// Follow the player's bag slots from now on and apply the quiver worn at this moment (login: after the equipment loaded, on the
    /// world thread, so the ranged weapon is already in its slot as vmangos' ascending slot order guarantees).
    /// </summary>
    public void Attach(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerInventory inventory = player.Inventory;
        if (s_subscriptions.TryGetValue(inventory, out Action<Item, byte, bool>? previous))
        {
            inventory.BagEquipChanged -= previous;
            s_subscriptions.Remove(inventory);
        }

        void Handler(Item item, byte slot, bool equipped) => Apply(player, item, equipped);
        s_subscriptions.Add(inventory, Handler);
        inventory.BagEquipChanged += Handler;
        inventory.ReplayBagEquips();
    }

    private void Apply(Player player, Item item, bool equipped)
    {
        if (item.Template.Class != (uint)ItemClass.Quiver)
        {
            return;
        }

        foreach (ItemSpell itemSpell in item.Template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != OnEquipTrigger)
            {
                continue;
            }

            _spells.RemoveAuras(player, itemSpell.SpellId);
            if (equipped)
            {
                _spells.CastSpell(player, itemSpell.SpellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
    }
}
