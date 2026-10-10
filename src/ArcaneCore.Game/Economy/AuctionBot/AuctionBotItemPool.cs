using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>
/// The items the bot may list, grouped by quality and item class. cMaNGOS draws them from loot tables and crafted items
/// (AuctionHouseBot.cpp:182-215); ArcaneCore draws from every item template that passes the same filters (AuctionHouseBot.cpp:226-240):
/// no bind-on-pickup or quest items, no items containing loot, a non-zero quality/class value, and the level caps of
/// ParseLevelConstraints/CalculateItemLevelCap (required level at most MaxRequiredLevel; item level at most MaxRequiredLevel + 5 below 60).
/// Conjured items, items without a price and blacklisted entries are left out too.
/// </summary>
public sealed class AuctionBotItemPool
{
    /// <summary>cMaNGOS DEFAULT_MAX_LEVEL for classic.</summary>
    public const uint DefaultMaxLevel = 60;

    /// <summary>ItemTemplate.Bonding: bind on pickup (cMaNGOS BIND_WHEN_PICKED_UP).</summary>
    public const uint BindOnPickup = 1;

    /// <summary>ItemTemplate.Bonding: quest item (cMaNGOS BIND_QUEST_ITEM).</summary>
    public const uint BindQuestItem = 4;

    private readonly ItemTemplate[] _all;
    private readonly Dictionary<(uint Quality, uint Class), ItemTemplate[]> _groups;
    private readonly HashSet<uint> _entries;

    public AuctionBotItemPool(IEnumerable<ItemTemplate> templates, AuctionBotOptions options, AuctionBotPricing pricing)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pricing);
        HashSet<uint> blacklist = [.. options.BlacklistIds()];
        uint maxItemLevel = options.MaxRequiredLevel >= DefaultMaxLevel ? uint.MaxValue : options.MaxRequiredLevel + 5;
        _all = [.. templates.Where(t => Eligible(t, options.MaxRequiredLevel, maxItemLevel, blacklist, pricing)).OrderBy(t => t.Entry)];
        _entries = [.. _all.Select(t => t.Entry)];
        _groups = _all.GroupBy(t => (t.Quality, t.Class)).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public int Count => _all.Length;

    /// <summary>Every eligible template, by entry.</summary>
    public IReadOnlyList<ItemTemplate> All => _all;

    /// <summary>The eligible templates of one quality and class.</summary>
    public IReadOnlyList<ItemTemplate> Group(uint quality, uint itemClass) => _groups.GetValueOrDefault((quality, itemClass)) ?? [];

    /// <summary>How many eligible templates each quality has (status reports).</summary>
    public IReadOnlyDictionary<uint, int> CountByQuality() => _all.GroupBy(t => t.Quality).ToDictionary(g => g.Key, g => g.Count());

    public bool Contains(uint entry) => _entries.Contains(entry);

    public static bool Eligible(ItemTemplate t, uint maxRequiredLevel, uint maxItemLevel, IReadOnlySet<uint> blacklist, AuctionBotPricing pricing)
        => t.Stackable > 0
            && t.Bonding is not (BindOnPickup or BindQuestItem)
            && !t.HasFlag(ItemTemplateFlags.Lootable)
            && !t.HasFlag(ItemTemplateFlags.Conjured)
            && t.RequiredLevel <= maxRequiredLevel
            && t.ItemLevel <= maxItemLevel
            && !blacklist.Contains(t.Entry)
            && pricing.ClassValue(t) > 0
            && pricing.BuyoutPerItem(t) > 0;
}
