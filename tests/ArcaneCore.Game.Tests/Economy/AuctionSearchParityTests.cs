using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// Auction browse against vmangos AuctionHouseObject::BuildListAuctionItems
/// (D:\refs\vmangos\src\game\AuctionHouse\AuctionHouseMgr.cpp:71-75 the list is keyed by buyout price, :760-767 filters).
/// </summary>
public sealed class AuctionSearchParityTests
{
    private static readonly AuctionQuery All = new(0, "", 0, 0, AuctionSearch.Any, AuctionSearch.Any, AuctionSearch.Any, AuctionSearch.Any, false);

    private static ItemTemplate? Find(uint entry) => entry switch
    {
        1 => new ItemTemplate { Entry = 1, Name = "Cheap Sword", Class = 2, InventoryType = 13, RequiredLevel = 5 },
        2 => new ItemTemplate { Entry = 2, Name = "Robe of Test", Class = 4, InventoryType = 20, RequiredLevel = 50 },
        3 => new ItemTemplate { Entry = 3, Name = "Plate Chest", Class = 4, InventoryType = 5, RequiredLevel = 20 },
        _ => null,
    };

    [Fact]
    public void Pages_follow_the_buyout_price_then_the_auction_id()
    {
        AuctionRecord[] auctions =
        [
            new() { Id = 1, ItemEntry = 1, Buyout = 500 },
            new() { Id = 2, ItemEntry = 1, Buyout = 0 },
            new() { Id = 3, ItemEntry = 1, Buyout = 100 },
            new() { Id = 4, ItemEntry = 1, Buyout = 100 },
        ];
        (IReadOnlyList<AuctionRecord> page, int total) = AuctionSearch.Run(auctions, All, Find);
        Assert.Equal([2u, 3u, 4u, 1u], page.Select(a => a.Id));
        Assert.Equal(4, total);
    }

    [Fact]
    public void Which_fifty_rows_a_page_shows_depends_on_the_buyout_order()
    {
        List<AuctionRecord> auctions = [.. Enumerable.Range(1, 60).Select(i => new AuctionRecord { Id = (uint)i, ItemEntry = 1, Buyout = (uint)(1000 - i) })];
        (IReadOnlyList<AuctionRecord> page, _) = AuctionSearch.Run(auctions, All, Find);
        Assert.Equal(60u, page[0].Id);   // the lowest buyout first
        Assert.Equal(11u, page[^1].Id);
    }

    [Fact]
    public void LevelMax_only_applies_together_with_LevelMin()
    {
        AuctionRecord[] auctions = [new() { Id = 1, ItemEntry = 1 }, new() { Id = 2, ItemEntry = 2 }, new() { Id = 3, ItemEntry = 3 }];
        Assert.Equal(3, AuctionSearch.Run(auctions, All with { LevelMax = 10 }, Find).Total);                 // ignored alone
        Assert.Equal([1u], AuctionSearch.Run(auctions, All with { LevelMin = 1, LevelMax = 10 }, Find).Page.Select(a => a.Id));
        Assert.Equal([2u, 3u], AuctionSearch.Run(auctions, All with { LevelMin = 10 }, Find).Page.Select(a => a.Id));
    }

    [Fact]
    public void The_chest_slot_also_lists_robes()
    {
        AuctionRecord[] auctions = [new() { Id = 1, ItemEntry = 1 }, new() { Id = 2, ItemEntry = 2 }, new() { Id = 3, ItemEntry = 3 }];
        Assert.Equal([2u, 3u], AuctionSearch.Run(auctions, All with { InventoryType = 5 }, Find).Page.Select(a => a.Id));
        Assert.Equal([2u], AuctionSearch.Run(auctions, All with { InventoryType = 20 }, Find).Page.Select(a => a.Id));
    }
}
