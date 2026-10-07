using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Conservative out-of-combat armor upgrades for one managed player.</summary>
internal sealed class PlayerbotEquipment(WorldSession session)
{
    private const int MaxItemsScanned = 128;

    internal bool Update(Player player)
    {
        if (!player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat
            || session.ManagedBudget is not { Remaining: > 0 })
            return false;

        if (session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting })
            return false;

        foreach (Item item in player.Inventory.AllItems.Take(MaxItemsScanned)
            .Where(candidate => InventorySlots.IsInventoryPos(candidate.BagSlot, candidate.Slot))
            .OrderBy(candidate => candidate.Slot).ThenBy(candidate => candidate.Entry))
        {
            if (!IsPlainArmor(item) || item.Template.Armor <= 0
                || (item.MaxDurability > 0 && item.Durability == 0)
                || player.Inventory.CanUseItem(item) != InventoryResult.Ok)
                continue;

            byte[] slots = item.Template.AllowedEquipSlots(player.Class, canDualWield: false)
                .Where(slot => slot is InventorySlots.Head or InventorySlots.Shoulders or InventorySlots.Chest
                    or InventorySlots.Waist or InventorySlots.Legs or InventorySlots.Feet
                    or InventorySlots.Wrists or InventorySlots.Hands or InventorySlots.Back)
                .ToArray();
            if (slots.Length != 1)
                continue;

            Item? worn = player.Inventory.GetItem(InventorySlots.Bag0, slots[0]);
            if (worn is not null && (!IsPlainArmor(worn) || item.Template.Armor <= worn.Template.Armor))
                continue;

            return session.TryManagedAction(WorldOpcode.CmsgAutoequipItem,
                [item.BagSlot, item.Slot]);
        }

        return false;
    }

    private static bool IsPlainArmor(Item item)
    {
        ItemTemplate template = item.Template;
        if ((ItemClass)template.Class != ItemClass.Armor
            || template.RandomProperty != 0 || item.RandomPropertyId != 0
            || template.HolyRes != 0 || template.FireRes != 0 || template.NatureRes != 0
            || template.FrostRes != 0 || template.ShadowRes != 0 || template.ArcaneRes != 0
            || template.SetId != 0
            || template.Stats.Any(stat => stat.Value != 0)
            || template.Spells.Any(spell => spell.SpellId != 0))
            return false;

        for (int slot = 0; slot < Item.EnchantmentValues / 3; slot++)
            if (item.EnchantmentId(slot) != 0)
                return false;
        return true;
    }
}
