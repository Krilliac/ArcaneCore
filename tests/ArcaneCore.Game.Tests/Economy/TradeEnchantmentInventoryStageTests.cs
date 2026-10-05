using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

public sealed class TradeEnchantmentInventoryStageTests
{
    [Fact]
    public void PlanningReplacementAndConsumeDoesNotMutateLiveInventory()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([]);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky, 3);
        Item held = ItemTestData.Give(player.Inventory, ItemTestData.RecruitsPants);
        ItemInstanceData replacement = held.ToData() with { Enchantments = [7001u, .. new uint[20]] };

        InventoryResult result = player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage, trade: false,
            replacements: [replacement], consume: [new InventoryRewardGrant(ItemTestData.ToughJerky, 2)]);

        Assert.Equal(InventoryResult.Ok, result);
        Assert.Equal(3u, reagent.Count);
        Assert.Equal(0u, held.EnchantmentId(0));
        Assert.NotNull(stage);
        Assert.Equal(1u, stage!.After.Items.Single(i => i.Item.Guid == reagent.Guid.Low).Item.Count);
        Assert.Equal(7001u, stage.After.Items.Single(i => i.Item.Guid == held.Guid.Low).Item.Enchantments[0]);
        player.Inventory.ApplyEconomyTransfer(stage);
        Assert.Same(held, player.Inventory.GetItemByGuid(held.Guid));
        Assert.Equal(7001u, held.EnchantmentId(0));
        Assert.Equal(1u, reagent.Count);
        Assert.True(PlayerInventory.SameEconomySnapshot(stage.After, player.Inventory.CreateSnapshot()));
    }

    [Fact]
    public void PlanningRejectsReplacementGuidConflictAndOfferedReagentConflict()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([]);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky, 2);
        ItemInstanceData incomingSameGuid = reagent.ToData();
        Assert.Equal(InventoryResult.CantDoRightNow,
            player.Inventory.TryStageEconomyTransfer([], [incomingSameGuid], out _, trade: false, replacements: null, consume: null));

        Assert.Equal(InventoryResult.ItemNotFound,
            player.Inventory.TryStageEconomyTransfer([reagent.Guid], [], out _, trade: false,
                replacements: null, consume: [new InventoryRewardGrant(ItemTestData.ToughJerky, 3)]));
    }

    [Fact]
    public void FullReagentRemovalPublishesWithTransfer_AndStaleBeforeIsRefused()
    {
        (var player, _) = ItemTestData.CreatePlayer();
        player.Inventory.Load([]);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky, 3);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage, trade: false,
            replacements: null, consume: [new InventoryRewardGrant(ItemTestData.ToughJerky, 3)]));
        Assert.NotNull(stage);
        player.Inventory.ApplyEconomyTransfer(stage!);
        Assert.Null(player.Inventory.GetItemByGuid(reagent.Guid));

        Item later = ItemTestData.Give(player.Inventory, ItemTestData.RecruitsShirt);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stale, trade: false,
            replacements: null, consume: null));
        later.Count++;
        Assert.Throws<InvalidOperationException>(() => player.Inventory.ApplyEconomyTransfer(stale!));
    }
}
