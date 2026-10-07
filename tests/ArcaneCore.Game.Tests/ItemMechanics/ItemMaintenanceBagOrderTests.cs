using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// The zone-limited and timed item passes against vmangos Player::DestroyZoneLimitedItem (Player.cpp:10881-10909): the contents of a bag
/// go before the bag, a detached item is never destroyed by its old slot number, and the bank is not searched.
/// </summary>
public sealed class ItemMaintenanceBagOrderTests
{
    private const uint GordokKey = 94102;   // AreaBound 2557
    private const uint GordokBag = 94103;   // an area-bound bag with eight slots
    private const uint TimedBag = 94106;
    private const uint TimedStone = 94107;

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = GordokKey, Class = 13, Name = "Gordok Key", DisplayId = 1, AreaBound = 2557 },
            new ItemTemplate { Entry = GordokBag, Class = 1, Name = "Gordok Bag", DisplayId = 1, InventoryType = 18, ContainerSlots = 8, AreaBound = 2557 },
            new ItemTemplate { Entry = TimedBag, Class = 1, Name = "Timed Bag", DisplayId = 1, InventoryType = 18, ContainerSlots = 8, Duration = 5 },
            new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 5 },
        ], []);

    private static (Player Player, FakeSession Session) Make(uint zone = 2557)
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        player.ZoneId = zone;
        return (player, session);
    }

    [Fact]
    public void ZoneLimitedBag_WithALimitedItemInside_DestroysBoth_AndNeverTheItemAtTheSameSlotNumberOfTheBackpackOrEquipment()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Item pants = Give(inv, RecruitsPants);
        inv.AutoEquipItem(pants.BagSlot, pants.Slot);
        Assert.Equal(InventorySlots.Legs, pants.Slot);
        Item bag = Give(inv, GordokBag);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item key = Give(inv, GordokKey);
        inv.SwapItem(key.BagSlot, key.Slot, bag.Slot, InventorySlots.Legs);
        Assert.Same(bag, key.Container);
        ItemMaintenance.Tick(player, 10);

        player.ZoneId = 12;
        ItemMaintenance.Tick(player, 11);

        Assert.Null(inv.GetItemByGuid(bag.Guid));
        Assert.Null(inv.GetItemByGuid(key.Guid));
        Assert.Same(pants, inv.GetItem(InventorySlots.Bag0, InventorySlots.Legs));
    }

    [Fact]
    public void ExpiredBag_WithAnExpiredItemInside_NeverDestroysTheItemAtTheSameSlotNumberOfTheEquipment()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Item pants = Give(inv, RecruitsPants);
        inv.AutoEquipItem(pants.BagSlot, pants.Slot);
        Assert.Equal(InventorySlots.Legs, pants.Slot);
        Item bag = Give(inv, TimedBag);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item stone = Give(inv, TimedStone);
        inv.SwapItem(stone.BagSlot, stone.Slot, bag.Slot, InventorySlots.Legs);
        Assert.Same(bag, stone.Container);

        ItemMaintenance.Tick(player, 100);
        ItemMaintenance.Tick(player, 106);

        Assert.Null(inv.GetItemByGuid(bag.Guid));
        Assert.Null(inv.GetItemByGuid(stone.Guid));
        Assert.Same(pants, inv.GetItem(InventorySlots.Bag0, InventorySlots.Legs));
    }

    [Fact]
    public void ZoneLimitedItem_InTheBank_IsKeptWhenLeavingTheArea()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Item key = Give(inv, GordokKey);
        inv.CanUseBank = () => true;
        inv.AutoBankItem(key.BagSlot, key.Slot);
        Assert.Same(key, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
        ItemMaintenance.Tick(player, 10);

        player.ZoneId = 12;
        ItemMaintenance.Tick(player, 11);

        Assert.Same(key, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
    }
}
