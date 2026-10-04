using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items.ItemSets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items.ItemUse;

/// <summary>
/// ON_EQUIP item spells and item set bonuses, driven by the inventory's one equip/unequip hook (mangos Player::ApplyItemEquipSpell and
/// ApplyEquipSpell, PlayerItemApply.cpp, with AddItemsSetItem / RemoveItemsSetItem, Item.cpp).
/// <list type="bullet">
/// <item>An item's "Equip:" spells (<see cref="ItemSpellTriggers.OnEquip"/>) are cast triggered on the wearer with the item as cast item
/// when the item starts counting as worn (<see cref="EquipmentChange.ModsApplied"/>: equipped and not broken, or repaired), so the aura
/// knows its item. When the item stops counting (taken off, or broken) every aura of every spell of the item that came from it is
/// removed, except an on-use spell with negative charges (an expendable's own use stays, mangos ApplyItemEquipSpell).</item>
/// <item>Set pieces are counted at <see cref="EquipmentChange.Worn"/> / <see cref="EquipmentChange.Removed"/>, independent of breakage
/// (see <see cref="ItemSetBonuses"/>).</item>
/// <item>The four equipped bag slots take part too (mangos applies for every slot below BAG_END), except quivers and ammo pouches, whose
/// haste aura belongs to <c>QuiverHaste</c> (it has its own aura-stacking guard and condition on the ranged weapon).</item>
/// </list>
/// Everything runs on the world thread inside the inventory operation. State is one <see cref="PlayerItemSets"/> and two delegates per
/// player, created at <see cref="Attach"/>; nothing allocates per tick.
/// </summary>
public sealed class ItemEquipSpells(SpellSystem spells, ItemSetBonuses? sets = null)
{
    private static readonly ConditionalWeakTable<PlayerInventory, Binding> s_bindings = new();

    private readonly ItemSetBonuses? _sets = sets;
    private readonly SpellSystem _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    /// <summary>
    /// Follow the player's equipment from now on and apply what is already worn (login, on the world thread, after the equipment and the
    /// saved auras were restored). A second call for the same inventory replaces the first binding, so a replay can never stack.
    /// </summary>
    public void Attach(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerInventory inventory = player.Inventory;
        if (s_bindings.TryGetValue(inventory, out Binding? previous))
        {
            inventory.EquipmentChanged -= previous.OnEquipment;
            inventory.BagEquipChanged -= previous.OnBag;
            DetachState(player, previous);
            s_bindings.Remove(inventory);
        }

        var binding = new Binding(this, player);
        s_bindings.Add(inventory, binding);
        inventory.EquipmentChanged += binding.OnEquipment;
        inventory.BagEquipChanged += binding.OnBag;

        // vmangos _ApplyAllItemMods order: sets for every worn piece (broken too), then the spells of the unbroken ones.
        foreach ((byte slot, Item item) in inventory.Equipped)
        {
            _sets?.ItemWorn(player, binding.Sets, item, replay: true);
        }

        foreach ((byte slot, Item item) in inventory.Equipped)
        {
            if (!IsBroken(item))
            {
                ApplyItem(player, item, replay: true);
            }
        }

        for (byte slot = InventorySlots.BagStart; slot < InventorySlots.BagEnd; slot++)
        {
            if (inventory.GetItem(InventorySlots.Bag0, slot) is { } bag)
            {
                OnBagChanged(player, bag, equipped: true, replay: true);
            }
        }
    }

    /// <summary>The set bonuses <paramref name="inventory"/>'s owner has (null before <see cref="Attach"/>).</summary>
    public static PlayerItemSets? SetsOf(PlayerInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return s_bindings.TryGetValue(inventory, out Binding? binding) ? binding.Sets : null;
    }

    private static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;

    private void DetachState(Player player, Binding binding)
    {
        // A re-attach starts counting from the worn items again: take the old bonuses off first so their auras do not linger.
        foreach ((byte slot, Item item) in player.Inventory.Equipped)
        {
            _sets?.ItemRemoved(player, binding.Sets, item);
        }
    }

    private void ApplyItem(Player player, Item item, bool replay)
    {
        foreach (ItemSpell itemSpell in item.Template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != ItemSpellTriggers.OnEquip)
            {
                continue;
            }

            if (replay)
            {
                _spells.RemoveAuras(player, itemSpell.SpellId);   // a restored aura has no item: do not stack on it
            }
            else
            {
                _spells.RemoveAurasDueToItemSpell(player, item, itemSpell.SpellId);
            }

            _spells.CastItemSpell(player, item, itemSpell.SpellId, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    private void RemoveItem(Player player, Item item)
    {
        foreach (ItemSpell itemSpell in item.Template.Spells)
        {
            if (itemSpell.SpellId == 0 || (itemSpell.Trigger == ItemSpellTriggers.OnUse && itemSpell.Charges < 0))
            {
                continue;
            }

            _spells.RemoveAurasDueToItemSpell(player, item, itemSpell.SpellId);
        }
    }

    private void OnBagChanged(Player player, Item bag, bool equipped, bool replay = false)
    {
        if (bag.Template.Class == (uint)ItemClass.Quiver)
        {
            return;
        }

        if (equipped)
        {
            ApplyItem(player, bag, replay);
        }
        else
        {
            RemoveItem(player, bag);
        }
    }

    private sealed class Binding
    {
        private readonly ItemEquipSpells _owner;
        private readonly Player _player;

        public Binding(ItemEquipSpells owner, Player player)
        {
            _owner = owner;
            _player = player;
        }

        public PlayerItemSets Sets { get; } = new();

        public void OnEquipment(Item item, byte slot, EquipmentChange change)
        {
            switch (change)
            {
                case EquipmentChange.Worn:
                    _owner._sets?.ItemWorn(_player, Sets, item, replay: false);
                    break;
                case EquipmentChange.Removed:
                    _owner._sets?.ItemRemoved(_player, Sets, item);
                    break;
                case EquipmentChange.ModsApplied:
                    _owner.ApplyItem(_player, item, replay: false);
                    break;
                case EquipmentChange.ModsRemoved:
                    _owner.RemoveItem(_player, item);
                    break;
            }
        }

        public void OnBag(Item item, byte slot, bool equipped) => _owner.OnBagChanged(_player, item, equipped);
    }
}
