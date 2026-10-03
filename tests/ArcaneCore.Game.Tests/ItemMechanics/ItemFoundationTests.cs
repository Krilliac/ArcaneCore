using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Trade versus mail/auction transfer rules (Item.cpp:932-952, TradeHandler.cpp:313,322,702,
/// MailHandler.cpp:301-307, AuctionHouseHandler.cpp:332-342) and the durability primitives
/// (Player.cpp:4794-4898).
/// </summary>
public sealed class ItemFoundationTests
{
    private const uint ConjuredWater = 92001; // flag 0x2 (shape of the mage conjured food)
    private const uint TimedStone = 92002; // Duration 1800

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = ConjuredWater, Class = 0, Name = "Conjured Water", DisplayId = 1, Flags = 0x2, Stackable = 20 },
            new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 1800 },
        ], []);

    private static PlayerInventory Loaded()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        return player.Inventory;
    }

    [Fact]
    public void ConjuredAndTimedItems_AreTradable_ButNotMailableOrAuctionable()
    {
        PlayerInventory inv = Loaded();
        Item water = Give(inv, ConjuredWater, 5);
        Item stone = Give(inv, TimedStone);

        Assert.Equal(InventoryResult.Ok, inv.CanBeTraded(water));
        Assert.Equal(InventoryResult.Ok, inv.CanBeTraded(stone));
        Assert.Equal(InventoryResult.ItemNotFound, inv.CanTransferOut(water));
        Assert.Equal(InventoryResult.ItemNotFound, inv.CanTransferOut(stone));
        Assert.Equal(InventoryResult.ItemNotFound, inv.TryStageEconomyTransfer([water.Guid], [], out _));
        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([water.Guid, stone.Guid], [], out EconomyInventoryStage? stage, trade: true));
        Assert.NotNull(stage);
    }

    [Fact]
    public void CanBeTraded_StillRefusesSoulboundAndNonEmptyBags()
    {
        PlayerInventory inv = Loaded();
        Item stone = Give(inv, Hearthstone);
        Assert.Equal(InventoryResult.CantDropSoulbound, inv.CanBeTraded(stone));
        Assert.Equal(InventoryResult.CantDropSoulbound, inv.TryStageEconomyTransfer([stone.Guid], [], out _, trade: true));
    }

    private static PlayerInventory WithEquipment(out Item sword, out Item bagItem, out Item pouchItem)
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        inv.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData
        {
            Guid = 4000, Entry = WornShortsword, Durability = 20, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        })]);
        sword = inv.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
        bagItem = Give(inv, RecruitsPants);
        Item pouch = Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(pouch.BagSlot, pouch.Slot));
        pouchItem = Give(inv, RecruitsBoots); // lands in the equipped pouch or backpack
        return inv;
    }

    [Fact]
    public void DurabilityPointsLoss_WhenDisabled_DoesNothing()
    {
        PlayerInventory inv = WithEquipment(out Item sword, out _, out _);
        inv.Options.DurabilityLossEnable = false;
        inv.DurabilityPointsLoss(sword, 5);
        inv.DurabilityLossAll(0.5, inventory: true);
        inv.DurabilityPointsLossAll(3, inventory: true);
        Assert.Equal(20u, sword.Durability);
    }

    [Fact]
    public void DurabilityPointsLossAll_WalksEquipmentBackpackAndEquippedBags()
    {
        PlayerInventory inv = WithEquipment(out Item sword, out Item pants, out Item boots);
        inv.DurabilityPointsLossAll(3, inventory: false);
        Assert.Equal((17u, 25u, 16u), (sword.Durability, pants.Durability, boots.Durability));

        inv.DurabilityPointsLossAll(2, inventory: true);
        Assert.Equal((15u, 23u, 14u), (sword.Durability, pants.Durability, boots.Durability));
    }

    [Fact]
    public void DurabilityPointLossForEquipSlot_TakesOnePoint_AndBreakingDropsNothingElse()
    {
        PlayerInventory inv = WithEquipment(out Item sword, out _, out _);
        inv.DurabilityPointLossForEquipSlot(InventorySlots.MainHand);
        Assert.Equal(19u, sword.Durability);
        inv.DurabilityPointLossForEquipSlot(InventorySlots.Head); // empty slot: no-op
        inv.DurabilityPointsLoss(sword, 100);
        Assert.Equal(0u, sword.Durability);
    }

    [Fact]
    public void Options_DefaultToRetail()
    {
        var options = new ItemMechanicsOptions();
        Assert.True(options.DurabilityLossEnable);
        Assert.Equal(0.5, options.DurabilityLossChanceDamage);
    }
}
