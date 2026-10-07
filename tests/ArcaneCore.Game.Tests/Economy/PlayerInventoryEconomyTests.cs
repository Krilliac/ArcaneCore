using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>Detached economy transfers: planning never mutates, publishing preserves item identity.</summary>
public sealed class PlayerInventoryEconomyTests
{
    private static (Player Player, FakeSession Session) Loaded(uint guid = 1)
    {
        (Player player, FakeSession session) = CreatePlayer(guid);
        player.Inventory.Load([]);
        return (player, session);
    }

    private static ItemInstanceData Foreign(uint guid, uint entry, uint count = 1) => new()
    {
        Guid = guid, Entry = entry, Count = count, Creator = 42, Durability = 7,
        Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
    };

    [Fact]
    public void Stage_DoesNotMutate_ApplyMovesItemsAndKeepsTheirData()
    {
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item jerky = Give(inv, ToughJerky, 4);
        InventorySnapshot initial = inv.CreateSnapshot();

        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([jerky.Guid], [Foreign(5000, RecruitsPants)], out EconomyInventoryStage? stage));
        Assert.True(PlayerInventory.SameEconomySnapshot(initial, inv.CreateSnapshot()));
        Assert.Same(jerky, inv.GetItemByGuid(jerky.Guid));
        Assert.Equal(4u, Assert.Single(stage!.RemovedData).Count);
        Assert.Contains(stage.After.Items, row => row.Item.Guid == 5000 && row.Item.Creator == 42);
        Assert.DoesNotContain(stage.After.Items, row => row.Item.Guid == jerky.Guid.Low);

