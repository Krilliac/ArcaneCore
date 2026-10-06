using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Plans the single applicable on-use spell carried by a caster-owned item for a
    /// foreign trade target. This is planning only: no binding, charge, reagent, cooldown,
    /// inventory, or world callback is performed here.
    /// </summary>
    public SpellCastResult TryPlanTradeItemEnchantment(Player caster, Player recipient, Item target,
        Item castItem, byte clientSpellIndex, out TradeEnchantmentPlan? plan, bool acceptance = false)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(castItem);
        plan = null;

        if (!ReferenceEquals(target.Inventory, recipient.Inventory) || target.OwnerGuid != recipient.Guid
            || caster.IsQuestSettlementPending || recipient.IsQuestSettlementPending
            || !ReferenceEquals(castItem.Inventory, caster.Inventory)
            || castItem.OwnerGuid != caster.Guid
            || caster.Inventory.GetItemByGuid(castItem.Guid) is not { } liveCastItem
            || !ReferenceEquals(liveCastItem, castItem))
            return SpellCastResult.ItemNotReady;

        if (!CanStartItemUse(caster, castItem, clientSpellIndex, out SpellCastResult eligibility))
            return eligibility;

        if (clientSpellIndex >= castItem.Template.Spells.Count)
            return SpellCastResult.ItemNotReady;

        ItemSpell selected = castItem.Template.Spells[clientSpellIndex];
        if (selected.SpellId == 0 || selected.Trigger != 0
            || Store.Get(selected.SpellId) is not { } spell || spell.IsPassive)
            return SpellCastResult.ItemNotReady;

        // Ordinary HandleItemUse dispatches every applicable on-use entry. A deferred
        // single-spell plan must refuse a multi-spell template instead of dropping work.
        int applicable = 0;
        foreach (ItemSpell itemSpell in castItem.Template.Spells)
        {
            if (itemSpell.SpellId != 0 && itemSpell.Trigger == 0
                && Store.Get(itemSpell.SpellId) is not null)
                applicable++;
        }
        if (applicable != 1)
            return SpellCastResult.ItemNotReady;

        SpellCastResult imageResult = TryBuildTradeEnchantmentAfterImage(caster, target, spell,
            out ItemInstanceData? updatedRecipientItem);
        if (imageResult != SpellCastResult.CastOk)
            return imageResult;

        SpellCastTargets targets = new()
        {
            Mask = SpellCastTargetFlags.TradeItem,
            Item = new ObjectGuid(6),
            ServerValidatedTradeItem = target,
        };
        UnitSpellState state = GetOrCreateState(caster);
        SpellCastResult check = CheckCast(state, spell, targets, unitTarget: null, triggered: acceptance,
            strict: true, castItem: castItem, itemCategory: selected.Category == 0 ? null : selected.Category);
        if (check != SpellCastResult.CastOk)
            return check;

        if (!CollectReagents(spell, out IReadOnlyList<InventoryRewardGrant> reagents))
            return SpellCastResult.ItemNotReady;

        bool overlapsCastItem = reagents.Any(r => r.Entry == castItem.Entry);
        if (overlapsCastItem)
        {
            reagents = reagents.Select(r => r.Entry == castItem.Entry
                ? new InventoryRewardGrant(r.Entry, RequiredReagentCount(r, castItem)) : r).ToArray();
        }

        foreach (InventoryRewardGrant reagent in reagents)
        {
            if (caster.Inventory.GetItemCount(reagent.Entry) < reagent.Count)
                return SpellCastResult.ItemNotReady;
        }

        TradeItemCastContext context = new(castItem.Guid, castItem.Entry, clientSpellIndex, selected,
            castItem.BagSlot, castItem.Slot, overlapsCastItem,
            overlapsCastItem ? null : ItemUsePaymentPlan.Create(castItem));
        plan = new TradeEnchantmentPlan(
            updatedRecipientItem!,
            Array.AsReadOnly(reagents.ToArray()),
            CasterLifeAfter: null,
            PowerCost: 0,
            SpellSnapshot: spell with { },
            ItemCast: context);
        return SpellCastResult.CastOk;
    }
}
