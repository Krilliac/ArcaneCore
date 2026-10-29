using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Economy;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// The AH bot's vendor-item set (what <c>AuctionBotPricing</c> values at the vendor price): it holds the direct <c>npc_vendor</c> rows and the shared
/// <c>npc_vendor_template</c> stock, and the planner built from it is rebuilt when the NPC store is swapped by <c>.reload npc_vendor</c>.
/// </summary>
public sealed class AuctionBotVendorSetTests
{
    private static NpcStore Store(uint[] direct, uint[] template) => new(NpcContent.Empty with
    {
        VendorItems = [.. direct.Select(item => new VendorItem { Entry = 100, Item = item })],
        VendorTemplateItems = [.. template.Select(item => new VendorItem { Entry = 7, Item = item })],
    });

    [Fact]
    public void TheVendorSet_HoldsTheDirectRows_AndTheSharedTemplateStock()
    {
        HashSet<uint> set = AuctionBotFeature.VendorItemSet(Store(direct: [2901, 159], template: [4540, 159]));

        Assert.Equal([159u, 2901u, 4540u], set.Order());   // 4540 is sold only through npc_vendor_template
    }

    [Fact]
    public void WithoutAnNpcStore_TheSetIsEmpty() => Assert.Empty(AuctionBotFeature.VendorItemSet(null));

    [Fact]
    public void TheSetFollowsAReloadedNpcStore_SoAnItemAddedByReloadNpcVendorIsPricedAsAVendorItem()
    {
        NpcStore before = Store(direct: [159], template: []);
        NpcStore after = Store(direct: [159], template: [4540]);   // .reload npc_vendor swaps the whole NpcStore
        Assert.DoesNotContain(4540u, AuctionBotFeature.VendorItemSet(before));
        Assert.Contains(4540u, AuctionBotFeature.VendorItemSet(after));
    }

    [Fact]
    public void ThePlanner_IsRebuiltWhenTheNpcStoreChanges_NotWhenNothingChanged()
    {
        var items = new ItemTemplateStore([new ItemTemplate { Entry = 1 }]);
        NpcStore npcs = Store(direct: [159], template: []);

        Assert.True(AuctionBotFeature.PlannerIsStale(items, null, npcs, null, hasPlanner: false));
        Assert.False(AuctionBotFeature.PlannerIsStale(items, items, npcs, npcs, hasPlanner: true));
        Assert.True(AuctionBotFeature.PlannerIsStale(items, items, Store([159], [4540]), npcs, hasPlanner: true));
        Assert.True(AuctionBotFeature.PlannerIsStale(new ItemTemplateStore([new ItemTemplate { Entry = 1 }]), items, npcs, npcs, hasPlanner: true));
        Assert.False(AuctionBotFeature.PlannerIsStale(items, items, null, null, hasPlanner: true));
    }
}
