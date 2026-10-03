using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>vmangos Spell::DoCreateItem (SpellEffects.cpp:1885-1990) and ItemPrototype::HasSignature (ItemPrototype.h:535).</summary>
public sealed class SpellCreateItemTests
{
    private const uint Bandage = 95001; // stackable 3
    private const uint Dagger = 95002; // unstackable weapon: has a signature
    private const uint Marker = 95003; // unstackable weapon with ITEM_FLAG_NO_CREATOR
    private const uint Trinket = 95004; // unique (MaxCount 2), stackable 5

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = Bandage, Class = 0, Name = "Bandage", DisplayId = 1, Stackable = 3 },
            new ItemTemplate { Entry = Dagger, Class = 2, SubClass = 15, Name = "Crafted Dagger", DisplayId = 1, InventoryType = 13 },
            new ItemTemplate { Entry = Marker, Class = 2, SubClass = 15, Name = "Plain Dagger", DisplayId = 1, InventoryType = 13, Flags = 0x20000 },
            new ItemTemplate { Entry = Trinket, Class = 15, Name = "Unique Bits", DisplayId = 1, Stackable = 5, MaxCount = 2 },
        ], []);

    private static (Player Player, FakeSession Session) Make()
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        return (player, session);
    }

    [Fact]
    public void Create_AmountAboveStackSize_IsClamped()
    {
        (Player player, _) = Make();
        InventoryResult result = player.Inventory.CreateItemFromSpell(Bandage, 5, out Item? item, out uint created);
        Assert.Equal(InventoryResult.Ok, result);
        Assert.Equal(3u, created);
        Assert.Equal(3u, item!.Count);
    }

    [Fact]
    public void Create_AmountBelowOne_CreatesOne()
    {
        (Player player, _) = Make();
        player.Inventory.CreateItemFromSpell(Bandage, 0, out _, out uint created);
        Assert.Equal(1u, created);
    }

    [Fact]
    public void Create_PartialRoom_SubtractsTheNoSpaceCount()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        for (int i = 0; i < 15; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Give(inv, Bandage, 2); // last slot: a stack of 2/3
        session.Clear();

        InventoryResult result = inv.CreateItemFromSpell(Bandage, 3, out Item? item, out uint created);
        Assert.Equal(InventoryResult.InventoryFull, result); // 3 do not fit: only 1 merges into the stack
        Assert.Equal(1u, created);
        Assert.Equal(3u, item!.Count);
        Assert.Empty(EquipErrors(session)); // partial store is silent

        InventoryResult none = inv.CreateItemFromSpell(Bandage, 3, out Item? nothing, out uint zero);
        Assert.Equal(InventoryResult.InventoryFull, none);
        Assert.Equal(0u, zero);
        Assert.Null(nothing);
        Assert.Empty(EquipErrors(session)); // vmangos does nothing when nothing fits
    }

    [Fact]
    public void Create_UniqueLimit_StoresWhatIsAllowed()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        InventoryResult result = inv.CreateItemFromSpell(Trinket, 5, out Item? item, out uint created);
        Assert.Equal(InventoryResult.CantCarryMoreOfThis, result);
        Assert.Equal(2u, created);
        Assert.Equal(2u, item!.Count);
        Assert.Empty(EquipErrors(session));
    }

    [Fact]
    public void Create_UnknownEntry_ReportsItemNotFound()
    {
        (Player player, FakeSession session) = Make();
        Assert.Equal(InventoryResult.ItemNotFound, player.Inventory.CreateItemFromSpell(424242, 1, out Item? item, out _));
        Assert.Null(item);
        Assert.Equal([InventoryResult.ItemNotFound], EquipErrors(session));
    }

    [Fact]
    public void Create_SetsCreatorOnlyForSignatureItems()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        inv.CreateItemFromSpell(Dagger, 1, out Item? crafted, out _);
        inv.CreateItemFromSpell(Marker, 1, out Item? noCreator, out _);
        inv.CreateItemFromSpell(Hearthstone, 1, out Item? stone, out _);
        inv.CreateItemFromSpell(Bandage, 1, out Item? stackable, out _);

        Assert.Equal(player.Guid, crafted!.Creator);
        Assert.True(noCreator!.Creator.IsEmpty);
        Assert.True(stone!.Creator.IsEmpty);
        Assert.True(stackable!.Creator.IsEmpty);
    }

    [Fact]
    public void Create_ConsumableAndQuestClassesHaveNoSignature()
    {
        Assert.False(PlayerInventory.HasSignature(Store.Find(ToughJerky)!)); // stackable consumable
        Assert.False(PlayerInventory.HasSignature(new ItemTemplate { Entry = 1, Class = 0, Stackable = 1 }));
        Assert.False(PlayerInventory.HasSignature(new ItemTemplate { Entry = 1, Class = 12, Stackable = 1 }));
        Assert.True(PlayerInventory.HasSignature(new ItemTemplate { Entry = 1, Class = 4, Stackable = 1 }));
    }
}
