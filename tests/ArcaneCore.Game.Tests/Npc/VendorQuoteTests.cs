using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

public sealed class VendorQuoteTests
{
    [Fact]
    public void DiscoveryUsesDiscountedPriceBeyondInteractionRangeWithoutPurchasing()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content(Bread),
            new QuestNpcDependencies(Reputation: new NpcVendorServiceTests.FixedReputation(0.9f)), npcDistance: 20);
        kit.Player.Money = 22;
        Assert.Equal(22u, kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        Assert.Equal(22u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Bread));
        Assert.Equal(0, kit.Sink.CharacterChanges);
        Assert.Empty(kit.Drain());
        kit.Npc = kit.Npc with { X = kit.Player.X + 1 };
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Bread, 1);
        Assert.Equal(0u, kit.Player.Money);
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(Bread));
    }

    [Fact]
    public void SoldOutOfferReturnsOnlyAfterOrdinaryRestock()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content(Lantern, maxCount: 1, incrTime: 60));
        kit.Player.Money = 1000;
        Assert.Equal(300u, kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Lantern));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Lantern));
        kit.Now += 60;
        Assert.Equal(300u, kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Lantern));
        Assert.Equal(700u, kit.Player.Money);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(Lantern));
    }

    [Fact]
    public void HiddenUnknownOrUnaffordableRowsDoNotProduceQuotes()
    {
        using var hidden = new NpcServiceKit(NpcFlags.Vendor, Content(Bread, condition: 99));
        hidden.Player.Money = 1000;
        Assert.Null(hidden.Services.GetVendorPurchasePrice(hidden.Player, hidden.Npc, Bread));
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content(Bread));
        kit.Player.Money = 24;
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Sword));
        Assert.Empty(kit.Drain());
    }

    [Fact]
    public void FullBagsOrUnloadedPlayerDoNotProduceQuotes()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content(Bread));
        kit.Player.Money = 1000;
        for (int i = 0; i < 16; i++) kit.Give(Lantern);
        kit.Session.Clear();
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        kit.State.Loaded = false;
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, Bread));
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Empty(kit.Drain());
    }

    private static NpcContent Content(uint item, uint maxCount = 0, uint incrTime = 0, uint condition = 0)
        => NpcContent.Empty with
        {
            VendorItems = [new VendorItem { Entry = Entry, Item = item, MaxCount = maxCount,
                IncrTime = incrTime, ConditionId = condition }],
        };
}
