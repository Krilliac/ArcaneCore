using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>One listing the seller wants to create.</summary>
public sealed record AuctionBotSellIntent(uint Entry, uint Count, uint StartBid, uint Buyout, uint Hours);

/// <summary>One player auction the buyer wants to buy out (<see cref="Bid"/> false) or bid on (true) at <see cref="Price"/>.</summary>
public sealed record AuctionBotBuyIntent(uint AuctionId, uint Price, bool Bid = false);

/// <summary>
/// The seller and buyer decisions of cMaNGOS AuctionHouseBot::Update (AuctionHouseBot.cpp:159-288), without side effects: the World
/// side reserves custody and commits each intent.
/// </summary>
public sealed class AuctionBotPlanner
{
    private readonly AuctionBotOptions _options;
    private readonly AuctionBotPricing _pricing;
    private readonly AuctionBotItemPool _pool;
    private readonly HashSet<uint> _blacklist;
    private readonly Random _random;
    private IReadOnlyDictionary<uint, AuctionBotItemOverride> _overrides = new Dictionary<uint, AuctionBotItemOverride>();

    public AuctionBotPlanner(AuctionBotOptions options, AuctionBotPricing pricing, AuctionBotItemPool pool, Random random)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _blacklist = [.. options.BlacklistIds()];
    }

    public AuctionBotItemPool Pool => _pool;

    public AuctionBotPricing Pricing => _pricing;

    /// <summary>The <c>ahbot_items</c> rows by item (cMaNGOS m_itemData).</summary>
    public IReadOnlyDictionary<uint, AuctionBotItemOverride> Overrides
    {
        get => _overrides;
        set => _overrides = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>cMaNGOS urand(0, 99) &lt; chance.</summary>
    public bool Roll(uint chancePercent) => _random.Next(0, 100) < chancePercent;

    /// <summary>The bot's value of one item before variance: the <c>ahbot_items</c> value when overridden, else CalculateBuyoutPrice; 0 = never.</summary>
    public uint ValuePerItem(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return _overrides.TryGetValue(template.Entry, out AuctionBotItemOverride? o) ? o.Value : _pricing.BuyoutPerItem(template);
    }

    /// <summary>
    /// The sell pass (AuctionHouseBot.cpp:179-254). The item map is built from <paramref name="lootItems"/> (the loot sources the World
    /// side rolled), then the profession draw applied to the template pool: draw between the min and max number of templates (a negative
    /// min makes an empty draw more likely, as cMaNGOS); keep a white item always, green 1 in 2, blue 1 in 4, purple 1 in 8 (poor items
    /// are never drawn); give each a stack of StackPercentMin..Max of its stack size (at least 1). Then every <c>ahbot_items</c> row with
    /// an add chance replaces its entry: AddChance percent to list MinAmount..MaxAmount, else nothing. An overridden item with value 0
    /// is never listed; one without an add chance must pass the pool filters (no BoP or quest items, no containers, class value, level
    /// caps, blacklist). Each entry's total is split into full stacks, all priced from one value with variance: buyout = value × count,
    /// starting bid BidMin..Max percent of it (at least 1 copper, which ArcaneCore's auction house requires), duration TimeMin..Max
    /// hours. At most <paramref name="room"/> listings.
    /// </summary>
    public IReadOnlyList<AuctionBotSellIntent> PlanSell(int room, Func<uint, ItemTemplate?>? templates = null,
        IEnumerable<(uint Entry, uint Count)>? lootItems = null)
    {
        if (room <= 0)
        {
            return [];
        }

        var totals = new Dictionary<uint, uint>();
        var order = new List<uint>();
        void Add(uint entry, uint count)
        {
            if (count == 0)
            {
                return;
            }

            if (totals.TryGetValue(entry, out uint have))
            {
                totals[entry] = (uint)Math.Min((ulong)have + count, uint.MaxValue);
            }
            else
            {
                totals[entry] = count;
                order.Add(entry);
            }
        }

        foreach ((uint entry, uint count) in lootItems ?? [])
        {
            Add(entry, count);
        }

        if (_pool.Count > 0 && _options.TemplatesPerSellMax > 0 && _options.StackPercentMax > 0)
        {
            int min = _options.TemplatesPerSellMin;
            int max = _options.TemplatesPerSellMax;
            int draws = min < 0 ? _random.Next(0, max - min + 1) + min : _random.Next(min, max + 1);
            for (int i = 0; i < draws; i++)
            {
                ItemTemplate t = _pool.All[_random.Next(_pool.Count)];
                if (t.Quality == 0 || t.Quality >= 31 || _random.NextInt64(0, 1L << (int)(t.Quality - 1)) > 0)
                {
                    continue;
                }

                uint percent = (uint)_random.Next((int)_options.StackPercentMin, (int)_options.StackPercentMax + 1);
                uint count = (uint)Math.Round(t.Stackable * (double)percent / 100.0, MidpointRounding.AwayFromZero);
                Add(t.Entry, Math.Max(count, 1));
            }
        }

        foreach (AuctionBotItemOverride o in _overrides.Values.Where(o => o.AddChance > 0).OrderBy(o => o.Item))
        {
            uint count = Roll(o.AddChance) ? (uint)_random.NextInt64(Math.Min(o.MinAmount, o.MaxAmount), (long)Math.Max(o.MinAmount, o.MaxAmount) + 1) : 0;
            if (count == 0)
            {
                if (totals.Remove(o.Item))
                {
                    order.Remove(o.Item);
                }
            }
            else
            {
                if (!totals.ContainsKey(o.Item))
                {
                    order.Add(o.Item);
                }

                totals[o.Item] = count;
            }
        }

        var intents = new List<AuctionBotSellIntent>();
        foreach (uint entry in order)
        {
            ItemTemplate? t = templates?.Invoke(entry) ?? _pool.All.FirstOrDefault(p => p.Entry == entry);
            if (t is null || t.Stackable == 0)
            {
                continue;
            }

            bool overridden = _overrides.TryGetValue(entry, out AuctionBotItemOverride? o);
            if ((overridden && o!.Value == 0) || ((!overridden || o!.AddChance == 0) && !_pool.Contains(entry)))
            {
                continue;
            }

            uint value = _pricing.WithVariance(ValuePerItem(t), _random);
            uint total = totals[entry];
            for (uint listed = 0; listed < total; listed += t.Stackable)
            {
                uint count = Math.Min(t.Stackable, total - listed);
                ulong buyout = (ulong)value * count;
                if (buyout == 0 || buyout > AuctionHouseRules.MaxPrice)
                {
                    continue;
                }

                ulong bid = buyout * (ulong)_random.Next((int)_options.BidMinPercent, (int)_options.BidMaxPercent + 1) / 100;
                uint hours = (uint)_random.Next((int)_options.TimeMinHours, (int)_options.TimeMaxHours + 1);
                intents.Add(new AuctionBotSellIntent(t.Entry, count, (uint)Math.Max(bid, 1), (uint)buyout, hours));
                if (intents.Count >= room)
                {
                    return intents;
                }
            }
        }

        return intents;
    }

    /// <summary>
    /// The buy pass (AuctionHouseBot.cpp:255-287): for each player auction, the bot's value with variance times the stack, times
    /// Buy.Value percent (cMaNGOS reads AuctionHouseBot.Buy.Value but never applies it; ArcaneCore applies it as the config text
    /// describes). A buyout below that is bought out; otherwise, with <see cref="AuctionBotOptions.Bidding"/>, a next bid (current bid +
    /// outbid step, at least the start bid) below it is bid. The bot's own auctions (cMaNGOS also bids on those once a player bid),
    /// auctions where the bot's bid already stands, blacklisted items and items it would not value are skipped.
    /// </summary>
    public IReadOnlyList<AuctionBotBuyIntent> PlanBuy(IEnumerable<AuctionRecord> auctions, Func<uint, ItemTemplate?> templates)
    {
        ArgumentNullException.ThrowIfNull(auctions);
        ArgumentNullException.ThrowIfNull(templates);
        var intents = new List<AuctionBotBuyIntent>();
        foreach (AuctionRecord auction in auctions)
        {
            if (auction.SellerId == 0 || (auction.BidderId == 0 && auction.Bid > 0) || _blacklist.Contains(auction.ItemEntry)
                || templates(auction.ItemEntry) is not { } template)
            {
                continue;
            }

            ulong check = (ulong)_pricing.WithVariance(ValuePerItem(template), _random) * auction.ItemCount * _options.BuyValuePercent / 100;
            if (check == 0)
            {
                continue;
            }

            uint next = AuctionHouseRules.MinimumBid(auction);
            if (auction.Buyout > 0 && check > auction.Buyout)
            {
                intents.Add(new AuctionBotBuyIntent(auction.Id, auction.Buyout));
            }
            else if (_options.Bidding && check > next && (auction.Buyout == 0 || next < auction.Buyout))
            {
                intents.Add(new AuctionBotBuyIntent(auction.Id, next, Bid: true));
            }
        }

        return intents;
    }
}
