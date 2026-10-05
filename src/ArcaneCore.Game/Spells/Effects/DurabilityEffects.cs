using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

/// <summary>Durability effects 111 and 115, after vmangos SpellEffects.cpp:5576-5640.</summary>
public sealed class DurabilityEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.RegisterEffect(SpellEffectName.DurabilityDamage, DamagePoints);
        system.RegisterEffect(SpellEffectName.DurabilityDamagePct, DamagePercent);
    }

    private static void DamagePoints(SpellEffectContext context)
    {
        if (context.Target is not Player player)
        {
            return;
        }

        int slot = context.Effect.MiscValue;
        if (slot < 0)
        {
            player.Inventory.DurabilityPointsLossAll(context.Value, inventory: slot < -1);
        }
        else if (slot < InventorySlots.BagEnd && player.Inventory.GetItem(InventorySlots.Bag0, (byte)slot) is { } item)
        {
            player.Inventory.DurabilityPointsLoss(item, context.Value);
        }
    }

    private static void DamagePercent(SpellEffectContext context)
    {
        if (context.Target is not Player player)
        {
            return;
        }

        int slot = context.Effect.MiscValue;
        if (slot < 0)
        {
            // Source dispatches negative selectors before checking the percentage: zero
            // reaches the shared minimum-one loss primitive for all selected items.
            player.Inventory.DurabilityLossAll(context.Value / 100.0f, inventory: slot < -1);
        }
        else if (slot < InventorySlots.BagEnd && context.Value > 0
            && player.Inventory.GetItem(InventorySlots.Bag0, (byte)slot) is { } item)
        {
            player.Inventory.DurabilityLoss(item, context.Value / 100.0f);
        }
    }
}
