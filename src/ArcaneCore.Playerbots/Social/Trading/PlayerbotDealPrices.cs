using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Where the trade desk gets the price of one unit of an item. This is the seam the price book (design lane T1) implements: the book's median when
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookMinSellers"/> distinct sellers listed the item within
/// <see cref="PlayerbotSocialTradingDealsOptions.PriceBookHours"/>, else the reference price. Until then <see cref="PlayerbotReferenceDealPrices"/> is the only source.
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
