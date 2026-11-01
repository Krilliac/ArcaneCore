using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Where the trade desk gets the price of one unit of an item. This is the seam the price book (design lane T1) implements: the book's median when
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookMinSellers"/> distinct sellers listed the item within
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookHours"/>, else the reference price. <see cref="PlayerbotPriceBookDealPrices"/> is that source;
/// <see cref="PlayerbotReferenceDealPrices"/> is the reference-only one.
/// </summary>
internal interface IPlayerbotDealPriceSource
{
    /// <summary>The price in copper of one unit of <paramref name="template"/>; 0 when there is no price evidence (no deal is made then).</summary>
    uint ReferencePerUnit(ItemTemplate template);
}

/// <summary>The reference price of the economy bot's auction pricing (<see cref="PlayerbotAuctionPricing.ReferencePerUnit"/>); stateless.</summary>
internal sealed class PlayerbotReferenceDealPrices : IPlayerbotDealPriceSource
{
    /// <summary>The one instance.</summary>
    public static PlayerbotReferenceDealPrices Instance { get; } = new();

    private PlayerbotReferenceDealPrices()
    {
    }

    public uint ReferencePerUnit(ItemTemplate template) => PlayerbotAuctionPricing.ReferencePerUnit(template);
}

/// <summary>
/// The price book (<c>World:Playerbots:Social:Trading:Deals:PriceBookHours</c>, <c>PriceBookMinSellers</c>): what the auction house asked per unit, as the bots saw it
/// on their visits. Per item it keeps one observation per seller (the seller's latest lowest per-unit buyout and when it was seen); the book's price is the median
/// over the sellers seen within the window, once enough distinct sellers are in it. Thread-safe (one lock). Memory is bounded: at most <see cref="MaxItems"/> items
/// and <see cref="MaxSellersPerItem"/> sellers per item (the oldest goes first).
/// </summary>
internal sealed class PlayerbotPriceBook(Func<long> clockUnixMs)
{
    /// <summary>The most items the book tracks; an observation of a new item beyond this drops the item with the oldest newest-observation.</summary>
    internal const int MaxItems = 4096;

    /// <summary>The most sellers kept per item.</summary>
    internal const int MaxSellersPerItem = 64;

    private const long HourMs = 3_600_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Dictionary<ulong, (uint PerUnit, long SeenMs)>> _items = [];

    /// <summary>The number of items tracked.</summary>
    internal int ItemCount
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    /// <summary>
    /// Notes the rows of one auction list at the book's clock. A row with no buyout (bid-only) or no count carries no price and is skipped. Several rows of one
    /// seller and item in the list count as one observation at the seller's lowest per-unit buyout.
    /// </summary>
    internal void Observe(IReadOnlyList<PlayerbotAuctionRow> rows) => Observe(rows, clockUnixMs());

    /// <summary>As <see cref="Observe(IReadOnlyList{PlayerbotAuctionRow})"/> at an explicit time (Unix milliseconds).</summary>
    internal void Observe(IReadOnlyList<PlayerbotAuctionRow> rows, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;
        var batch = new Dictionary<(uint Entry, ulong Owner), uint>();
        foreach (PlayerbotAuctionRow row in rows)
        {
            if (row.Entry == 0 || row.Buyout == 0 || row.Count == 0) continue;
            uint perUnit = Math.Max(1u, row.Buyout / row.Count);
            var key = (row.Entry, row.Owner);
            batch[key] = batch.TryGetValue(key, out uint known) ? Math.Min(known, perUnit) : perUnit;
        }

        if (batch.Count == 0) return;
        lock (_gate)
        {
            foreach (KeyValuePair<(uint Entry, ulong Owner), uint> seen in batch)
            {
                if (!_items.TryGetValue(seen.Key.Entry, out Dictionary<ulong, (uint PerUnit, long SeenMs)>? sellers))
                {
                    if (_items.Count >= MaxItems) EvictOldestItem();
                    sellers = [];
                    _items[seen.Key.Entry] = sellers;
                }

                if (!sellers.ContainsKey(seen.Key.Owner) && sellers.Count >= MaxSellersPerItem) EvictOldestSeller(sellers);
                sellers[seen.Key.Owner] = (seen.Value, nowMs);
            }
        }
    }

