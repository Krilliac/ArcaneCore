using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>One listing the seller wants to create.</summary>
public sealed record AuctionBotSellIntent(uint Entry, uint Count, uint StartBid, uint Buyout, uint Hours);

/// <summary>One player auction the buyer wants to buy out at <see cref="Price"/>.</summary>
public sealed record AuctionBotBuyIntent(uint AuctionId, uint Price);

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

    public AuctionBotPlanner(AuctionBotOptions options, AuctionBotPricing pricing, AuctionBotItemPool pool, Random random)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _blacklist = [.. options.BlacklistIds()];
    }

    public AuctionBotItemPool Pool => _pool;

    /// <summary>cMaNGOS urand(0, 99) &lt; chance.</summary>
    public bool Roll(uint chancePercent) => _random.Next(0, 100) < chancePercent;

    /// <summary>
    /// The sell pass (AuctionHouseBot.cpp:179-254, crafted-item branch :194-215 applied to the template pool): draw between the min and
    /// max number of templates (a negative min makes an empty draw more likely, as cMaNGOS); keep a white item always, green 1 in 2, blue
    /// 1 in 4, purple 1 in 8 (poor items are never drawn); give each a stack of StackPercentMin..Max of its stack size (at least 1);
    /// then split each entry's total into full stacks, all priced from one value with variance: buyout = value × count, starting bid
    /// BidMin..Max percent of it (at least 1 copper, which ArcaneCore's auction house requires), duration TimeMin..Max hours.
    /// At most <paramref name="room"/> listings.
    /// </summary>
    public IReadOnlyList<AuctionBotSellIntent> PlanSell(int room)
    {
        if (room <= 0 || _pool.Count == 0 || _options.TemplatesPerSellMax <= 0 || _options.StackPercentMax == 0)
        {
            return [];
        }

        int min = _options.TemplatesPerSellMin;
        int max = _options.TemplatesPerSellMax;
        int draws = min < 0 ? _random.Next(0, max - min + 1) + min : _random.Next(min, max + 1);
        var totals = new Dictionary<uint, uint>();
        var order = new List<ItemTemplate>();
        for (int i = 0; i < draws; i++)
        {
            ItemTemplate t = _pool.All[_random.Next(_pool.Count)];
            if (t.Quality == 0 || t.Quality >= 31 || _random.NextInt64(0, 1L << (int)(t.Quality - 1)) > 0)
            {
                continue;
            }

            uint percent = (uint)_random.Next((int)_options.StackPercentMin, (int)_options.StackPercentMax + 1);
            uint count = (uint)Math.Round(t.Stackable * (double)percent / 100.0, MidpointRounding.AwayFromZero);
            count = Math.Max(count, 1);
            if (!totals.TryAdd(t.Entry, count))
            {
                totals[t.Entry] += count;
            }
            else
            {
                order.Add(t);
            }
        }

        var intents = new List<AuctionBotSellIntent>();
        foreach (ItemTemplate t in order)
        {
            uint value = _pricing.WithVariance(_pricing.BuyoutPerItem(t), _random);
            uint total = totals[t.Entry];
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
    /// The buy pass (AuctionHouseBot.cpp:255-287): for each player auction with a buyout, the bot's value with variance times the stack,
    /// times Buy.Value percent (cMaNGOS reads AuctionHouseBot.Buy.Value but never applies it; ArcaneCore applies it as the config text
    /// describes); a buyout below that is bought out. The bot's own auctions, blacklisted items and items it would not value are skipped.
    /// cMaNGOS also places plain bids when the buyout is too high; ArcaneCore's buyer only buys out (a bot bid has no character to
    /// refund when outbid).
    /// </summary>
    public IReadOnlyList<AuctionBotBuyIntent> PlanBuy(IEnumerable<AuctionRecord> auctions, Func<uint, ItemTemplate?> templates)
    {
        ArgumentNullException.ThrowIfNull(auctions);
        ArgumentNullException.ThrowIfNull(templates);
        var intents = new List<AuctionBotBuyIntent>();
        foreach (AuctionRecord auction in auctions)
        {
            if (auction.SellerId == 0 || auction.Buyout == 0 || _blacklist.Contains(auction.ItemEntry)
                || templates(auction.ItemEntry) is not { } template)
            {
                continue;
            }

            ulong check = (ulong)_pricing.WithVariance(_pricing.BuyoutPerItem(template), _random) * auction.ItemCount * _options.BuyValuePercent / 100;
            if (check > auction.Buyout)
            {
                intents.Add(new AuctionBotBuyIntent(auction.Id, auction.Buyout));
            }
        }

        return intents;
    }
}
