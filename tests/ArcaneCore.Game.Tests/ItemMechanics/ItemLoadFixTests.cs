using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// vmangos Item::LoadFromDB corrections (Item.cpp:403-410 duration, 430-435 bind flag, 462-476
/// wrapped flag): stored state that contradicts the template is repaired at load.
/// </summary>
public sealed class ItemLoadFixTests
{
    private const uint TimedStone = 91001; // template Duration 1800 (shape of classic-db 4986)
    private const uint PlainWrapper = 91002; // flag 0x200, not stackable
    private const uint StackableWrapper = 91003; // flag 0x200, stackable

    private const uint BindOnEquipBlade = 91004; // main-hand, bonding BIND_WHEN_EQUIPPED
    private const uint BindOnEquipPouch = 91005; // bag, bonding BIND_WHEN_EQUIPPED

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 1800 },
            new ItemTemplate { Entry = PlainWrapper, Class = 0, Name = "Wrapper", DisplayId = 1, Flags = 0x200 },
            new ItemTemplate { Entry = StackableWrapper, Class = 0, Name = "Stack Wrapper", DisplayId = 1, Flags = 0x200, Stackable = 5 },
            new ItemTemplate { Entry = BindOnEquipBlade, Class = 2, SubClass = 7, Name = "BoE Blade", DisplayId = 1, InventoryType = 21, Bonding = (uint)ItemBonding.WhenEquipped },
            new ItemTemplate { Entry = BindOnEquipPouch, Class = 1, Name = "BoE Pouch", DisplayId = 1, InventoryType = 18, ContainerSlots = 4, Bonding = (uint)ItemBonding.WhenEquipped },
        ], []);

    private static Item LoadOne(uint entry, uint duration = 0, uint flags = 0, byte slot = InventorySlots.ItemStart)
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([new InventoryItemData(0, slot, new ItemInstanceData
        {
            Guid = 9000, Entry = entry, Count = 1, Duration = duration, Flags = flags, Durability = 0,
            Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        })]);
        return player.Inventory.GetItem(InventorySlots.Bag0, slot)!;
    }

    [Fact]
    public void Load_BoundFlagOnNoBindTemplate_IsCleared()
    {
        Item shirt = LoadOne(RecruitsShirt, flags: (uint)ItemDynFlags.Bound);
        Assert.False(shirt.IsSoulBound);
    }

    [Fact]
    public void Load_BoundFlagOnBindOnPickupTemplate_IsKept()
    {
        Item stone = LoadOne(Hearthstone, flags: (uint)ItemDynFlags.Bound);
        Assert.True(stone.IsSoulBound);
    }

    /// <summary>
    /// vmangos Player::_LoadInventory binds while placing: StoreItem soulbinds a bind-on-pickup or
    /// quest item that was saved unbound (a GM .additem), including one inside a bag, and
    /// QuickEquipItem / BankItem soulbind a bind-on-equip item that is worn or sitting in a bag
    /// slot. A bind-on-equip item left in the backpack stays tradable.
    /// </summary>
    [Fact]
    public void Load_SavedUnboundItems_BindTheWayPlacementDoes_AndThenCannotBeTraded()
    {
        Item pickup = LoadOne(Hearthstone);
        Assert.True(pickup.IsSoulBound);
        Assert.Equal(InventoryResult.CantDropSoulbound, pickup.Inventory!.CanBeTraded(pickup));

        Assert.True(LoadOne(QuestPelt).IsSoulBound);

        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load(
        [
            new InventoryItemData(0, InventorySlots.BagStart, BagRow(9100, SmallBrownPouch)),
            new InventoryItemData(9100, 0, BagRow(9101, Hearthstone)),
        ]);
        Item inBag = player.Inventory.GetItem(InventorySlots.BagStart, 0)!;
        Assert.True(inBag.IsSoulBound);
        Assert.Equal(InventoryResult.CantDropSoulbound, player.Inventory.CanBeTraded(inBag));

        Item worn = LoadOne(BindOnEquipBlade, slot: InventorySlots.MainHand);
        Assert.True(worn.IsSoulBound);
        worn.Inventory!.SwapItem(InventorySlots.Bag0, InventorySlots.MainHand, InventorySlots.Bag0, InventorySlots.ItemStart);
        Assert.Equal(InventoryResult.CantDropSoulbound, worn.Inventory.CanBeTraded(worn));

        Assert.True(LoadOne(BindOnEquipPouch, slot: InventorySlots.BankBagStart).IsSoulBound);

        Item carried = LoadOne(BindOnEquipBlade);
        Assert.False(carried.IsSoulBound);
        Assert.Equal(InventoryResult.Ok, carried.Inventory!.CanBeTraded(carried));
    }

    private static ItemInstanceData BagRow(uint guid, uint entry) => new()
    {
        Guid = guid, Entry = entry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
    };

    [Fact]
    public void Load_StoredDurationZero_TemplateTimed_ResetsToTemplate()
        => Assert.Equal(1800u, LoadOne(TimedStone, duration: 0).ToData().Duration);

    [Fact]
    public void Load_StoredDurationNonZero_TemplateZero_IsCleared()
        => Assert.Equal(0u, LoadOne(RecruitsShirt, duration: 500).ToData().Duration);

    [Fact]
    public void Load_StoredDurationNonZero_TemplateTimed_KeepsStoredRemainder()
        => Assert.Equal(700u, LoadOne(TimedStone, duration: 700).ToData().Duration);

    [Fact]
    public void Load_WrappedFlagOnNonWrapper_IsStripped()
        => Assert.Equal(0u, LoadOne(RecruitsShirt, flags: (uint)ItemDynFlags.Wrapped).ToData().Flags & (uint)ItemDynFlags.Wrapped);

    [Fact]
    public void Load_WrappedFlagOnStackableWrapper_IsStripped_OnPlainWrapperKept()
    {
        Assert.Equal(0u, LoadOne(StackableWrapper, flags: (uint)ItemDynFlags.Wrapped).ToData().Flags & (uint)ItemDynFlags.Wrapped);
        Assert.Equal((uint)ItemDynFlags.Wrapped, LoadOne(PlainWrapper, flags: (uint)ItemDynFlags.Wrapped).ToData().Flags & (uint)ItemDynFlags.Wrapped);
    }
}
