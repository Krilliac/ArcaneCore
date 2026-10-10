using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>Item values of the auction house bot (cMaNGOS AuctionHouseBot::CalculateBuyoutPrice and ValueWithVariance).</summary>
public sealed class AuctionBotPricing
{
    private readonly uint[][] _values;
    private readonly IReadOnlySet<uint> _vendorItems;
    private readonly bool _vendorValue;
    private readonly uint _variance;

    public AuctionBotPricing(AuctionBotOptions options, IReadOnlySet<uint>? vendorItems = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _values = options.ParseValues();
        _vendorItems = vendorItems ?? new HashSet<uint>();
        _vendorValue = options.VendorValue;
        _variance = options.ValueVariance;
    }

    /// <summary>The configured percent for a quality and class; 0 filters the item out (cMaNGOS m_itemValue[quality][class]).</summary>
    public uint ClassValue(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Quality < AuctionBotOptions.QualityCount && template.Class < AuctionBotOptions.ItemClassCount
            ? _values[template.Quality][template.Class]
            : 0;
    }

    /// <summary>
    /// cMaNGOS CalculateBuyoutPrice (AuctionHouseBot.cpp:613-625): the vendor buy price, or the sell price times 4 (poor and common) or 5
    /// when there is no buy price or it is more than 5 times the sell price (arrows and shells); then times the quality/class percent, or
    /// 100 % for an item a vendor sells when Value.Vendor is on. Computed in 64 bits and capped at the client's price limit.
    /// </summary>
    public uint BuyoutPerItem(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ulong price = template.BuyPrice;
        if (price == 0 || (template.SellPrice > 0 && price / template.SellPrice > 5))
        {
            price = (ulong)template.SellPrice * (template.Quality <= 1 ? 4u : 5u);
        }

        uint percent = _vendorValue && _vendorItems.Contains(template.Entry) ? 100 : ClassValue(template);
        return (uint)Math.Min(price * percent / 100, AuctionHouseRules.MaxPrice);
    }

    /// <summary>
    /// cMaNGOS ValueWithVariance (AuctionHouseBot.h): value + (urand(0, 2v + 1) − v) × (value / 100), where urand is inclusive, so the
    /// step runs from −v to v + 1. Never below 0.
    /// </summary>
    public uint WithVariance(uint value, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        long step = random.NextInt64(0, (2L * _variance) + 2) - _variance;
        long result = value + (step * (value / 100));
        return (uint)Math.Clamp(result, 0, AuctionHouseRules.MaxPrice);
    }
}
