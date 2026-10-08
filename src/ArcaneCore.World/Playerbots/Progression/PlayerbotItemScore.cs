using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots.Progression;

/// <summary>How a build holds its weapons; it decides how a main-hand candidate is compared with what the hands hold now.</summary>
internal enum PlayerbotWeaponStyle
{
    /// <summary>A two-handed weapon (Arms, Retribution, Enhancement).</summary>
    TwoHand,

    /// <summary>A one-handed weapon and a shield (protection tanks, shield casters).</summary>
    OneHandShield,

    /// <summary>Two one-handed weapons (Fury, rogues).</summary>
    DualWield,

    /// <summary>A caster's weapon and off-hand: stats matter, weapon damage barely does.</summary>
    Caster,
}

/// <summary>
/// The stat weights of one build: how much one point of each stat, armor, block and weapon damage per second is worth to it.
/// Values are relative (only comparisons between items of one character matter). mangoszero's playerbot ranks items by item
/// level and armor sub-class alone (ItemUsageValue.cpp QueryItemUsageForEquip); these weights add what the build needs.
/// </summary>
internal sealed record PlayerbotStatWeights(
    string Name,
    float Strength,
    float Agility,
    float Stamina,
    float Intellect,
    float Spirit,
    float Armor,
    float Block,
    float MeleeDps,
    float RangedDps,
    float WandDps,
    PlayerbotWeaponStyle Style)
{
    /// <summary>A shield tank (Protection warrior or paladin).</summary>
    public static PlayerbotStatWeights ShieldTank { get; } = new("shield-tank", 1.0f, 0.8f, 1.5f, 0.2f, 0f, 0.08f, 0.6f, 1.5f, 0.3f, 0f, PlayerbotWeaponStyle.OneHandShield);

    /// <summary>Two-handed strength melee (Arms warrior, Retribution paladin).</summary>
    public static PlayerbotStatWeights TwoHandStrength { get; } = new("two-hand-strength", 2.0f, 1.0f, 0.6f, 0.1f, 0f, 0.02f, 0f, 3.0f, 0.3f, 0f, PlayerbotWeaponStyle.TwoHand);

    /// <summary>Dual-wield strength melee (Fury warrior).</summary>
    public static PlayerbotStatWeights DualWieldStrength { get; } = new("dual-wield-strength", 2.0f, 1.2f, 0.6f, 0f, 0f, 0.02f, 0f, 2.5f, 0.3f, 0f, PlayerbotWeaponStyle.DualWield);

    /// <summary>Agility dual wielder (rogues).</summary>
    public static PlayerbotStatWeights DualWieldAgility { get; } = new("dual-wield-agility", 1.0f, 2.0f, 0.6f, 0f, 0f, 0.02f, 0f, 2.5f, 0.3f, 0f, PlayerbotWeaponStyle.DualWield);

    /// <summary>Two-handed shaman melee (Enhancement).</summary>
    public static PlayerbotStatWeights TwoHandShaman { get; } = new("two-hand-shaman", 2.0f, 1.0f, 0.6f, 0.5f, 0.1f, 0.02f, 0f, 3.0f, 0f, 0f, PlayerbotWeaponStyle.TwoHand);

    /// <summary>Feral druid: forms ignore the weapon, so its damage barely counts.</summary>
    public static PlayerbotStatWeights Feral { get; } = new("feral", 1.6f, 1.6f, 1.0f, 0.2f, 0f, 0.06f, 0f, 0.2f, 0f, 0f, PlayerbotWeaponStyle.TwoHand);

    /// <summary>Hunter: agility and the ranged weapon.</summary>
    public static PlayerbotStatWeights Hunter { get; } = new("hunter", 0.3f, 2.0f, 0.6f, 0.6f, 0.1f, 0.02f, 0f, 0.5f, 3.0f, 0f, PlayerbotWeaponStyle.TwoHand);

    /// <summary>Damage caster (mage, warlock, Shadow priest, Elemental shaman, Balance druid).</summary>
    public static PlayerbotStatWeights Caster { get; } = new("caster", 0f, 0f, 0.8f, 1.5f, 0.5f, 0.01f, 0f, 0.05f, 0f, 1.0f, PlayerbotWeaponStyle.Caster);

    /// <summary>Healer (Holy paladin and priest, Restoration shaman and druid).</summary>
    public static PlayerbotStatWeights Healer { get; } = new("healer", 0f, 0f, 0.6f, 1.5f, 1.0f, 0.01f, 0f, 0.05f, 0f, 0.6f, PlayerbotWeaponStyle.Caster);

    /// <summary>The weight of one point of item stat <paramref name="type"/> (item_template stat_typeN, vmangos ItemModType).</summary>
    public float Stat(uint type) => (ItemStatType)type switch
    {
        ItemStatType.Strength => Strength,
        ItemStatType.Agility => Agility,
        ItemStatType.Stamina => Stamina,
        ItemStatType.Intellect => Intellect,
        ItemStatType.Spirit => Spirit,
        // ITEM_MOD_HEALTH and ITEM_MOD_MANA are flat pools: a tenth of the stamina (10 health) or intellect (15 mana) weight.
        ItemStatType.Health => Stamina / 10f,
        ItemStatType.Mana => Intellect / 15f,
        _ => 0f,
    };
}

