using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Progression;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// The gear kit chosen for one character: the template per equipment slot, the slots with no candidate, and for a pick whose template
/// has a random property (<c>item_template.random_property</c>, "of the Bear") the property the new item is given.
/// </summary>
internal sealed record BoostGearPlan(IReadOnlyList<KeyValuePair<byte, ItemTemplate>> Picks, IReadOnlyList<byte> EmptySlots)
{
    /// <summary>The chosen random property per slot (only slots whose pick has a random property are listed).</summary>
    public IReadOnlyDictionary<byte, BoostSuffix> Suffixes { get; init; } = new Dictionary<byte, BoostSuffix>();
}

/// <summary>
/// One random property an item of a random-property template can be created with: the ItemRandomProperties.dbc id, its name ("of the
/// Bear") and the item stats and armor its enchantments add (see <see cref="BoostSuffixes"/>).
/// </summary>
internal sealed record BoostSuffix(int PropertyId, string Name, IReadOnlyList<ItemStat> Stats)
{
    /// <summary>The armor the property adds ("of Toughness").</summary>
    public int Armor { get; init; }
}

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
    private const uint QualityNormal = 1;             // vmangos SharedDefines.h ITEM_QUALITY_NORMAL (white)
    private const uint QualityRare = 3;               // ITEM_QUALITY_RARE (blue)

    /// <summary>The default highest quality of a kit piece: rare (blue), what dungeons at the level give (<see cref="CharacterBoostOptions.MaxQuality"/>).</summary>
    public const uint DefaultMaxQuality = QualityRare;
    private const uint BondingQuestItem = 4;          // vmangos ItemPrototype.h BIND_QUEST_ITEM

    private readonly record struct Candidate(ItemTemplate Template, byte[] Slots, float Value, BoostSuffix? Suffix);

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
    /// Whether a template may be part of a boost kit at <paramref name="level"/>: white up to <paramref name="maxQuality"/> (grey is vendor
    /// trash; the default cap is <see cref="DefaultMaxQuality"/>), required level and item level within the level, not deprecated or flagged
    /// unobtainable, no skill, spell, reputation, honor, city rank, quest start, duration or area/map bound, not a quest-bound item, an
    /// equippable inventory type that is not a shirt, tabard, bag, quiver or ammo, and obtainable (<paramref name="obtainable"/>). A random
    /// property is allowed here: <see cref="Plan"/> keeps such a template only when it can name the property the item gets.
    /// </summary>
    public static bool IsPoolItem(ItemTemplate t, byte level, IReadOnlySet<uint> obtainable, uint maxQuality = DefaultMaxQuality)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(obtainable);
        return t.Quality >= QualityNormal && t.Quality <= maxQuality
            && t.RequiredLevel <= level && t.ItemLevel <= level
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
    /// Whether a template, with the random property <paramref name="suffix"/> when it has one, gives its wearer anything: a stat,
    /// armor, block, a resistance, a spell or weapon damage. A piece with none of these (a statless hat, an item-level-1 necklace) is
    /// no better than the empty slot, so the planner never offers it.
    /// </summary>
    public static bool GivesAnything(ItemTemplate t, BoostSuffix? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Stats.Any(s => s.Value != 0) || (suffix is not null && (suffix.Stats.Any(s => s.Value != 0) || suffix.Armor > 0))
            || t.Armor > 0 || t.Block > 0
            || t.HolyRes != 0 || t.FireRes != 0 || t.NatureRes != 0 || t.FrostRes != 0 || t.ShadowRes != 0 || t.ArcaneRes != 0
            || t.Spells.Any(s => s.SpellId != 0)
            || t.Damages.Any(d => d.Max > 0f);
    }

    /// <summary>The value of a random property's stats and armor under <paramref name="weights"/> (see <see cref="PlayerbotStatWeights.Stat"/>).</summary>
    public static float SuffixScore(BoostSuffix suffix, PlayerbotStatWeights weights)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        ArgumentNullException.ThrowIfNull(weights);
        float score = weights.Armor * Math.Max(suffix.Armor, 0);
        foreach (ItemStat stat in suffix.Stats)
        {
            score += weights.Stat(stat.Type) * stat.Value;
        }

        return score;
    }

    /// <summary>
    /// The random property a boost gives a new item: the one of <paramref name="choices"/> worth most under <paramref name="weights"/>
    /// (the lower property id on a tie), or null when there is no choice. Every listed property is one the template's
    /// <c>item_enchantment_template</c> group can roll, so the kit holds an item the world can drop; the boost only picks the roll.
    /// </summary>
    public static BoostSuffix? ChooseSuffix(IReadOnlyList<BoostSuffix> choices, PlayerbotStatWeights weights)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(weights);
        BoostSuffix? best = null;
        float bestScore = float.NegativeInfinity;
        foreach (BoostSuffix choice in choices)
        {
            float score = SuffixScore(choice, weights);
            if (best is null || score > bestScore || (score == bestScore && choice.PropertyId < best.PropertyId))
            {
                best = choice;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a template is a cosmetic quest reward: its only source is a quest (<paramref name="questOnly"/>, see
    /// <see cref="BoostItemSources.QuestOnly"/>) and it carries no stat, resistance, spell or weapon damage. Such a piece can only
    /// win a slot as a fallback or on item level alone, so the planner never offers it, even when the slot would stay empty.
    /// </summary>
    public static bool IsCosmeticQuestReward(ItemTemplate t, IReadOnlySet<uint> questOnly)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(questOnly);
        return questOnly.Contains(t.Entry)
            && !t.Stats.Any(s => s.Value != 0)
            && t.HolyRes == 0 && t.FireRes == 0 && t.NatureRes == 0 && t.FrostRes == 0 && t.ShadowRes == 0 && t.ArcaneRes == 0
            && !t.Spells.Any(s => s.SpellId != 0)
            && !t.Damages.Any(d => d.Max > 0f);
    }

    /// <summary>
    /// The kit: for each slot in <see cref="SlotOrder"/> the best candidate by score times style factor, then item level, then
    /// the lower entry. <paramref name="canUse"/> is the server's wear rule; <paramref name="canDualWield"/> offers one-hand
    /// weapons to the off hand. A slot with no candidate is listed in <see cref="BoostGearPlan.EmptySlots"/>; the off hand is
    /// skipped (not listed) when the main hand is a two-hander. A cosmetic quest reward (<see cref="IsCosmeticQuestReward"/>, when
    /// <paramref name="questOnly"/> is given) and a piece that gives nothing (<see cref="GivesAnything"/>) are never candidates.
    /// A template with a random property is scored with the best of the properties <paramref name="suffixes"/> lists for it
    /// (<see cref="ChooseSuffix"/>), which the plan records in <see cref="BoostGearPlan.Suffixes"/>; with no suffix source, or none
    /// listed for it, it is not a candidate (its stats would be unknown, and an item created without its property has none).
    /// </summary>
    public static BoostGearPlan Plan(
        IEnumerable<ItemTemplate> pool,
        PlayerbotStatWeights weights,
        Class playerClass,
        bool canDualWield,
        Func<ItemTemplate, bool> canUse,
        IReadOnlySet<uint>? questOnly = null,
        Func<ItemTemplate, IReadOnlyList<BoostSuffix>>? suffixes = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(canUse);

        // Per template: the slots it may fill, computed once. The score depends only on the template and the weights.
        List<Candidate> usable = [];
        foreach (ItemTemplate template in pool)
        {
            byte[] slots = [.. template.AllowedEquipSlots(playerClass, canDualWield).Where(s => s < InventorySlots.EquipmentEnd)];
            if (slots.Length == 0 || (questOnly is not null && IsCosmeticQuestReward(template, questOnly)) || !canUse(template))
            {
                continue;
            }

            BoostSuffix? suffix = null;
            if (template.RandomProperty != 0)
            {
                suffix = suffixes is null ? null : ChooseSuffix(suffixes(template), weights);
                if (suffix is null)
                {
                    continue;
                }
            }

            if (!GivesAnything(template, suffix))
            {
                continue;
            }

            float score = PlayerbotItemScore.Score(template, weights) + (suffix is null ? 0f : SuffixScore(suffix, weights));
            usable.Add(new Candidate(template, slots, score * PlayerbotItemScore.StyleFactor(template, weights), suffix));
        }

        var picks = new List<KeyValuePair<byte, ItemTemplate>>();
        var empty = new List<byte>();
        var chosen = new Dictionary<byte, ItemTemplate>();
        var chosenSuffixes = new Dictionary<byte, BoostSuffix>();
        foreach (byte slot in SlotOrder)
        {
            if (slot == InventorySlots.OffHand
                && chosen.TryGetValue(InventorySlots.MainHand, out ItemTemplate? main) && main.GetInventoryType() == InventoryType.TwoHandWeapon)
            {
                continue;
            }

            if (Best(usable, slot, weights, chosen) is not { } pick)
            {
                empty.Add(slot);
                continue;
            }

            chosen[slot] = pick.Template;
            picks.Add(new KeyValuePair<byte, ItemTemplate>(slot, pick.Template));
            if (pick.Suffix is { } chosenSuffix)
            {
                chosenSuffixes[slot] = chosenSuffix;
            }
        }

        return new BoostGearPlan(picks, empty) { Suffixes = chosenSuffixes };
    }

    private static Candidate? Best(List<Candidate> usable, byte slot, PlayerbotStatWeights weights, Dictionary<byte, ItemTemplate> chosen)
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

        return best;
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
