using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Gift wrapping and opening (vmangos WorldSession::HandleWrapItemOpcode, ItemHandler.cpp:1049-1139; HandleOpenItemOpcode, SpellHandler.cpp:200-227).
/// Paper, gift and items are synthetic; the paper and the gift carry ITEM_FLAG_WRAPPER as in the content, the gift is not stackable.
/// </summary>
public sealed class GiftWrapTests
{
    private const uint Paper = 91_000;
    private const uint GiftBox = 91_001;
    private const uint PaperWithoutGift = 91_002;
    private const uint Sword = 91_010;
    private const uint Cloth = 91_011;
    private const uint Unique = 91_012;
    private const uint Bag = 91_013;
    private const uint BoundRing = 91_014;

    private static readonly ItemTemplateStore Content = new(
        [
            .. Templates,
            new ItemTemplate { Entry = Paper, Class = 0, Name = "Wrapping Paper", DisplayId = 1, Stackable = 20, Flags = (uint)ItemTemplateFlags.Wrapper, WrappedGift = GiftBox },
            new ItemTemplate { Entry = GiftBox, Class = 0, Name = "Wrapped Gift", DisplayId = 2, Stackable = 1, Flags = (uint)ItemTemplateFlags.Wrapper },
            new ItemTemplate { Entry = PaperWithoutGift, Class = 0, Name = "Plain Paper", DisplayId = 3, Stackable = 20, Flags = (uint)ItemTemplateFlags.Wrapper },
            new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Gift Sword", DisplayId = 4, InventoryType = 13, Delay = 2000, MaxDurability = 50, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = Cloth, Class = 7, Name = "Cloth", DisplayId = 5, Stackable = 20 },
            new ItemTemplate { Entry = Unique, Class = 4, Name = "One Only", DisplayId = 6, InventoryType = 12, MaxCount = 1 },
            new ItemTemplate { Entry = Bag, Class = 1, Name = "Small Bag", DisplayId = 7, InventoryType = 18, ContainerSlots = 4 },
            new ItemTemplate { Entry = BoundRing, Class = 4, Name = "Bound Ring", DisplayId = 8, InventoryType = 11, Bonding = 1 },
        ], []);

