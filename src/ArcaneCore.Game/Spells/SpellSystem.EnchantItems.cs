using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Spells;

internal static class EnchantItemSpellRules
{
    public static SpellCastResult Check(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player player)
            return SpellCastResult.ItemNotReady;

        Item? item;
        bool trade = context.Targets.IsRawNonTradedTradeTarget;
        if (trade)
        {
            item = context.Targets.ServerValidatedTradeItem;
            if (item is null || ReferenceEquals(item.Inventory, player.Inventory) || item.OwnerGuid == player.Guid
                || item.Inventory?.EnchantmentSink is null)
                return SpellCastResult.ItemNotReady;
        }
        else
        {
            if ((context.Targets.Mask & SpellCastTargetFlags.TradeItem) != 0
                || (context.Targets.Mask & SpellCastTargetFlags.Item) == 0
                || player.Inventory.GetItemByGuid(context.Targets.Item) is not { } owned
                || owned.OwnerGuid != player.Guid || player.Inventory.EnchantmentSink is null)
                return SpellCastResult.ItemNotReady;
            item = owned;
        }

        SpellInfo spell = context.Spell;
        if (spell.EquippedItemClass >= 0 && (item.Template.Class != (uint)spell.EquippedItemClass
            || (spell.EquippedItemSubClassMask != 0 && (spell.EquippedItemSubClassMask & (1 << (int)item.Template.SubClass)) == 0)
            || (spell.EquippedItemInventoryTypeMask != 0 && (spell.EquippedItemInventoryTypeMask & (1 << (int)item.Template.InventoryType)) == 0)))
            return SpellCastResult.ItemNotReady;
        if (context.Effect.Effect == SpellEffectName.EnchantItem
            && item.Template.ItemLevel < spell.BaseLevel)
            return SpellCastResult.ItemNotReady;

        if (context.Effect.Effect == SpellEffectName.EnchantItemTemporary
            && ((long)context.Effect.BasePoints + 1) > uint.MaxValue / 1000L)
            return SpellCastResult.ItemNotReady;

        return context.System.ItemEnchantments.Find((uint)context.Effect.MiscValue) is null
            ? SpellCastResult.ItemNotReady : SpellCastResult.CastOk;
    }

    public static Item? ResolveItem(SpellEffectContext context)
        => context.Caster is Player player && (context.Cast.Targets.Mask & SpellCastTargetFlags.Item) != 0
            ? player.Inventory.GetItemByGuid(context.Cast.Targets.Item) : null;
}