        inv.ApplyEconomyTransfer(stage);
        Assert.True(stage.Applied);
        Assert.Null(inv.GetItemByGuid(jerky.Guid));
        Item pants = inv.GetItemByGuid(ObjectGuid.Item(5000))!;
        Assert.Equal((RecruitsPants, 7u, InventorySlots.Bag0, InventorySlots.ItemStart), (pants.Entry, pants.Durability, pants.BagSlot, pants.Slot));
        Assert.True(PlayerInventory.SameEconomySnapshot(stage.After, inv.CreateSnapshot()));
        Assert.Throws<InvalidOperationException>(() => inv.ApplyEconomyTransfer(stage));
    }

    [Fact]
    public void Stage_RefusesItemsThatCannotLeave()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        inv.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 20 })]);
        Item sword = inv.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
        Item stone = Give(inv, Hearthstone);
        Item bag = Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item inBagContainer = Give(inv, SmallBrownPouch);
        Item content = Give(inv, RecruitsShirt);

        Assert.Equal(InventoryResult.CantDropSoulbound, inv.TryStageEconomyTransfer([stone.Guid], [], out EconomyInventoryStage? stage));
        Assert.Null(stage);
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([sword.Guid], [], out _));
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([bag.Guid], [], out _));
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([ObjectGuid.Item(77777)], [], out _));
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([content.Guid, content.Guid], [], out _));
        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([inBagContainer.Guid], [], out _));
        Assert.Equal(InventoryResult.Ok, inv.CanTransferOut(content));
    }

    [Fact]
    public void Stage_RefusesContainerItemWithGeneratedLoot()
    {
        (Player player, _) = Loaded();
        PlayerInventory inventory = player.Inventory;
        Item container = Give(inventory, RecruitsShirt);
        container.Loot = new ItemLootData(0, [new ItemLootEntry(0, ToughJerky, 2, false)]);
        InventorySnapshot before = inventory.CreateSnapshot();

        Assert.Equal(InventoryResult.AlreadyLooted, inventory.CanBeTraded(container));
        Assert.Equal(InventoryResult.AlreadyLooted, inventory.CanTransferOut(container));
        Assert.Equal(InventoryResult.AlreadyLooted,
            inventory.TryStageEconomyTransfer([container.Guid], [], out EconomyInventoryStage? tradeStage, trade: true));
        Assert.Null(tradeStage);
        Assert.Equal(InventoryResult.AlreadyLooted,
            inventory.TryStageEconomyTransfer([container.Guid], [], out EconomyInventoryStage? mailStage));
        Assert.Null(mailStage);
        Assert.True(PlayerInventory.SameEconomySnapshot(before, inventory.CreateSnapshot()));

        container.Loot = new ItemLootData(0, []);
        Assert.Equal(InventoryResult.Ok, inventory.CanBeTraded(container));
    }

    [Fact]
    public void Stage_NeedsALoadedInventory()
    {
        (Player unloaded, _) = CreatePlayer(2);
        Assert.Equal(InventoryResult.CantDoRightNow, unloaded.Inventory.TryStageEconomyTransfer([], [Foreign(5000, RecruitsShirt)], out EconomyInventoryStage? stage));
        Assert.Null(stage);
    }

    [Fact]
    public void Stage_FullInventory_OrUnknownTemplate_OrClashingGuid()
    {
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item first = Give(inv, RecruitsShirt);
        for (int i = 1; i < 16; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Assert.Equal(InventoryResult.InventoryFull, inv.TryStageEconomyTransfer([], [Foreign(5000, RecruitsPants)], out _));
        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([first.Guid], [Foreign(5000, RecruitsPants)], out EconomyInventoryStage? swap));
        Assert.Contains(swap!.After.Items, row => row.Item.Guid == 5000 && row.Slot == first.Slot);
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([first.Guid], [Foreign(5000, 123456)], out _));
        Assert.Equal(InventoryResult.CantDoRightNow, inv.TryStageEconomyTransfer([first.Guid], [Foreign(first.Guid.Low, RecruitsPants)], out _));
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([], [Foreign(5000, RecruitsPants), Foreign(5000, RecruitsBoots)], out _));
    }

    [Fact]
    public void Stage_UniqueLimitApplies()
    {
        (Player player, _) = Loaded();
        Give(player.Inventory, UniqueKey);
        Assert.Equal(InventoryResult.CantCarryMoreOfThis, player.Inventory.TryStageEconomyTransfer([], [Foreign(5000, UniqueKey)], out _));
    }

    [Fact]
    public void Stage_PlacesIntoACarriedBagWhenTheBackpackIsFull()
    {
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item bag = Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        for (int i = 0; i < 16; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([], [Foreign(5000, RecruitsPants)], out EconomyInventoryStage? stage));
        inv.ApplyEconomyTransfer(stage!);
        Item pants = inv.GetItemByGuid(ObjectGuid.Item(5000))!;
        Assert.Same(bag, pants.Container);
        Assert.Equal((InventorySlots.BagStart, (byte)0), (pants.BagSlot, pants.Slot));
    }

    [Fact]
    public void Stage_MergesAnArrivingStackIntoRoomOfAnExistingOne_WhenNoSlotIsFree()
    {
        // vmangos CanStoreItem(NULL_BAG, NULL_SLOT, …, pItem) fills existing stacks before free slots and StoreItem
        // merges the arriving instance away (Player.cpp _StoreItem); mail, trade and auction items take that path.
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item jerky = Give(inv, ToughJerky, 4);
        for (int i = 1; i < 16; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([], [Foreign(5000, ToughJerky, 3)], out EconomyInventoryStage? stage));
        Assert.Equal([5000u], stage!.MergedItemGuids);
        Assert.DoesNotContain(stage.After.Items, row => row.Item.Guid == 5000);
        Assert.Equal(7u, stage.After.Items.Single(row => row.Item.Guid == jerky.Guid.Low).Item.Count);
        Assert.Equal(4u, jerky.Count);

        inv.ApplyEconomyTransfer(stage);
        Assert.Equal(7u, jerky.Count);
        Assert.Null(inv.GetItemByGuid(ObjectGuid.Item(5000)));
        Assert.True(PlayerInventory.SameEconomySnapshot(stage.After, inv.CreateSnapshot()));

        Assert.Equal(InventoryResult.InventoryFull, inv.TryStageEconomyTransfer([], [Foreign(5001, ToughJerky, 14)], out _));
    }

    [Fact]
    public void Stage_FillsAnExistingStackFirst_ThenPlacesTheRestWithItsOwnGuid()
    {
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item jerky = Give(inv, ToughJerky, 18);

        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([], [Foreign(5000, ToughJerky, 5)], out EconomyInventoryStage? stage));
        Assert.Empty(stage!.MergedItemGuids);
        inv.ApplyEconomyTransfer(stage);
        Assert.Equal(20u, jerky.Count);
        Item rest = inv.GetItemByGuid(ObjectGuid.Item(5000))!;
        Assert.Equal((3u, 42u), (rest.Count, rest.ToData().Creator));
        Assert.True(PlayerInventory.SameEconomySnapshot(stage.After, inv.CreateSnapshot()));
    }

    [Fact]
    public void CanBeTraded_RefusesAnItemCarryingAnEnchantmentThatCanSoulbind()
    {
        // vmangos Item::CanBeTraded → IsBoundByEnchant (Item.cpp:950-973): any enchantment slot with ENCHANTMENT_CAN_SOULBOUND.
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        var catalog = new EnchantCatalog(
        [
            new SpellItemEnchantment(701, [5, 0, 0], [2, 0, 0], [4, 0, 0], "Plain", 0, 0),
            new SpellItemEnchantment(702, [5, 0, 0], [2, 0, 0], [4, 0, 0], "Soulbound", 0, EnchantCatalog.CanSoulboundFlag),
        ]);
        player.AttachEnchantments(new PlayerEnchantments(player, catalog, null));
        Item pants = Give(inv, RecruitsPants);
        ItemEnchantments.Set(pants, EnchantSlots.Permanent, 701, 0, 0);
        Assert.Equal(InventoryResult.Ok, inv.CanBeTraded(pants));

        ItemEnchantments.Set(pants, EnchantSlots.Temporary, 702, 60_000, 0);
        Assert.False(pants.IsSoulBound);
        Assert.Equal(InventoryResult.CantDropSoulbound, inv.CanBeTraded(pants));
        Assert.Equal(InventoryResult.CantDropSoulbound, inv.CanTransferOut(pants));
        Assert.Equal(InventoryResult.CantDropSoulbound, inv.TryStageEconomyTransfer([pants.Guid], [], out _, trade: true));
    }

    [Fact]
    public void Apply_ThrowsWhenTheInventoryChangedAfterPlanning()
    {
        (Player player, _) = Loaded();
        PlayerInventory inv = player.Inventory;
        Item jerky = Give(inv, ToughJerky, 4);
        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([jerky.Guid], [], out EconomyInventoryStage? stage));
        Give(inv, RecruitsShirt);
        Assert.Throws<InvalidOperationException>(() => inv.ApplyEconomyTransfer(stage!));
        Assert.Same(jerky, inv.GetItemByGuid(jerky.Guid));
        Assert.False(stage!.Applied);

        (Player other, _) = Loaded(3);
        Assert.Throws<InvalidOperationException>(() => other.Inventory.ApplyEconomyTransfer(stage));
    }
}
