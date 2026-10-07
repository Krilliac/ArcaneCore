using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class ItemUsePaymentPlanTests
{
    [Fact]
    public void Create_DecrementsPositiveAndNegativeChargesTowardZero()
    {
        Item item = Create([new(1001, 0, 2, 0, 0, 0, 0), new(1002, 0, -2, 0, 0, 0, 0)], [2, -2, 0, 0, 0]);

        ItemUsePaymentPlan plan = ItemUsePaymentPlan.Create(item);

        Assert.Equal([2, -2, 0, 0, 0], plan.Before.Charges);
        Assert.Equal([1, -1, 0, 0, 0], plan.After!.Charges);
        Assert.Equal(0u, plan.DestroyCount);
    }

    [Fact]
    public void Create_ZeroChargeChargedSlotIsWithoutCharges()
    {
        Item item = Create([new(1001, 0, -1, 0, 0, 0, 0)], [0, 0, 0, 0, 0]);

        ItemUsePaymentPlan plan = ItemUsePaymentPlan.Create(item);

        Assert.Null(plan.After);
        Assert.Equal(1u, plan.DestroyCount);
    }

    [Fact]
    public void Create_StackableCountTwoLeavesChargeFieldUnchangedButDestroysOne()
    {
        Item item = Create([new(1001, 0, -1, 0, 0, 0, 0)], [-1, 0, 0, 0, 0], count: 2, stackable: 20);

        ItemUsePaymentPlan plan = ItemUsePaymentPlan.Create(item);

        Assert.Equal(-1, plan.After!.Charges[0]);
        Assert.Equal(1u, plan.After.Count);
        Assert.Equal(1u, plan.DestroyCount);
    }

    [Fact]
    public void Create_LastChargedSlotControlsDeletionForMixedSlots()
    {
        Item item = Create(
            [new(1001, 0, -1, 0, 0, 0, 0), new(1002, 0, 2, 0, 0, 0, 0)],
            [0, 2, 0, 0, 0]);

        ItemUsePaymentPlan plan = ItemUsePaymentPlan.Create(item);

        Assert.NotNull(plan.After);
        Assert.Equal([0, 1, 0, 0, 0], plan.After!.Charges);
        Assert.Equal(0u, plan.DestroyCount);
    }

    [Fact]
    public void Create_DoesNotMutateLiveItem_AndFreezesSnapshotLists()
    {
        Item item = Create([new(1001, 0, 2, 0, 0, 0, 0)], [1, 0, 0, 0, 0]);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7001);
        ItemInstanceData liveBefore = item.ToData();

        ItemUsePaymentPlan plan = ItemUsePaymentPlan.Create(item);

        ItemInstanceData liveAfter = item.ToData();
        Assert.Equal(liveBefore.Guid, liveAfter.Guid);
        Assert.Equal(liveBefore.Entry, liveAfter.Entry);
        Assert.Equal(liveBefore.Count, liveAfter.Count);
        Assert.Equal(liveBefore.Enchantments, liveAfter.Enchantments);
        Assert.Equal(liveBefore.Charges, liveAfter.Charges);
        Assert.Equal(liveBefore.Charges, plan.Before.Charges);
        Assert.Equal(7001u, plan.Before.Enchantments[0]);
        Assert.IsAssignableFrom<IList<int>>(plan.Before.Charges);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)plan.Before.Charges)[0] = 99);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)plan.Before.Enchantments)[0] = 99);
    }

    private static Item Create(
        IReadOnlyList<ItemSpell> spells,
        IReadOnlyList<int> charges,
        uint count = 1,
        uint stackable = 1)
    {
        var template = new ItemTemplate
        {
            Entry = 91000,
            Name = "Item use payment plan test item",
            Stackable = stackable,
            Spells = spells,
        }.Normalized();
        var item = new Item(1, template, ObjectGuid.Player(1)) { Count = count };
        for (int i = 0; i < charges.Count && i < Item.SpellChargeSlots; i++)
        {
            item.SetInt32(UpdateFields.ItemFieldSpellCharges + i, charges[i]);
        }

        return item;
    }
}
