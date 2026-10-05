using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly Stack<ItemUseDispatch> _itemUseDispatches = [];
    /// <summary>
    /// CMSG_USE_ITEM entry point. The selected on-use template spell is validated, then every
    /// on-use spell is dispatched in template order: first normal, later triggered (vmangos
    /// Player.cpp:7362-7403). The live item is retained through the regular cast pipeline.
    /// </summary>
    public SpellCastResult HandleItemUse(Player player, byte bag, byte slot, byte spellIndex, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(targets);
        Item? item = player.Inventory.GetItem(bag, slot);
        if (item is null || spellIndex >= item.Template.Spells.Count)
        {
            return SpellCastResult.ItemNotReady;
        }
        if (!CanStartItemUse(player, item, spellIndex, out SpellCastResult eligibility)) return eligibility;

        if (item.Template.Bonding is (uint)ItemBonding.WhenUse or (uint)ItemBonding.WhenPickedUp or (uint)ItemBonding.QuestItem)
        {
            item.SetBinding(true);
        }

        var dispatch = new ItemUseDispatch(item);
        _itemUseDispatches.Push(dispatch);
        try
        {
            SpellCastResult first = SpellCastResult.NotFound;
            bool ran = false;
            for (byte index = 0; index < item.Template.Spells.Count; index++)
            {
                ItemSpell itemSpell = item.Template.Spells[index];
                if (itemSpell.SpellId == 0 || itemSpell.Trigger != 0 || Store.Get(itemSpell.SpellId) is not { } spell) continue;
                SpellCastResult result = Prepare(player, spell, targets, triggered: ran, castItem: item, itemSpellIndex: index,
                    itemCooldownMs: itemSpell.Cooldown, itemCategoryCooldownMs: itemSpell.CategoryCooldown,
                    itemCategory: itemSpell.Category == 0 ? null : itemSpell.Category);
                if (!ran) first = result;
                ran = true;
            }
            return ran ? first : SpellCastResult.ItemNotReady;
        }
        finally
        {
            _itemUseDispatches.Pop();
            if (dispatch.PendingConsume && !dispatch.ReagentCleared && ReferenceEquals(item.Inventory, player.Inventory))
                player.Inventory.ConsumeItemUse(item);
        }
    }

    internal bool CanStartItemUse(Player player, Item item, byte spellIndex, out SpellCastResult result, bool requireCharges = true)
    {
        result = SpellCastResult.ItemNotReady;
        if (!player.IsInWorld || player.IsLoggingOut || IsInTransit(player) || IsQuestSettlementPending(player))
        {
            result = SpellCastResult.NotReady;
            return false;
        }

        if (player.Inventory.CanUseItem(item) != InventoryResult.Ok || spellIndex >= item.Template.Spells.Count)
        {
            return false;
        }

        ItemSpell selected = item.Template.Spells[spellIndex];
        if (selected.SpellId == 0 || selected.Trigger != 0 || (requireCharges && !HasAllItemCharges(item)))
        {
            return false;
        }

        if (item.Template.GetInventoryType() != InventoryType.NonEquip
            && !(item.Container is null && item.Slot < InventorySlots.EquipmentEnd))
        {
            return false;
        }

        bool equipped = item.Container is null && item.Slot < InventorySlots.EquipmentEnd;
        if (player.GetByte(UpdateFields.UnitFieldBytes1, 2) != 0 && !equipped)
        {
            result = SpellCastResult.NoItemsWhileShapeshifted;
            return false;
        }

        if (player.Combat.IsInCombat && item.Template.Spells.Any(s => s.SpellId != 0 && s.Trigger == 0
            && Store.Get(s.SpellId)?.HasAttribute(SpellAttributesCombat.NotInCombatOnlyPeaceful) == true))
        {
            result = SpellCastResult.AffectingCombat;
            return false;
        }

        if (ItemUseTradeGuard(player, item))
        {
            return false;
        }

        result = SpellCastResult.CastOk;
        return true;
    }

    private sealed class ItemUseDispatch(Item item)
    {
        public Item Item { get; } = item;
        public bool PendingConsume { get; set; }
        public bool ReagentCleared { get; set; }
    }

    private ItemUseDispatch? CurrentItemUseDispatch(Item item)
        => _itemUseDispatches.Count > 0 && ReferenceEquals(_itemUseDispatches.Peek().Item, item) ? _itemUseDispatches.Peek() : null;

    private static bool HasAllItemCharges(Item item)
    {
        for (int index = 0; index < Item.SpellChargeSlots && index < item.Template.Spells.Count; index++)
        {
            ItemSpell spell = item.Template.Spells[index];
            if (spell.SpellId != 0 && spell.Charges != 0
                && item.GetInt32(UpdateFields.ItemFieldSpellCharges + index) == 0)
            {
                return false;
            }
        }

        return true;
    }
}
