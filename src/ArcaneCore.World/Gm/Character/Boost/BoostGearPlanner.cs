using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Progression;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>The gear kit chosen for one character: the template per equipment slot, and the slots with no candidate.</summary>
internal sealed record BoostGearPlan(IReadOnlyList<KeyValuePair<byte, ItemTemplate>> Picks, IReadOnlyList<byte> EmptySlots);

/// <summary>
/// Chooses the gear kit of <c>.character boost</c>: a pool filter over item templates and a deterministic per-slot choice scored
/// by <see cref="PlayerbotItemScore"/> under a build's <see cref="PlayerbotStatWeights"/>. Whether a character may wear a
/// template (class and race masks, armor and weapon proficiency, level) is not decided here: the caller passes the server's own
/// rule (<see cref="PlayerInventory.CanUseItem(ItemTemplate, bool)"/>). Holds no state; safe from any thread.
/// </summary>
internal static class BoostGearPlanner
{
    /// <summary>Slots in choice order: hands first, because the off hand depends on the main hand; rings and trinkets pick slot 1 before slot 2.</summary>
    public static readonly byte[] SlotOrder =
    [
        InventorySlots.MainHand, InventorySlots.OffHand, InventorySlots.Ranged, InventorySlots.Head, InventorySlots.Neck,
        InventorySlots.Shoulders, InventorySlots.Chest, InventorySlots.Waist, InventorySlots.Legs, InventorySlots.Feet,
        InventorySlots.Wrists, InventorySlots.Hands, InventorySlots.Finger1, InventorySlots.Finger2, InventorySlots.Trinket1,
        InventorySlots.Trinket2, InventorySlots.Back,
    ];

    private const uint ItemExtraNotObtainable = 0x04; // vmangos ItemPrototype.h ITEM_EXTRA_NOT_OBTAINABLE
    private const uint QualityUncommon = 2;           // vmangos SharedDefines.h ITEM_QUALITY_UNCOMMON
    private const uint BondingQuestItem = 4;          // vmangos ItemPrototype.h BIND_QUEST_ITEM

    private readonly record struct Candidate(ItemTemplate Template, byte[] Slots, float Value);

    /// <summary>The display name of an equipment slot.</summary>
    public static string SlotName(byte slot) => slot switch
    {
        InventorySlots.Head => "Head",
        InventorySlots.Neck => "Neck",
        InventorySlots.Shoulders => "Shoulders",
        InventorySlots.Chest => "Chest",
        InventorySlots.Waist => "Waist",
        InventorySlots.Legs => "Legs",
        InventorySlots.Feet => "Feet",
        InventorySlots.Wrists => "Wrists",
        InventorySlots.Hands => "Hands",
        InventorySlots.Finger1 => "Finger1",
        InventorySlots.Finger2 => "Finger2",
        InventorySlots.Trinket1 => "Trinket1",
        InventorySlots.Trinket2 => "Trinket2",
        InventorySlots.Back => "Back",
        InventorySlots.MainHand => "MainHand",
        InventorySlots.OffHand => "OffHand",
        InventorySlots.Ranged => "Ranged",
        _ => "Slot" + slot,
    };