    /// <summary>
    /// The median per-unit buyout of <paramref name="entry"/> over the distinct sellers seen within <paramref name="hours"/> hours of the book's clock, or null when fewer than
    /// <paramref name="minSellers"/> (at least 1) distinct sellers were. With an even number of sellers it is the mean of the two middle values, rounded down.
    /// </summary>
    internal uint? Median(uint entry, int hours, int minSellers) => Median(entry, hours, minSellers, clockUnixMs());

    /// <summary>As <see cref="Median(uint, int, int)"/> at an explicit time (Unix milliseconds).</summary>
    internal uint? Median(uint entry, int hours, int minSellers, long nowMs)
    {
        long window = Math.Max(1, hours) * HourMs;
        int needed = Math.Max(1, minSellers);
        uint[] values;
        lock (_gate)
        {
            if (!_items.TryGetValue(entry, out Dictionary<ulong, (uint PerUnit, long SeenMs)>? sellers)) return null;
            List<ulong>? stale = null;
            foreach (KeyValuePair<ulong, (uint PerUnit, long SeenMs)> seller in sellers)
                if (nowMs - seller.Value.SeenMs > window) (stale ??= []).Add(seller.Key);
            if (stale is not null)
            {
                foreach (ulong owner in stale) sellers.Remove(owner);
                if (sellers.Count == 0) _items.Remove(entry);
            }

            if (sellers.Count < needed) return null;
            values = new uint[sellers.Count];
            int i = 0;
            foreach ((uint perUnit, long _) in sellers.Values) values[i++] = perUnit;
        }

        Array.Sort(values);
        int mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (uint)(((ulong)values[mid - 1] + values[mid]) / 2);
    }

    // Both evictions run under _gate.
    private void EvictOldestItem()
    {
        uint oldest = 0;
        long oldestSeen = long.MaxValue;
        foreach (KeyValuePair<uint, Dictionary<ulong, (uint PerUnit, long SeenMs)>> item in _items)
        {
            long newest = long.MinValue;
            foreach ((uint _, long seen) in item.Value.Values) newest = Math.Max(newest, seen);
            if (newest < oldestSeen)
            {
                oldestSeen = newest;
                oldest = item.Key;
            }
        }

        _items.Remove(oldest);
    }

    private static void EvictOldestSeller(Dictionary<ulong, (uint PerUnit, long SeenMs)> sellers)
    {
        ulong oldest = 0;
        long oldestSeen = long.MaxValue;
        foreach (KeyValuePair<ulong, (uint PerUnit, long SeenMs)> seller in sellers)
            if (seller.Value.SeenMs < oldestSeen)
            {
                oldestSeen = seller.Value.SeenMs;
                oldest = seller.Key;
            }

        sellers.Remove(oldest);
    }
}

/// <summary>
/// The trade desk's price source backed by the <see cref="PlayerbotPriceBook"/>: the book's median when
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookMinSellers"/> distinct sellers listed the item within
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookHours"/>, else the reference price. An item with no vendor sell price has no price at all (0), book or not,
/// as for the reference source. The options are read live on every call.
/// </summary>
internal sealed class PlayerbotPriceBookDealPrices(PlayerbotPriceBook book, PlayerbotOptions options) : IPlayerbotDealPriceSource
{
    /// <summary>The book this source reads.</summary>
    internal PlayerbotPriceBook Book => book;

    public uint ReferencePerUnit(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        uint reference = PlayerbotAuctionPricing.ReferencePerUnit(template);
        if (reference == 0) return 0;
        PlayerbotSocialTradingDealsOptions deals = options.Social.Trading.Deals;
        uint? median = book.Median(template.Entry, deals.PriceBookHours, deals.PriceBookMinSellers);
        return median is > 0 ? Math.Min(median.Value, AuctionHouseRules.MaxPrice) : reference;
    }
}
