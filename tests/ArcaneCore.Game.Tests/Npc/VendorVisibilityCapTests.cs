using System.Collections;
using System.Reflection;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>The item-id buy opcodes may name a row absent from the capped inventory packet.</summary>
public sealed class VendorVisibilityCapTests
{
    private const uint FirstItem = 93000;
    private const uint NewlyVisibleAfterFiltering = FirstItem + 134;
    private const uint HiddenByCap = FirstItem + 154;
    private const uint Template = 501;

    private static NpcServiceKit Kit(bool hideFirstTen = false)
    {
        var goods = Enumerable.Range(0, 160)
            .Select(index => NpcServiceKit.Templates[0] with
            {
                Entry = FirstItem + (uint)index,
                BuyPrice = 1,
                BuyCount = 1,
                Stackable = 1,
                AllowableRace = hideFirstTen && index < 5 ? 0u : uint.MaxValue,
                RequiredReputationRank = hideFirstTen && index is >= 8 and < 10 ? 5u : 0u,
            }).ToArray();
        var store = new ItemTemplateStore([.. NpcServiceKit.Templates, .. goods], []);
        var content = NpcContent.Empty with
        {
            VendorItems = Enumerable.Range(0, 10).Select(index => new VendorItem
            {
                Entry = NpcServiceKit.Entry, Item = FirstItem + (uint)index, Slot = (uint)index,
                ConditionId = hideFirstTen && index is >= 5 and < 8 ? 99u : 0u,
            }).ToArray(),
            VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceKit.Entry] = Template },
            VendorTemplateItems = Enumerable.Range(10, 150).Select(index => new VendorItem
            {
                Entry = Template, Item = FirstItem + (uint)index, Slot = (uint)index,
                MaxCount = index == 154 ? 2u : 0u,
                IncrTime = index == 154 ? 60u : 0u,
            }).ToArray(),
        };
        QuestNpcDependencies? extra = hideFirstTen
            ? new QuestNpcDependencies(Reputation: new NpcVendorServiceTests.FixedReputation(1f, rank: 4),
                Conditions: new RefusingCondition())
            : null;
        var kit = new NpcServiceKit(NpcFlags.Vendor, content, extra: extra, itemStore: store);
        kit.Player.Money = 1000;
        return kit;
    }

    [Fact]
    public void ItemIdBuy_RejectsLimitedStockRow155AbsentFrom128RowPacket()
    {
        using var kit = Kit();
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(128, packet[8]);
        Assert.Equal(FirstItem, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(FirstItem + 127, BitConverter.ToUInt32(packet, 9 + (127 * 28) + 4));
        Assert.DoesNotContain(HiddenByCap, Enumerable.Range(0, packet[8])
            .Select(index => BitConverter.ToUInt32(packet, 9 + (index * 28) + 4)));
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, HiddenByCap));

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, HiddenByCap, 1);
        Assert.Equal((byte)BuyResult.CantFindItem, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(HiddenByCap));

        kit.Now += 120;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, HiddenByCap, 1);
        Assert.Equal((byte)BuyResult.CantFindItem, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(HiddenByCap));
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(typeof(QuestNpcServices)
            .GetField("_vendorStock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(kit.Services)));
    }

    [Fact]
    public void RaceConditionAndFactionFilteredRowsBeforeTheCapLetLaterTemplateRowsAppearAndSell()
    {
        using var kit = Kit(hideFirstTen: true);
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(128, packet[8]);
        Assert.Equal(FirstItem + 10, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(FirstItem + 137, BitConverter.ToUInt32(packet, 9 + (127 * 28) + 4));
        Assert.Equal(1u, kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, NewlyVisibleAfterFiltering));
        Assert.Null(kit.Services.GetVendorPurchasePrice(kit.Player, kit.Npc, HiddenByCap));

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NewlyVisibleAfterFiltering, 1);
        byte[] bought = kit.Drain().Single(p => p.Opcode == WorldOpcode.SmsgBuyItem).Payload;
        Assert.Equal(125u, BitConverter.ToUInt32(bought, 8));
        Assert.Equal(999u, kit.Player.Money);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(NewlyVisibleAfterFiltering));
    }

    /// <summary>
    /// The price checks find their row without walking the visible rows before it (QuestNpcServices.VisibleVendorRow); the packet still
    /// walks the full view. For every item the vendor lists, before and past the 128-row cap, with rows filtered out ahead of it or
    /// not, a price is quoted exactly when the item is in the packet.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PriceQuotesAgreeWithThePacketForEveryRow(bool hideFirstTen)
    {
        using var kit = Kit(hideFirstTen);
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        var listed = Enumerable.Range(0, packet[8]).Select(index => BitConverter.ToUInt32(packet, 9 + (index * 28) + 4)).ToHashSet();
        for (uint item = FirstItem; item < FirstItem + 160; item++)
            Assert.True(listed.Contains(item) == (kit.Services.QuoteVendorPrice(kit.Player, kit.Npc, item) is not null), $"item {item}");
        Assert.Equal(128, listed.Count);
    }

    private sealed class RefusingCondition : IConditionEvaluator
    {
        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => false;
    }

    [Fact]
    public void SlotBuyRejectsRow155_BuybackRemainsIndependentOfVendorCap()
    {
        using var kit = Kit();
        kit.Services.BuyItemInSlot(kit.Player, kit.Npc.Guid, HiddenByCap,
            kit.Player.Guid, InventorySlots.ItemStart, 1);
        Assert.Equal((byte)BuyResult.CantFindItem, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);
        Assert.Null(kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));
        Assert.Equal(1000u, kit.Player.Money);

        var sold = kit.Give(NpcServiceKit.Sword);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sold.Guid, 0);
        kit.Player.Money = 1000;
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        Assert.Same(sold, kit.Player.Inventory.GetItemByGuid(sold.Guid));
        Assert.Equal(750u, kit.Player.Money);
    }
}
