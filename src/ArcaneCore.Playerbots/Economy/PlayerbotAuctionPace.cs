using System.Runtime.CompilerServices;
using ArcaneCore.World.Economy;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The hourly pace of all bots together at each auction house: per house id one <see cref="PlayerbotHourlyWindow"/> of buyouts and one of
/// listings. One per world (each scenario world has its own), found through <see cref="For"/>; it lives as long as that world's
/// <see cref="EconomyFeature"/>. World thread only.
/// </summary>
internal sealed class PlayerbotAuctionPace
{
    private static readonly ConditionalWeakTable<EconomyFeature, PlayerbotAuctionPace> Paces = new();

    private readonly Dictionary<uint, PlayerbotHourlyWindow> _buyouts = [];
    private readonly Dictionary<uint, PlayerbotHourlyWindow> _listings = [];

    /// <summary>The pace of the world that <paramref name="economy"/> belongs to.</summary>
    internal static PlayerbotAuctionPace For(EconomyFeature economy)
    {
        ArgumentNullException.ThrowIfNull(economy);
        return Paces.GetValue(economy, static _ => new PlayerbotAuctionPace());
    }

    /// <summary>Whether another bot may buy out at <paramref name="houseId"/> now under <paramref name="limit"/> an hour.</summary>
    internal bool MayBuy(uint houseId, uint now, int limit) => limit > 0 && Buyouts(houseId, now) < limit;

    /// <summary>Whether another bot may list at <paramref name="houseId"/> now under <paramref name="limit"/> an hour.</summary>
    internal bool MayList(uint houseId, uint now, int limit) => limit > 0 && Listings(houseId, now) < limit;

    /// <summary>Notes a buyout at <paramref name="houseId"/>.</summary>
    internal void NoteBuyout(uint houseId, uint now, int limit) => WindowOf(_buyouts, houseId).Note(now, limit);

    /// <summary>Notes a listing at <paramref name="houseId"/>.</summary>
    internal void NoteListing(uint houseId, uint now, int limit) => WindowOf(_listings, houseId).Note(now, limit);

    /// <summary>The buyouts at <paramref name="houseId"/> in the last hour.</summary>
    internal int Buyouts(uint houseId, uint now) => _buyouts.TryGetValue(houseId, out PlayerbotHourlyWindow? window) ? window.CountAt(now) : 0;

    /// <summary>The listings at <paramref name="houseId"/> in the last hour.</summary>
    internal int Listings(uint houseId, uint now) => _listings.TryGetValue(houseId, out PlayerbotHourlyWindow? window) ? window.CountAt(now) : 0;

    private static PlayerbotHourlyWindow WindowOf(Dictionary<uint, PlayerbotHourlyWindow> windows, uint houseId)
    {
        if (!windows.TryGetValue(houseId, out PlayerbotHourlyWindow? window)) windows[houseId] = window = new PlayerbotHourlyWindow();
        return window;
    }
}
