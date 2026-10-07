using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly Stack<ItemUseDispatch> _itemUseDispatches = [];

    /// <summary>World-owned deferred trade callback for a caster-owned item use.</summary>
    public Func<Player, Item, byte, SpellCastTargets, SpellCastResult>? TradeItemEnchantmentRequest { get; set; }
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
        if (!CanStartItemUse(player, item, spellIndex, out SpellCastResult eligibility,
                out InventoryResult? inventoryFailure, out SpellInfo? shapeshiftSpell))
        {
            if (inventoryFailure is { } failure)
            {
                player.Inventory.SendEquipError(failure, item, null);
            }
            if (eligibility == SpellCastResult.NoItemsWhileShapeshifted)
            {
                player.Inventory.SendEquipError(InventoryResult.None, item, null);
                if (shapeshiftSpell is { } spell)
                {
                    SendCastResult(player, spell, eligibility, triggered: false);
                }
            }

            return eligibility;
        }

        if (item.Template.Bonding is (uint)ItemBonding.WhenUse or (uint)ItemBonding.WhenPickedUp or (uint)ItemBonding.QuestItem)
        {
            item.SetBinding(true);
        }

        // Item-use eligibility and binding intentionally precede deferral, matching the
        // ordinary vmangos CastItemUseSpell path. The callback owns planning and settlement;
        // no ordinary Prepare, charge consumption, or global cooldown may run here.
        if ((targets.Mask & SpellCastTargetFlags.TradeItem) != 0
            && TradeItemEnchantmentRequest is { } deferred)
        {
            return deferred(player, item, spellIndex, targets);
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
                // vmangos Player::CastItemUseSpell (Player.cpp:7387): the ITEM_USE_CANCELS auras go first (stealth stays for a spell that
                // allows it).
                RemoveAurasWithInterruptFlags(player, AuraInterruptMask.ItemUse, 0,
                    skipStealth: ((uint)spell.AttributesEx & StealthBreakRules.AttributesExAllowWhileStealthed) != 0);
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
        => CanStartItemUse(player, item, spellIndex, out result, out _, out _, requireCharges);

    private bool CanStartItemUse(Player player, Item item, byte spellIndex, out SpellCastResult result,
        out InventoryResult? inventoryFailure, out SpellInfo? shapeshiftSpell, bool requireCharges = true)
    {
        result = SpellCastResult.ItemNotReady;
        inventoryFailure = null;
        shapeshiftSpell = null;
        if (!player.IsInWorld || player.IsLoggingOut || IsInTransit(player) || IsQuestSettlementPending(player))
        {
            result = SpellCastResult.NotReady;
            return false;
        }

        InventoryResult canUse = player.Inventory.CanUseItem(item);
        if (canUse != InventoryResult.Ok || spellIndex >= item.Template.Spells.Count)
        {
            if (canUse == InventoryResult.Ok && spellIndex >= item.Template.Spells.Count)
            {
                inventoryFailure = InventoryResult.ItemNotFound;
            }
            else if (canUse != InventoryResult.Ok)
            {
                inventoryFailure = canUse;
            }

            return false;
        }

        ItemSpell selected = item.Template.Spells[spellIndex];
        if (selected.SpellId == 0 || selected.Trigger != 0)
        {
            inventoryFailure = InventoryResult.ItemNotFound;
            return false;
        }

        if (requireCharges && !HasAllItemCharges(item))
        {
            return false;
        }

        if (item.Template.GetInventoryType() != InventoryType.NonEquip
            && !(item.Container is null && item.Slot < InventorySlots.EquipmentEnd))
        {
            inventoryFailure = InventoryResult.ItemNotFound;
            return false;
        }

        bool equipped = item.Container is null && item.Slot < InventorySlots.EquipmentEnd;
        if (player.GetByte(UpdateFields.UnitFieldBytes1, 2) != 0 && !equipped)
        {
            result = SpellCastResult.NoItemsWhileShapeshifted;
            shapeshiftSpell = Store.Get(selected.SpellId);
            return false;
        }

        if (player.Combat.IsInCombat && item.Template.Spells.Any(s => s.SpellId != 0 && s.Trigger == 0
            && Store.Get(s.SpellId)?.HasAttribute(SpellAttributesCombat.NotInCombatOnlyPeaceful) == true))
        {
            result = SpellCastResult.AffectingCombat;
            inventoryFailure = InventoryResult.NotInCombat;
            return false;
        }

        if (ItemUseTradeGuard(player, item))
        {
            inventoryFailure = InventoryResult.ItemNotFound;
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
