using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots;

internal enum PlayerbotConsumableKind : byte
{
    None = 0,
    Food = 1,
    Drink = 2,
    HealingPotion = 3,
    ManaPotion = 4,
    Bandage = 5,
    BuffScrollOrElixir = 6,
}

internal readonly record struct PlayerbotConsumable(Item Item, PlayerbotConsumableKind Kind);

/// <summary>Bounded, fail-closed classification of carried food, drink, potions, bandages, and buff consumables using the real spell catalog.</summary>
internal static class PlayerbotConsumables
{
    private const int MaxItemsScanned = 128;
    private const uint SubClassPotion = 1;
    private const uint SubClassElixir = 2;
    private const uint SubClassFlask = 3;
    private const uint SubClassScroll = 4;
    private const uint SubClassFoodDrink = 5;
    private const uint SubClassBandage = 7;

    internal static bool TryFindRecovery(Player player, SpellSystem spells, out PlayerbotConsumable consumable)
        => TryFindRecovery(player, spells, 45, 35, out consumable);

    /// <param name="healthBelowPct">Eat below this health percentage (45 between ordinary fights; the risk's recovery asks for more).</param>
    /// <param name="manaBelowPct">Drink below this mana percentage (35 between ordinary fights).</param>
    internal static bool TryFindRecovery(Player player, SpellSystem spells, uint healthBelowPct, uint manaBelowPct, out PlayerbotConsumable consumable)
    {
        consumable = default;
        if (ArcaneCore.Game.Items.ItemUse.ItemUseService.IsShapeShifted(player))
            return false;

        bool healthLow = player.Health * 100UL < player.MaxHealth * (ulong)healthBelowPct;
        bool manaLow = player.PowerType == PowerType.Mana
            && player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) > 0
            && SpellSystem.GetPower(player, PowerType.Mana) * 100UL
                < player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) * (ulong)manaBelowPct;

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
        if (ArcaneCore.Game.Items.ItemUse.ItemUseService.IsShapeShifted(player))
            return false;

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

    /// <summary>
    /// A food or drink is running (<see cref="HasActiveFoodDrink"/>), or the bot's use of one may still be waiting in the spell batch for its
    /// go (<see cref="SpellSystem.HasPendingGo"/>; always false with batching off). A rest that has just begun is not over, and a second
    /// item is not used, while the first has not landed: standing up then would cancel the food the moment it lands.
    /// </summary>
    internal static bool IsEatingOrAboutTo(Player player, SpellSystem spells)
        => HasActiveFoodDrink(player, spells) || spells.HasPendingGo(player);

    internal static bool HasActiveFoodDrink(Player player, SpellSystem spells)
        => spells.GetAuras(player).Any(holder => !holder.IsRemoved
            && holder.Spell.SpellFamilyName == 0
            && (holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0
            && (holder.HasAura(AuraType.ModRegen) || holder.HasAura(AuraType.ModPowerRegen)));

    internal static PlayerbotConsumableKind Classify(ItemTemplate template, SpellSystem spells)
        => TryClassify(template, spells.Store, out PlayerbotConsumableKind kind) ? kind : PlayerbotConsumableKind.None;

    internal static PlayerbotConsumableKind Classify(ItemTemplate template, SpellStore store)
        => TryClassify(template, store, out PlayerbotConsumableKind kind) ? kind : PlayerbotConsumableKind.None;

    internal static bool TryClassify(ItemTemplate template, SpellSystem spells, out PlayerbotConsumableKind kind)
        => TryClassify(template, spells.Store, out kind);

    /// <summary>
    /// The food/drink classification of each template, per spell store: pure on (template, store), and read for every carried item, vendor
    /// row and protected-item check of a think. Both keys are immutable content objects compared by reference and held weakly, so a reload of
    /// either (a new store, new templates) misses and the old entries go with the old objects. Thread-safe; a race computes twice, same result.
    /// </summary>
    private static readonly ConditionalWeakTable<SpellStore, ConditionalWeakTable<ItemTemplate, Classified>> s_classified = new();

    internal static bool TryClassify(ItemTemplate template, SpellStore store, out PlayerbotConsumableKind kind)
    {
        if (template is null || store is null)
            return ComputeClassify(template!, store!, out kind); // uncached: exactly as before the table, including its null behaviour
        ConditionalWeakTable<ItemTemplate, Classified> byTemplate = s_classified.GetValue(store, static _ => new());
        if (!byTemplate.TryGetValue(template, out Classified? classified))
        {
            classified = ComputeClassify(template, store, out PlayerbotConsumableKind computed) ? new Classified(true, computed) : Classified.No;
            byTemplate.AddOrUpdate(template, classified);
        }

        kind = classified.Kind;
        return classified.Ok;
    }

    private sealed record Classified(bool Ok, PlayerbotConsumableKind Kind)
    {
        public static readonly Classified No = new(false, default);
    }

    /// <summary>The classification <see cref="TryClassify(ItemTemplate, SpellStore, out PlayerbotConsumableKind)"/> keeps per template.</summary>
    internal static bool ComputeClassify(ItemTemplate template, SpellStore store, out PlayerbotConsumableKind kind)
    {
        kind = default;
        SpellInfo? normal = null;
        int normalCount = 0;
        foreach (ItemSpell itemSpell in template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != 0)
                continue;
            normalCount++;
            normal = store.Get(itemSpell.SpellId);
        }

        // The ordinary bot item packet currently selects spell slot zero.
        if (normalCount != 1 || normal is null || template.Spells[0].SpellId != normal.Id || template.Spells[0].Trigger != 0
            || normal.SpellFamilyName != 0
            || (normal.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) == 0)
            return false;

        SpellEffectInfo[] activeEffects = [.. normal.Effects.Where(effect => effect.Effect != SpellEffectName.None)];
        if (activeEffects.Length == 0 || activeEffects[0].Effect != SpellEffectName.ApplyAura
            || activeEffects[0].BasePoints < 0)
            return false;

        // Multi-effect cooked food ("Well Fed") has Effect 0 = ApplyAura(ModRegen) and Effect 1 = TriggerSpell / ApplyAura(Well Fed),
        // whereas ambiguous items carrying both ModRegen and ModPowerRegen(Mana) are rejected.
        bool hasFoodRegen = activeEffects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.AuraType == AuraType.ModRegen);
        bool hasDrinkRegen = activeEffects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.AuraType == AuraType.ModPowerRegen && e.MiscValue == (int)PowerType.Mana);

        SpellEffectInfo effect = activeEffects[0];
        bool food = effect.AuraType == AuraType.ModRegen && !hasDrinkRegen;
        bool drink = effect.AuraType == AuraType.ModPowerRegen && effect.MiscValue == (int)PowerType.Mana && !hasFoodRegen;
        if (food == drink)
            return false;

        kind = food ? PlayerbotConsumableKind.Food : PlayerbotConsumableKind.Drink;
        return true;
    }

    internal static Item? FindBestFood(Player player, SpellStore store)
    {
        Item? best = null;
        int bestPoints = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            if (!TryClassify(item.Template, store, out PlayerbotConsumableKind kind) || kind != PlayerbotConsumableKind.Food)
                continue;

            int points = store.Get(item.Template.Spells[0].SpellId)?.Effects[0].BasePoints ?? 0;
            if (best is null || points > bestPoints || (points == bestPoints && item.Template.ItemLevel > best.Template.ItemLevel))
            {
                best = item;
                bestPoints = points;
            }
        }
        return best;
    }

    internal static Item? FindBestDrink(Player player, SpellStore store)
    {
        Item? best = null;
        int bestPoints = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            if (!TryClassify(item.Template, store, out PlayerbotConsumableKind kind) || kind != PlayerbotConsumableKind.Drink)
                continue;

            int points = store.Get(item.Template.Spells[0].SpellId)?.Effects[0].BasePoints ?? 0;
            if (best is null || points > bestPoints || (points == bestPoints && item.Template.ItemLevel > best.Template.ItemLevel))
            {
                best = item;
                bestPoints = points;
            }
        }
        return best;
    }

    internal static Item? FindBestHealingPotion(Player player, SpellStore store,
        IReadOnlySet<uint>? cooldownSpells = null, IReadOnlySet<uint>? cooldownCategories = null)
    {
        Item? best = null;
        int bestHeal = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            ItemTemplate template = item.Template;
            if ((ItemClass)template.Class != ItemClass.Consumable || template.SubClass is not (SubClassPotion or 0))
                continue;

            ItemSpell use = template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0 || IsOnCooldown(use, cooldownSpells, cooldownCategories)
                || store.Get(use.SpellId) is not { } spell)
                continue;

            int heal = 0;
            foreach (SpellEffectInfo eff in spell.Effects)
            {
                if (eff.Effect == SpellEffectName.Heal && eff.BasePoints >= 0)
                    heal += eff.BasePoints + 1;
            }
            if (heal <= 0)
                continue;

            if (best is null || heal > bestHeal)
            {
                best = item;
                bestHeal = heal;
            }
        }
        return best;
    }

    internal static Item? FindBestManaPotion(Player player, SpellStore store,
        IReadOnlySet<uint>? cooldownSpells = null, IReadOnlySet<uint>? cooldownCategories = null)
    {
        Item? best = null;
        int bestMana = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            ItemTemplate template = item.Template;
            if ((ItemClass)template.Class != ItemClass.Consumable || template.SubClass is not (SubClassPotion or 0))
                continue;

            ItemSpell use = template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0 || IsOnCooldown(use, cooldownSpells, cooldownCategories)
                || store.Get(use.SpellId) is not { } spell)
                continue;

            int mana = 0;
            foreach (SpellEffectInfo eff in spell.Effects)
            {
                if (eff.Effect == SpellEffectName.Energize && eff.MiscValue == (int)PowerType.Mana && eff.BasePoints >= 0)
                    mana += eff.BasePoints + 1;
            }
            if (mana <= 0)
                continue;

            if (best is null || mana > bestMana)
            {
                best = item;
                bestMana = mana;
            }
        }
        return best;
    }

    internal static Item? FindBestBandage(Player player, SpellStore store,
        IReadOnlySet<uint>? cooldownSpells = null, IReadOnlySet<uint>? cooldownCategories = null)
    {
        Item? best = null;
        int bestTick = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            ItemTemplate template = item.Template;
            if ((ItemClass)template.Class != ItemClass.Consumable || template.SubClass is not (SubClassBandage or 0))
                continue;

            ItemSpell use = template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0 || IsOnCooldown(use, cooldownSpells, cooldownCategories)
                || store.Get(use.SpellId) is not { } spell
                || (spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0)
                continue;

            int tick = 0;
            foreach (SpellEffectInfo eff in spell.Effects)
            {
                if (eff.Effect == SpellEffectName.ApplyAura && eff.AuraType == AuraType.PeriodicHeal && eff.BasePoints >= 0)
                    tick += eff.BasePoints + 1;
            }
            if (tick <= 0)
                continue;

            if (best is null || tick > bestTick)
            {
                best = item;
                bestTick = tick;
            }
        }
        return best;
    }

    internal static Item? FindBestBuffConsumable(Player player, SpellStore store,
        IReadOnlyList<SpellAuraHolder>? activeAuras = null,
        IReadOnlySet<uint>? cooldownSpells = null, IReadOnlySet<uint>? cooldownCategories = null)
    {
        Item? best = null;
        int bestScore = int.MinValue;
        foreach (Item item in CarriedUsable(player))
        {
            ItemTemplate template = item.Template;
            if ((ItemClass)template.Class != ItemClass.Consumable
                || template.SubClass is SubClassPotion or SubClassFoodDrink or SubClassBandage)
                continue;

            ItemSpell use = template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0 || IsOnCooldown(use, cooldownSpells, cooldownCategories)
                || store.Get(use.SpellId) is not { } spell
                || !spell.IsPositive
                || (spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0)
                continue;

            if (activeAuras is not null && activeAuras.Any(h => !h.IsRemoved
                && (h.Spell.Id == spell.Id || (!string.IsNullOrEmpty(spell.Name) && string.Equals(h.Spell.Name, spell.Name, StringComparison.OrdinalIgnoreCase)))))
                continue;

            int score = 0;
            bool hasNegative = false;
            foreach (SpellEffectInfo eff in spell.Effects)
            {
                if (eff.Effect == SpellEffectName.None)
                    continue;
                if (eff.BasePoints < 0 || eff.TargetA == SpellImplicitTarget.UnitCasterPet)
                {
                    hasNegative = true;
                    break;
                }
                if (eff.Effect == SpellEffectName.ApplyAura && eff.AuraType is AuraType.ModStat
                    or AuraType.ModResistance or AuraType.ModAttackPower or AuraType.ModRangedAttackPower
                    or AuraType.ModDamageDone or AuraType.ModHealingDone or AuraType.ModIncreaseHealth
                    or AuraType.ModIncreaseEnergy or AuraType.ModRegen or AuraType.ModPowerRegen)
                {
                    score += eff.BasePoints + 1;
                }
            }
            if (hasNegative || score <= 0)
                continue;

            if (best is null || score > bestScore)
            {
                best = item;
                bestScore = score;
            }
        }
        return best;
    }

    private static IEnumerable<Item> CarriedUsable(Player player)
    {
        if (ArcaneCore.Game.Items.ItemUse.ItemUseService.IsShapeShifted(player))
            yield break;

        foreach (Item item in player.Inventory.AllItems.Take(MaxItemsScanned))
        {
            if (InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                && !InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                && item.Count > 0
                && player.Inventory.CanUseItem(item) == InventoryResult.Ok)
            {
                yield return item;
            }
        }
    }

    private static bool IsOnCooldown(ItemSpell use, IReadOnlySet<uint>? cooldownSpells, IReadOnlySet<uint>? cooldownCategories)
        => (cooldownSpells is not null && cooldownSpells.Contains(use.SpellId))
            || (use.Category != 0 && cooldownCategories is not null && cooldownCategories.Contains(use.Category));
}
