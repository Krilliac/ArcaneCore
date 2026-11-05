using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>
/// The items the bot may list, grouped by quality and item class. cMaNGOS draws them from loot tables and crafted items
/// (AuctionHouseBot.cpp:182-215); ArcaneCore draws from every item template that passes the same filters (AuctionHouseBot.cpp:226-240):
/// no bind-on-pickup or quest items, no items containing loot, a non-zero quality/class value, and the level caps of
/// ParseLevelConstraints/CalculateItemLevelCap (required level at most the max required level, which is <c>MaxRequiredLevel</c> or, with
/// <c>DynamicMaxRequiredLevel</c>, the highest online level; item level at most <c>MaxItemLevel</c> when set, else that level + 5 below 60).
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

    /// <param name="maxRequiredLevel">The required level cap in force (the dynamic level); null takes <see cref="AuctionBotOptions.MaxRequiredLevel"/>.</param>
    public AuctionBotItemPool(IEnumerable<ItemTemplate> templates, AuctionBotOptions options, AuctionBotPricing pricing, uint? maxRequiredLevel = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pricing);
        HashSet<uint> blacklist = [.. options.BlacklistIds()];
        uint requiredCap = maxRequiredLevel ?? options.MaxRequiredLevel;
        uint maxItemLevel = ItemLevelCap(requiredCap, options.MaxItemLevel);
        _all = [.. templates.Where(t => Eligible(t, requiredCap, maxItemLevel, blacklist, pricing)).OrderBy(t => t.Entry)];
        _entries = [.. _all.Select(t => t.Entry)];
        _groups = _all.GroupBy(t => (t.Quality, t.Class)).ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>
    /// The item level cap (cMaNGOS CalculateItemLevelCap): <paramref name="configuredMaxItemLevel"/> when it is not 0, else no cap from a
    /// required level cap of 60 or more, else the required level cap + 5 (a typical item level is its required level + 5).
    /// </summary>
    public static uint ItemLevelCap(uint maxRequiredLevel, uint configuredMaxItemLevel)
        => configuredMaxItemLevel != 0 ? configuredMaxItemLevel : maxRequiredLevel >= DefaultMaxLevel ? uint.MaxValue : maxRequiredLevel + 5;

    /// <summary>
    /// The levels that count for the dynamic cap: every online level, or with <paramref name="ignoreGmAccounts"/> only those of characters whose
    /// account is not a staff account (cMaNGOS AuctionHouseBot.Level.IgnoreGmAccounts). When that leaves nobody but someone is online (only
    /// staff), the levels of the staff characters count, so the cap follows the highest GM level (cMaNGOS AuctionHouseBot.cpp:498-502, "No
    /// non-GM players online. Fallback max required level (including GMs)").
    /// </summary>
    public static IEnumerable<uint> CountedOnlineLevels(IEnumerable<(uint Level, bool IsGmAccount)> online, bool ignoreGmAccounts)
    {
        ArgumentNullException.ThrowIfNull(online);
        (uint Level, bool IsGmAccount)[] all = [.. online];
        uint[] counted = [.. all.Where(o => !ignoreGmAccounts || !o.IsGmAccount).Select(o => o.Level)];
        return counted.Length == 0 ? all.Select(o => o.Level).ToArray() : counted;
    }

    /// <summary>
    /// The dynamic required level cap (cMaNGOS UpdateDynamicMaxLevel): the highest of <paramref name="onlineLevels"/>, kept within 1-255;
    /// with nobody online the cap in force (<paramref name="current"/>) stays.
    /// </summary>
    public static uint DynamicMaxLevel(IEnumerable<uint> onlineLevels, uint current)
    {
        ArgumentNullException.ThrowIfNull(onlineLevels);
        uint highest = 0;
        foreach (uint level in onlineLevels)
        {
            highest = Math.Max(highest, level);
        }

        return highest == 0 ? current : Math.Clamp(highest, 1u, 255u);
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
