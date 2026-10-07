using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

public sealed class TradeExactReagentSelectionTests
{
    [Fact]
    public void PartialStacksPlanExactLiveGuidsAndCounts()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([
            new InventoryItemData(0, 23, new ItemInstanceData { Guid = 100, Entry = ItemTestData.ToughJerky, Count = 2 }),
            new InventoryItemData(0, 24, new ItemInstanceData { Guid = 101, Entry = ItemTestData.ToughJerky, Count = 3 })]);
        Item first = player.Inventory.GetItemByGuid(ObjectGuid.Item(100))!;
        Item second = player.Inventory.GetItemByGuid(ObjectGuid.Item(101))!;

        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: null, consume: [new InventoryRewardGrant(ItemTestData.ToughJerky, 4)]));
        Assert.Equal([(first.Guid, 2u), (second.Guid, 2u)], stage!.ConsumeItems.Select(x => (x.Existing.Guid, x.Count)));
    }

    [Fact]
    public void SameEntryReplacementDoesNotRefuseDifferentReagentGuid()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([
            new InventoryItemData(0, 23, new ItemInstanceData { Guid = 100, Entry = ItemTestData.ToughJerky, Count = 2 }),
            new InventoryItemData(0, 24, new ItemInstanceData { Guid = 101, Entry = ItemTestData.ToughJerky, Count = 2 })]);
        Item existing = player.Inventory.GetItemByGuid(ObjectGuid.Item(101))!;
        Item other = player.Inventory.GetItemByGuid(ObjectGuid.Item(100))!;
        ItemInstanceData replacement = existing.ToData() with { Enchantments = [7001u, .. new uint[20]] };

        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: [replacement], consume: [new InventoryRewardGrant(ItemTestData.ToughJerky, 2)]));
        Assert.Equal(other.Guid, Assert.Single(stage!.ConsumeItems).Existing.Guid);
    }

    [Fact]
    public void FrozenCallerListsCannotChangePlannedExactConsumption()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([]);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky, 3);
        var grants = new List<InventoryRewardGrant> { new(ItemTestData.ToughJerky, 2) };
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: null, consume: grants));
        grants[0] = new InventoryRewardGrant(ItemTestData.ToughJerky, 1);
        Assert.Equal(2u, stage!.ConsumeItems.Single().Count);
        Assert.Same(reagent, stage.ConsumeItems.Single().Existing);
    }
}