    /// <summary>
    /// Whether a template may be part of a boost kit at <paramref name="level"/>: green or lower quality, required level and item
    /// level within the level, no random property (every stat is in the template), not deprecated or flagged unobtainable, no
    /// skill, spell, reputation, honor, city rank, quest start, duration or area/map bound, not a quest-bound item, an equippable
    /// inventory type that is not a shirt, tabard, bag, quiver or ammo, and obtainable (<paramref name="obtainable"/>).
    /// </summary>
    public static bool IsPoolItem(ItemTemplate t, byte level, IReadOnlySet<uint> obtainable)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(obtainable);
        return t.Quality <= QualityUncommon
            && t.RequiredLevel <= level && t.ItemLevel <= level
            && t.RandomProperty == 0
            && !t.HasFlag(ItemTemplateFlags.Deprecated) && (t.ExtraFlags & ItemExtraNotObtainable) == 0
            && t.RequiredSkill == 0 && t.RequiredSpell == 0 && t.RequiredReputationFaction == 0
            && t.RequiredHonorRank == 0 && t.RequiredCityRank == 0
            && t.StartQuest == 0 && t.Duration == 0 && t.AreaBound == 0 && t.MapBound == 0
            && t.Bonding != BondingQuestItem
            && t.GetInventoryType() is not (InventoryType.NonEquip or InventoryType.Body or InventoryType.Tabard
                or InventoryType.Bag or InventoryType.Quiver or InventoryType.Ammo)
            && obtainable.Contains(t.Entry);
    }

    /// <summary>
    /// The kit: for each slot in <see cref="SlotOrder"/> the best candidate by score times style factor, then item level, then
    /// the lower entry. <paramref name="canUse"/> is the server's wear rule; <paramref name="canDualWield"/> offers one-hand
    /// weapons to the off hand. A slot with no candidate is listed in <see cref="BoostGearPlan.EmptySlots"/>; the off hand is
    /// skipped (not listed) when the main hand is a two-hander.
    /// </summary>
    public static BoostGearPlan Plan(
        IEnumerable<ItemTemplate> pool,
        PlayerbotStatWeights weights,
        Class playerClass,
        bool canDualWield,
        Func<ItemTemplate, bool> canUse)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(canUse);

        // Per template: the slots it may fill, computed once. The score depends only on the template and the weights.
        List<Candidate> usable = [];
        foreach (ItemTemplate template in pool)
        {
            byte[] slots = [.. template.AllowedEquipSlots(playerClass, canDualWield).Where(s => s < InventorySlots.EquipmentEnd)];
            if (slots.Length == 0 || !canUse(template))
            {
                continue;
            }

            usable.Add(new Candidate(template, slots, PlayerbotItemScore.Score(template, weights) * PlayerbotItemScore.StyleFactor(template, weights)));
        }

        var picks = new List<KeyValuePair<byte, ItemTemplate>>();
        var empty = new List<byte>();
        var chosen = new Dictionary<byte, ItemTemplate>();
        foreach (byte slot in SlotOrder)
        {
            if (slot == InventorySlots.OffHand
                && chosen.TryGetValue(InventorySlots.MainHand, out ItemTemplate? main) && main.GetInventoryType() == InventoryType.TwoHandWeapon)
            {
                continue;
            }

            ItemTemplate? best = Best(usable, slot, weights, chosen);
            if (best is null)
            {
                empty.Add(slot);
                continue;
            }

            chosen[slot] = best;
            picks.Add(new KeyValuePair<byte, ItemTemplate>(slot, best));
        }

        return new BoostGearPlan(picks, empty);
    }

    private static ItemTemplate? Best(List<Candidate> usable, byte slot, PlayerbotStatWeights weights, Dictionary<byte, ItemTemplate> chosen)
    {
        List<Candidate> candidates = [.. usable.Where(c => Array.IndexOf(c.Slots, slot) >= 0 && SuitsSlot(c.Template, slot, weights, chosen))];
        if (slot == InventorySlots.MainHand && weights.Style == PlayerbotWeaponStyle.TwoHand
            && candidates.Any(c => c.Template.GetInventoryType() == InventoryType.TwoHandWeapon))
        {
            // A two-hander is preferred when there is one; otherwise the best one-hander.
            candidates = [.. candidates.Where(c => c.Template.GetInventoryType() == InventoryType.TwoHandWeapon)];
        }

        Candidate? best = null;
        foreach (Candidate candidate in candidates)
        {
            if (best is null || Better(candidate, best.Value))
            {
                best = candidate;
            }
        }

        return best?.Template;
    }

    private static bool Better(Candidate a, Candidate b)
    {
        if (a.Value != b.Value)
        {
            return a.Value > b.Value;
        }

        if (a.Template.ItemLevel != b.Template.ItemLevel)
        {
            return a.Template.ItemLevel > b.Template.ItemLevel;
        }

        return a.Template.Entry < b.Template.Entry;
    }

    private static bool SuitsSlot(ItemTemplate template, byte slot, PlayerbotStatWeights weights, Dictionary<byte, ItemTemplate> chosen)
    {
        InventoryType type = template.GetInventoryType();
        switch (slot)
        {
            case InventorySlots.MainHand:
                return weights.Style == PlayerbotWeaponStyle.OneHandShield
                    ? type is InventoryType.Weapon or InventoryType.WeaponMainHand
                    : type is InventoryType.Weapon or InventoryType.WeaponMainHand or InventoryType.TwoHandWeapon;
            case InventorySlots.OffHand:
                return weights.Style switch
                {
                    PlayerbotWeaponStyle.OneHandShield => type == InventoryType.Shield,
                    // The same one-hander in both hands needs two copies, which a unique item (MaxCount 1) forbids.
                    PlayerbotWeaponStyle.DualWield => type is InventoryType.Weapon or InventoryType.WeaponOffHand
                        && (chosen.GetValueOrDefault(InventorySlots.MainHand)?.Entry != template.Entry || template.MaxCount != 1),
                    _ => type is InventoryType.Holdable or InventoryType.Shield,
                };
            case InventorySlots.Finger2:
                return chosen.GetValueOrDefault(InventorySlots.Finger1)?.Entry != template.Entry;
            case InventorySlots.Trinket2:
                return chosen.GetValueOrDefault(InventorySlots.Trinket1)?.Entry != template.Entry;
            default:
                return true;
        }
    }
}
