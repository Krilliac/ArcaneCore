using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class EconomyItemUsePaymentTests
{
    private static (Player Player, Item Item) LoadedItem(uint entry, uint count = 1)
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Load([]);
        return (player, Give(player.Inventory, entry, count));
    }

    [Fact]
    public void Stage_CastItemPartialStackUsesExactGuidConsumption()
    {
        (Player player, Item castItem) = LoadedItem(ToughJerky, count: 2);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);

        Assert.Equal(1u, payment.DestroyCount);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: null, consume: null, itemUse: payment));
        Assert.DoesNotContain(castItem.Guid.Low, stage!.ConsumedItemGuids);
        Assert.Equal(1u, stage.After.Items.Single(row => row.Item.Guid == castItem.Guid.Low).Item.Count);
        Assert.Equal(2u, castItem.Count);

        player.Inventory.ApplyEconomyTransfer(stage);
        Assert.Same(castItem, player.Inventory.GetItemByGuid(castItem.Guid));
        Assert.Equal(1u, castItem.Count);
    }

    [Fact]
    public void Stage_CastItemRejectsReagentOverlap()
    {
        (Player player, Item castItem) = LoadedItem(ToughJerky, count: 2);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);

        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.TryStageEconomyTransfer([], [], out _,
            trade: false, replacements: null,
            consume: [new InventoryRewardGrant(ToughJerky, 1)], itemUse: payment));
    }

    [Fact]
    public void Stage_CastItemRequiresExactLivePlanAndRejectsForgedAfter()
    {
        (Player player, Item castItem) = LoadedItem(ToughJerky, count: 2);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);
        ItemInstanceData paid = payment.After!;
        ItemUsePaymentPlan forged = payment with
        {
            After = paid with { Flags = paid.Flags ^ 1u },
        };

        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.TryStageEconomyTransfer([], [], out _,
            trade: false, replacements: null, consume: null, itemUse: forged));

        castItem.Count++;
        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.TryStageEconomyTransfer([], [], out _,
            trade: false, replacements: null, consume: null, itemUse: payment));
    }

    [Fact]
    public void Stage_CastItemCannotAlsoBeOfferedOut()
    {
        (Player player, Item castItem) = LoadedItem(ToughJerky);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);

        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.TryStageEconomyTransfer([castItem.Guid], [], out _,
            trade: false, replacements: null, consume: null, itemUse: payment));
    }

    [Fact]
    public void Stage_CastItemWithNoChargedSpellIsAStableNoOp()
    {
        (Player player, Item castItem) = LoadedItem(RecruitsShirt);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);
        InventorySnapshot before = player.Inventory.CreateSnapshot();

        Assert.Equal(0u, payment.DestroyCount);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: null, consume: null, itemUse: payment));
        Assert.True(PlayerInventory.SameEconomySnapshot(before, stage!.After));
        player.Inventory.ApplyEconomyTransfer(stage);
        Assert.True(PlayerInventory.SameEconomySnapshot(before, player.Inventory.CreateSnapshot()));
    }

    [Fact]
    public void Stage_CastItemPreservesGuidForPositiveAndNegativeChargeDeltas()
    {
        (Player player, _) = CreatePlayer();
        const uint positiveEntry = 91010;
        const uint negativeEntry = 91011;
        player.Inventory.Templates = new ItemTemplateStore([
            .. Templates,
            new ItemTemplate { Entry = positiveEntry, Class = 0, Stackable = 1, Spells = [new ItemSpell(1001, 0, 2, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = negativeEntry, Class = 0, Stackable = 1, Spells = [new ItemSpell(1002, 0, -2, 0, 0, 0, 0)] },
        ]);
        player.Inventory.Load([]);
        Item positive = Give(player.Inventory, positiveEntry);
        Item negative = Give(player.Inventory, negativeEntry);
        positive.SetInt32(UpdateFields.ItemFieldSpellCharges, 2);
        negative.SetInt32(UpdateFields.ItemFieldSpellCharges, -2);

        ItemUsePaymentPlan positivePayment = ItemUsePaymentPlan.Create(positive);
        ItemUsePaymentPlan negativePayment = ItemUsePaymentPlan.Create(negative);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? positiveStage,
            trade: false, replacements: null, consume: null, itemUse: positivePayment));
        Assert.Equal([1, 0, 0, 0, 0], positiveStage!.After.Items.Single(row => row.Item.Guid == positive.Guid.Low).Item.Charges);
        Assert.Equal(0u, positivePayment.DestroyCount);

        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? negativeStage,
            trade: false, replacements: null, consume: null, itemUse: negativePayment));
        Assert.Equal([-1, 0, 0, 0, 0], negativeStage!.After.Items.Single(row => row.Item.Guid == negative.Guid.Low).Item.Charges);
        Assert.Equal(0u, negativePayment.DestroyCount);
    }

    [Fact]
    public void Stage_CastItemLastNegativeChargeUsesConsumedGuid()
    {
        (Player player, _) = CreatePlayer();
        const uint entry = 91012;
        player.Inventory.Templates = new ItemTemplateStore([
            .. Templates,
            new ItemTemplate { Entry = entry, Class = 0, Stackable = 1, Spells = [new ItemSpell(1003, 0, -1, 0, 0, 0, 0)] },
        ]);
        player.Inventory.Load([]);
        Item castItem = Give(player.Inventory, entry);
        ItemUsePaymentPlan payment = ItemUsePaymentPlan.Create(castItem);

        Assert.Null(payment.After);
        Assert.Equal(1u, payment.DestroyCount);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage,
            trade: false, replacements: null, consume: null, itemUse: payment));
        Assert.Contains(castItem.Guid.Low, stage!.ConsumedItemGuids);
        Assert.DoesNotContain(stage.After.Items, row => row.Item.Guid == castItem.Guid.Low);
    }
}
