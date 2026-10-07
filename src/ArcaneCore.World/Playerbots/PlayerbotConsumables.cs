using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots;

internal enum PlayerbotConsumableKind : byte
{
    Food,
    Drink,
}

internal readonly record struct PlayerbotConsumable(Item Item, PlayerbotConsumableKind Kind);

/// <summary>Bounded, fail-closed classification of carried food and drink using the real spell catalog.</summary>
internal static class PlayerbotConsumables
{
    private const int MaxItemsScanned = 128;

    internal static bool TryFindRecovery(Player player, SpellSystem spells, out PlayerbotConsumable consumable)
    {
        consumable = default;
        bool healthLow = player.Health * 100UL < player.MaxHealth * 45UL;
        bool manaLow = player.PowerType == PowerType.Mana
            && player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) > 0
            && SpellSystem.GetPower(player, PowerType.Mana) * 100UL
                < player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) * 35UL;

        if (!healthLow && !manaLow)
            return false;

        PlayerbotConsumable? foodCandidate = null;
        PlayerbotConsumable? drinkCandidate = null;
        foreach (Item item in player.Inventory.AllItems.Take(MaxItemsScanned))
        {
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || item.Count == 0 || player.Inventory.CanUseItem(item) != InventoryResult.Ok
                || !TryClassify(item.Template, spells, out PlayerbotConsumableKind kind))
                continue;

            if (kind == PlayerbotConsumableKind.Food) foodCandidate ??= new(item, kind);
            if (kind == PlayerbotConsumableKind.Drink) drinkCandidate ??= new(item, kind);
        }

        if (healthLow && foodCandidate is { } food)
        {
            consumable = food;
            return true;
        }

        if (manaLow && drinkCandidate is { } drink)
        {
            consumable = drink;
            return true;
        }

        return false;
    }

    internal static bool NeedsRecovery(Player player, PlayerbotConsumableKind kind)
    {
        if (kind == PlayerbotConsumableKind.Food) return player.Health < player.MaxHealth;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return player.PowerType == PowerType.Mana && maxMana > 0
            && SpellSystem.GetPower(player, PowerType.Mana) < maxMana;
    }

    internal static bool HasUsable(Player player, SpellSystem spells, PlayerbotConsumableKind wanted)
    {
        foreach (Item item in player.Inventory.AllItems.Take(MaxItemsScanned))
        {
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || item.Count == 0 || player.Inventory.CanUseItem(item) != InventoryResult.Ok
                || !TryClassify(item.Template, spells, out PlayerbotConsumableKind kind)
                || kind != wanted)
                continue;
            return true;
        }
        return false;
    }

    internal static bool HasActiveFoodDrink(Player player, SpellSystem spells)
        => spells.GetAuras(player).Any(holder => !holder.IsRemoved
            && holder.Spell.SpellFamilyName == 0
            && (holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
            && (holder.HasAura(AuraType.ModRegen) || holder.HasAura(AuraType.ModPowerRegen)));

    internal static bool TryClassify(ItemTemplate template, SpellSystem spells, out PlayerbotConsumableKind kind)
    {
        kind = default;
        SpellInfo? normal = null;
        int normalCount = 0;
        foreach (ItemSpell itemSpell in template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != 0)
                continue;
            normalCount++;
            normal = spells.Store.Get(itemSpell.SpellId);
        }

        // The ordinary bot item packet currently selects spell slot zero.
        if (normalCount != 1 || normal is null || template.Spells[0].SpellId != normal.Id || template.Spells[0].Trigger != 0
            || normal.SpellFamilyName != 0
            || (normal.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) == 0)
            return false;

        SpellEffectInfo[] activeEffects = [.. normal.Effects.Where(effect => effect.Effect != SpellEffectName.None)];
        if (activeEffects.Length != 1 || activeEffects[0].Effect != SpellEffectName.ApplyAura
            || activeEffects[0].BasePoints < 0)
            return false;

        SpellEffectInfo effect = activeEffects[0];
        bool food = effect.AuraType == AuraType.ModRegen;
        bool drink = effect.AuraType == AuraType.ModPowerRegen && effect.MiscValue == (int)PowerType.Mana;
        if (food == drink)
            return false;

        kind = food ? PlayerbotConsumableKind.Food : PlayerbotConsumableKind.Drink;
        return true;
    }
}