    private static (Player Player, FakeSession Session) Create()
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Content;
        player.Inventory.Load([]);
        return (player, session);
    }

    private static InventoryResult Wrap(Player player, Item paper, Item item, bool casting = false)
        => player.Inventory.WrapItem(paper.BagSlot, paper.Slot, item.BagSlot, item.Slot, casting);

    [Fact]
    public void Wrap_TurnsTheItemIntoTheGift_UsesOnePaper_AndOpeningRestoresIt()
    {
        (Player player, FakeSession session) = Create();
        Item paper = Give(player.Inventory, Paper, 3);
        Item sword = Give(player.Inventory, Sword);
        sword.Durability = 37;
        ObjectGuid guid = sword.Guid;

        Assert.Equal(InventoryResult.Ok, Wrap(player, paper, sword));
        Assert.Empty(EquipErrors(session));
        Assert.Equal(GiftBox, sword.Entry);
        Assert.Equal(GiftBox, sword.GetUInt32(UpdateFields.ObjectFieldEntry));
        Assert.Equal(ItemDynFlags.Wrapped, sword.DynamicFlags);
        Assert.Equal(player.Guid.Value, sword.GetUInt64(UpdateFields.ItemFieldGiftcreator));
        Assert.Equal(2u, player.Inventory.GetItemCount(Paper));
        Assert.Equal(0u, player.Inventory.GetItemCount(Sword));

        // Saved and loaded again (a relog, or a letter): the gift keeps its contents and the sword's own durability.
        ItemInstanceData saved = sword.ToData();
        Assert.Equal((GiftBox, Sword, 0u, 37u), (saved.Entry, saved.GiftEntry, saved.GiftFlags, saved.Durability));
        (Player again, _) = Create();
        again.Inventory.Load([new InventoryItemData(0, sword.Slot, saved)]);
        Item gift = again.Inventory.GetItem(InventorySlots.Bag0, sword.Slot)!;
        Assert.Equal((GiftBox, Sword), (gift.Entry, gift.GiftEntry));

        Assert.True(again.Inventory.OpenGift(gift));
        Assert.Equal(guid, gift.Guid);
        Assert.Equal(Sword, gift.Entry);
        Assert.Equal(ItemDynFlags.None, gift.DynamicFlags);
        Assert.Equal(0ul, gift.GetUInt64(UpdateFields.ItemFieldGiftcreator));
        Assert.Equal((50u, 37u), (gift.MaxDurability, gift.Durability));
        Assert.Equal(0u, gift.ToData().GiftEntry);
        Assert.False(again.Inventory.OpenGift(gift));   // no longer wrapped
    }

    [Fact]
    public void Wrap_RefusesInVmangosOrder()
    {
        (Player player, FakeSession session) = Create();
        Item paper = Give(player.Inventory, Paper, 10);
        Item plain = Give(player.Inventory, PaperWithoutGift);
        Item cloth = Give(player.Inventory, Cloth, 2);
        Item unique = Give(player.Inventory, Unique);
        Item bag = Give(player.Inventory, Bag);
        Item ring = Give(player.Inventory, BoundRing);
        ring.DynamicFlags |= ItemDynFlags.Bound;
        Item sword = Give(player.Inventory, Sword);

        Assert.Equal(InventoryResult.ItemNotFound, Wrap(player, plain, sword));
        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.WrapItem(InventorySlots.Bag0, 38, sword.BagSlot, sword.Slot));
        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.WrapItem(paper.BagSlot, paper.Slot, InventorySlots.Bag0, 38));
        Assert.Equal(InventoryResult.WrappedCantBeWrapped, Wrap(player, paper, paper));
        Assert.Equal(InventoryResult.BagsCantBeWrapped, Wrap(player, paper, bag));
        Assert.Equal(InventoryResult.BoundCantBeWrapped, Wrap(player, paper, ring));
        Assert.Equal(InventoryResult.StackableCantBeWrapped, Wrap(player, paper, cloth));
        Assert.Equal(InventoryResult.UniqueCantBeWrapped, Wrap(player, paper, unique));
        Assert.Equal(InventoryResult.CantDoRightNow, Wrap(player, paper, sword, casting: true));
        Assert.Equal(10u, player.Inventory.GetItemCount(Paper));

        Assert.Equal(InventoryResult.Ok, Wrap(player, paper, sword));
        Assert.Equal(InventoryResult.WrappedCantBeWrapped, Wrap(player, paper, sword));
        Assert.Equal(9u, player.Inventory.GetItemCount(Paper));
        Assert.Equal(
            [InventoryResult.ItemNotFound, InventoryResult.ItemNotFound, InventoryResult.ItemNotFound, InventoryResult.WrappedCantBeWrapped,
                InventoryResult.BagsCantBeWrapped, InventoryResult.BoundCantBeWrapped, InventoryResult.StackableCantBeWrapped,
                InventoryResult.UniqueCantBeWrapped, InventoryResult.CantDoRightNow, InventoryResult.WrappedCantBeWrapped],
            EquipErrors(session));
    }

    [Fact]
    public void Wrap_RefusesAWornItem()
    {
        (Player player, _) = Create();
        Item paper = Give(player.Inventory, Paper);
        Item sword = Give(player.Inventory, Sword);
        player.Inventory.AutoEquipItem(sword.BagSlot, sword.Slot);
        Assert.Equal(InventorySlots.MainHand, sword.Slot);
        Assert.Equal(InventoryResult.EquippedCantBeWrapped, Wrap(player, paper, sword));
    }

    [Fact]
    public void OpeningAGiftWhoseContentsAreUnknown_DestroysIt()
    {
        (Player player, _) = Create();
        player.Inventory.Load([new InventoryItemData(0, 23, new ItemInstanceData
        {
            Guid = 77, Entry = GiftBox, Flags = (uint)ItemDynFlags.Wrapped, GiftEntry = 999_999, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        })]);
        Item gift = player.Inventory.GetItem(InventorySlots.Bag0, 23)!;
        Assert.True(player.Inventory.OpenGift(gift));
        Assert.Null(player.Inventory.GetItem(InventorySlots.Bag0, 23));
    }

    [Fact]
    public void AStoredGiftWithoutTheWrappedFlag_DropsItsContents()
    {
        (Player player, _) = Create();
        player.Inventory.Load([new InventoryItemData(0, 23, new ItemInstanceData
        {
            Guid = 78, Entry = Cloth, Flags = (uint)ItemDynFlags.Wrapped, GiftEntry = Sword, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        })]);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, 23)!;
        Assert.Equal(ItemDynFlags.None, item.DynamicFlags);   // vmangos Item.cpp:447-458: not a wrapper template
        Assert.Equal(0u, item.ToData().GiftEntry);
    }
}
