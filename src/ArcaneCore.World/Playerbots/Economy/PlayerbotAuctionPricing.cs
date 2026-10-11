using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Auction prices of the economy bot, pure functions of the item template, the listings in the house and the house rules.
/// <para>
/// References: the reference price follows the mangoszero auction house bot's default price (<c>PricingStrategy::GetDefaultBuyPrice</c>,
/// src/modules/Bots/ahbot/PricingStrategy.cpp:196-226, defaults of ahbot.conf.dist.in:33-35): the larger of the vendor sell price and a
/// quarter of the buy price, raised to <c>DefaultMinPrice * level^2 / 10</c> (level = the larger of item level and required level) and to 100
/// copper, times the square root of the quality for any quality above poor. Deposit and cut are the house's own (vmangos
/// <c>AuctionHouseMgr::GetAuctionDeposit</c> :98-110 and <c>GetAuctionCut</c> :845-848, <see cref="AuctionHouseRules"/>): a sale returns the
/// deposit with the proceeds (bid + deposit - cut, :226), an expiry loses it and returns the item by mail.
/// </para>
/// </summary>
internal static class PlayerbotAuctionPricing
{
    /// <summary>The listing duration, 8 hours: a middle one of the 2/8/24 hours the client offers (<see cref="AuctionHouseRules.DurationsMinutes"/>).</summary>
    internal const uint DurationMinutes = 480;

    /// <summary>mangoszero <c>AhBot.DefaultMinPrice</c>.</summary>
    internal const uint DefaultMinPrice = 20;

    /// <summary>mangoszero's lowest default price in copper (<c>max(price, 100)</c>).</summary>
    internal const uint MinimumReference = 100;

    /// <summary>The start bid as a share of the buyout, in percent.</summary>
    internal const uint StartBidPercent = 80;

    /// <summary>A listing under a competitor asks this share less (at least one copper).</summary>
    internal const uint UndercutPercent = 1;

    /// <summary>The most a listing asks per unit, as a multiple of the reference price.</summary>
    internal const uint CapMultiple = 2;

    /// <summary>
    /// The reference price of one unit in copper; 0 for an item with no vendor sell price (it is never listed: the bot has no
    /// price evidence for it and vendoring it is not possible either).
    /// </summary>
    internal static uint ReferencePerUnit(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.SellPrice == 0) return 0;
        ulong price = Math.Max(template.SellPrice, template.BuyPrice / 4);
        ulong level = Math.Max(template.ItemLevel, template.RequiredLevel);
        price = Math.Max(price, DefaultMinPrice * level * level / 10);
        price = Math.Max(price, MinimumReference);
        if (template.Quality > 0) price = (ulong)(price * Math.Sqrt(template.Quality));
        return (uint)Math.Min(price, AuctionHouseRules.MaxPrice);
    }

    /// <summary>
    /// The smallest buyout per unit whose proceeds after the house's cut still beat vendoring the unit:
    /// <c>ceil(sell * 100 / (100 - cut%)) + 1</c>. <see cref="uint.MaxValue"/> when the cut takes everything.
    /// </summary>
    internal static uint VendorFloorPerUnit(uint sellPrice, float cutPercent)
    {
        if (!float.IsFinite(cutPercent) || cutPercent >= 100f) return uint.MaxValue;
        double floor = Math.Ceiling(sellPrice * 100.0 / (100.0 - Math.Max(0f, cutPercent))) + 1;
        return floor >= uint.MaxValue ? uint.MaxValue : (uint)floor;
    }

    /// <summary>
    /// The lowest buyout per unit that other sellers ask for <paramref name="entry"/> (0 when there is none). A bid-only auction (buyout 0),
    /// the bot's own listing and a row of another item (a name search also matches longer names) do not count.
    /// </summary>
    internal static uint LowestCompetitorPerUnit(IReadOnlyList<PlayerbotAuctionRow> rows, uint entry, ulong self)
    {
        ArgumentNullException.ThrowIfNull(rows);
        uint lowest = 0;
        foreach (PlayerbotAuctionRow row in rows)
        {
            if (row.Entry != entry || row.Buyout == 0 || row.Count == 0 || row.Owner == self) continue;
            uint perUnit = Math.Max(1u, row.Buyout / row.Count);
            if (lowest == 0 || perUnit < lowest) lowest = perUnit;
        }

        return lowest;
    }

    /// <summary>
    /// The buyout in copper to ask for <paramref name="count"/> units, or 0 when the item must not be listed (no sell price, or the
    /// vendor floor lies above twice the reference). Per unit: one percent (at least one copper) under the lowest competing buyout, else
    /// the reference price; never below <see cref="VendorFloorPerUnit"/>, never above <see cref="CapMultiple"/> times the reference; the
    /// total is capped at the client price limit (<see cref="AuctionHouseRules.MaxPrice"/>).
    /// </summary>
    internal static uint ListingPrice(ItemTemplate template, uint count, IReadOnlyList<PlayerbotAuctionRow> rows, ulong self,
        AuctionHouseEntry house, float cutRate = 1f)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(house);
        uint reference = ReferencePerUnit(template);
        if (reference == 0 || count == 0) return 0;
        ulong floor = VendorFloorPerUnit(template.SellPrice, house.CutPercent * cutRate);
        ulong cap = (ulong)reference * CapMultiple;
        if (floor > cap) return 0;
        uint lowest = LowestCompetitorPerUnit(rows, template.Entry, self);
        ulong perUnit = reference;
        if (lowest != 0)
        {
            ulong undercut = Math.Max(1u, lowest * UndercutPercent / 100);
            perUnit = lowest > undercut ? lowest - undercut : lowest;
        }

        perUnit = Math.Clamp(perUnit, floor, cap);
        return (uint)Math.Min(perUnit * count, AuctionHouseRules.MaxPrice);
    }

    /// <summary>The start bid: <see cref="StartBidPercent"/> of the buyout, at least 1 copper, never above the buyout (the server refuses a bid above the buyout).</summary>
    internal static uint StartBid(uint buyout)
        => buyout == 0 ? 0 : Math.Min(buyout, Math.Max(1u, (uint)((ulong)buyout * StartBidPercent / 100)));

    /// <summary>The deposit the house takes for <paramref name="count"/> units listed for <see cref="DurationMinutes"/> (<see cref="AuctionHouseRules.Deposit"/>).</summary>
    internal static uint Deposit(AuctionHouseEntry house, ItemTemplate template, uint count, uint depositMinimum = 0, float depositRate = 1f)
    {
        ArgumentNullException.ThrowIfNull(house);
        ArgumentNullException.ThrowIfNull(template);
        return AuctionHouseRules.Deposit(house, template.SellPrice, count, DurationMinutes, depositMinimum, depositRate);
    }

    /// <summary>
    /// Whether a listing pays: a sale returns the deposit, an expiry loses it, so the deposit lost on one expiry must stay below the
    /// margin a sale leaves over vendoring the stack (buyout - cut - sell price), and the deposit must fit the budget left.
    /// </summary>
    internal static bool IsWorthListing(ItemTemplate template, uint count, uint buyout, uint deposit, AuctionHouseEntry house,
        float cutRate, uint remainingBudget)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(house);
        if (buyout == 0 || count == 0 || template.SellPrice == 0) return false;
        uint cut = AuctionHouseRules.Cut(house, buyout, cutRate);
        long margin = (long)buyout - cut - ((long)template.SellPrice * count);
        return margin > 0 && deposit < margin && deposit <= remainingBudget;
    }
}