/// <summary>
/// Item scores for managed playerbots: a pure function of the template (stats, armor, block, weapon damage per second, item
/// level as a tie-break) plus the stats an item instance carries in its enchantment slots (random suffixes and enchants), under
/// one build's <see cref="PlayerbotStatWeights"/>. Legality is never decided here: the server's own equip rules are.
/// </summary>
internal static class PlayerbotItemScore
{
    /// <summary>The smallest gain worth an equip request (filters float noise and same-item swaps).</summary>
    public const float MinimumGain = 0.05f;

    private const float ItemLevelWeight = 0.01f;
    private const uint WeaponBow = 2;
    private const uint WeaponGun = 3;
    private const uint WeaponThrown = 16;
    private const uint WeaponCrossbow = 18;
    private const uint WeaponWand = 19;

    /// <summary>The score of an item instance: its template plus the stats of its enchantment slots (null catalog: template only).</summary>
    public static float Score(Item item, PlayerbotStatWeights weights, Func<uint, SpellItemEnchantment?>? enchantments)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.MaxDurability > 0 && item.Durability == 0)
        {
            return 0f; // a broken item gives nothing (vmangos _ApplyItemMods skips it)
        }

        float score = Score(item.Template, weights);
        if (enchantments is null)
        {
            return score;
        }

        for (int slot = 0; slot < Item.EnchantmentValues / 3; slot++)
        {
            uint id = item.EnchantmentId(slot);
            if (id == 0 || enchantments(id) is not { } enchantment)
            {
                continue;
            }

            for (int effect = 0; effect < Math.Min(enchantment.Types.Count, Math.Min(enchantment.Amounts.Count, enchantment.Args.Count)); effect++)
            {
                if ((EnchantEffectType)enchantment.Types[effect] == EnchantEffectType.Stat)
                {
                    score += weights.Stat(enchantment.Args[effect]) * enchantment.Amounts[effect];
                }
            }
        }

        return score;
    }

    /// <summary>The score of a template: stats, armor, block value, weapon damage per second by kind, item level.</summary>
    public static float Score(ItemTemplate template, PlayerbotStatWeights weights)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(weights);
        float score = template.ItemLevel * ItemLevelWeight;
        foreach (ItemStat stat in template.Stats)
        {
            score += weights.Stat(stat.Type) * stat.Value;
        }

        score += weights.Armor * Math.Max(template.Armor, 0);
        score += weights.Block * template.Block;
        if ((ItemClass)template.Class == ItemClass.Weapon && template.Delay > 0)
        {
            float dps = WeaponDps(template);
            score += dps * template.SubClass switch
            {
                WeaponWand => weights.WandDps,
                WeaponBow or WeaponGun or WeaponCrossbow or WeaponThrown => weights.RangedDps,
                _ => weights.MeleeDps,
            };
        }

        return score;
    }

    /// <summary>Average damage per second over every damage entry (vmangos ItemPrototype damage ranges and Delay).</summary>
    public static float WeaponDps(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Delay == 0)
        {
            return 0f;
        }

        float average = 0f;
        foreach (ItemDamage damage in template.Damages)
        {
            average += (damage.Min + damage.Max) / 2f;
        }

        return average * 1000f / template.Delay;
    }

    /// <summary>How well the build's weapon style suits a template in a hand slot (1 = fully; ranged and armor slots are always 1).</summary>
    public static float StyleFactor(ItemTemplate template, PlayerbotStatWeights weights)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (template.GetInventoryType(), weights.Style) switch
        {
            (InventoryType.TwoHandWeapon, PlayerbotWeaponStyle.OneHandShield) => 0.5f,
            (InventoryType.TwoHandWeapon, PlayerbotWeaponStyle.DualWield) => 0.8f,
            (InventoryType.Weapon or InventoryType.WeaponMainHand, PlayerbotWeaponStyle.TwoHand) => 0.8f,
            (InventoryType.Shield, PlayerbotWeaponStyle.TwoHand or PlayerbotWeaponStyle.DualWield) => 0.7f,
            _ => 1f,
        };
    }

    /// <summary>
    /// The quest reward choice for <paramref name="player"/>: the usable equippable choice that gains most over what is worn in its
    /// slot (vmangos has no bot reward logic; mangoszero's playerbot asks ItemUsageValue for an equip upgrade), else the choice
    /// that sells for most (sell price times count). Indexes are into the dense choice list the turn-in validates. Ties keep the
    /// lower index; an empty list is choice 0.
    /// </summary>
    public static uint ChooseQuestReward(Player player, IReadOnlyList<uint> choiceItems, IReadOnlyList<uint> choiceCounts,
        PlayerbotStatWeights weights, Func<uint, SpellItemEnchantment?>? enchantments = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(choiceItems);
        ArgumentNullException.ThrowIfNull(choiceCounts);
        int best = -1;
        float bestGain = float.NegativeInfinity;
        int richest = 0;
        ulong richestValue = 0;
        for (int index = 0; index < choiceItems.Count && choiceItems[index] != 0; index++)
        {
            if (player.Inventory.Templates.Find(choiceItems[index]) is not { } template)
            {
                continue;
            }

            ulong value = (ulong)template.SellPrice * Math.Max(index < choiceCounts.Count ? choiceCounts[index] : 1u, 1u);
            if (value > richestValue)
            {
                richest = index;
                richestValue = value;
            }

            if (UpgradeGain(player, template, weights, enchantments) is { } gain && gain > bestGain)
            {
                best = index;
                bestGain = gain;
            }
        }

        return (uint)(best >= 0 ? best : richest);
    }

    /// <summary>
    /// What equipping a new item of <paramref name="template"/> would gain (null when the character cannot use or wear it).
    /// </summary>
    public static float? UpgradeGain(Player player, ItemTemplate template, PlayerbotStatWeights weights,
        Func<uint, SpellItemEnchantment?>? enchantments = null)
        => UpgradeGain(player, template, Score(template, weights), weights, enchantments, out _);

    /// <summary>
    /// What wearing an item of <paramref name="template"/> scoring <paramref name="candidateScore"/> would gain over the worse
    /// of the slots it may fill, and that slot (<see cref="InventorySlots.NullSlot"/> and null when the character cannot use or
    /// wear it). A free slot gains the whole score. A two-handed weapon replaces both hands; an off-hand item cannot go beside
    /// a two-handed weapon (vmangos CanEquipItem: EQUIP_ERR_CANT_EQUIP_WITH_TWOHANDED). The slots come from the server's own
    /// rules (ItemPrototype::GetAllowedEquipSlots with the character's dual-wield ability).
    /// </summary>
    public static float? UpgradeGain(Player player, ItemTemplate template, float candidateScore, PlayerbotStatWeights weights,
        Func<uint, SpellItemEnchantment?>? enchantments, out byte slot)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(template);
        slot = InventorySlots.NullSlot;
        byte[] slots = [.. template.AllowedEquipSlots(player.Class, player.Inventory.Requirements.CanDualWield(player.Inventory))
            .Where(candidate => candidate < InventorySlots.EquipmentEnd)];
        if (slots.Length == 0 || player.Inventory.CanUseItem(template) != InventoryResult.Ok)
        {
            return null;
        }

        float candidateValue = candidateScore * StyleFactor(template, weights);
        bool twoHand = template.GetInventoryType() == InventoryType.TwoHandWeapon;
        float worst = float.PositiveInfinity;
        foreach (byte candidate in slots)
        {
            if (candidate == InventorySlots.OffHand && player.Inventory.IsTwoHandUsed)
            {
                continue;
            }

            float worn = WornScore(player, candidate, weights, enchantments)
                + (twoHand && candidate == InventorySlots.MainHand ? WornScore(player, InventorySlots.OffHand, weights, enchantments) : 0f);
            if (worn < worst)
            {
                worst = worn;
                slot = candidate;
            }
        }

        return slot == InventorySlots.NullSlot ? null : candidateValue - worst;
    }

    /// <summary>The score of what is worn in <paramref name="slot"/> (0 when empty), style-weighted like a candidate.</summary>
    public static float WornScore(Player player, byte slot, PlayerbotStatWeights weights, Func<uint, SpellItemEnchantment?>? enchantments)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } worn
            ? Score(worn, weights, enchantments) * StyleFactor(worn.Template, weights)
            : 0f;
    }
}
