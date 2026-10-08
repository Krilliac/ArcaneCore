using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Random item properties (vmangos Item::GenerateItemRandomPropertyId / SetItemRandomProperties, Item.cpp:792-834; GetItemEnchantMod,
/// ItemEnchantmentMgr.cpp): the roll over an <c>item_enchantment_template</c> entry, the field and the three property enchantment slots, and the
/// new-item paths that roll (StoreNewItem: loot, vendors, spells, quest rewards). Content is synthetic.
/// </summary>
public sealed class ItemRandomPropertyTests
{
    private const uint Sword = 92_000;
    private const uint PlainSword = 92_001;
    private const uint OrphanSword = 92_002;
    private const uint Group = 6;
    private const uint OrphanGroup = 7;

    private static readonly ItemTemplateStore Content = new(
        [
            .. Templates,
            new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, RandomProperty = Group },
            new ItemTemplate { Entry = PlainSword, Class = 2, SubClass = 7, Name = "Plain Sword", DisplayId = 1, InventoryType = 13, Delay = 2000 },
            new ItemTemplate { Entry = OrphanSword, Class = 2, SubClass = 7, Name = "Orphan Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, RandomProperty = OrphanGroup },
        ], []);

    private static readonly ItemRandomPropertyCatalog Catalog = new(
        [new ItemRandomPropertyRecord(1001, "of the Bear", [74, 75, 0]), new ItemRandomPropertyRecord(1002, "of the Eagle", [76, 77, 78])],
        [new ItemEnchantmentChance(Group, 1001, 25f), new ItemEnchantmentChance(Group, 1002, 75f), new ItemEnchantmentChance(9, 4040, 100f)]);

    private static ItemRandomProperties Roller(float percent, List<string>? reports = null)
        => new(Catalog, () => percent, reports is null ? null : reports.Add);

    [Theory]
    [InlineData(0f, 1001)]
    [InlineData(25f, 1001)]      // the running sum reaches the roll: the first row
    [InlineData(25.01f, 1002)]
    [InlineData(99.99f, 1002)]
    public void Generate_PicksByTheRunningChance(float percent, int expected)
        => Assert.Equal(expected, Roller(percent).Generate(Content.Find(Sword)!));

    [Fact]
    public void Generate_IsZero_WithoutARandomProperty_OrWithoutRows_OrForAnUnknownDbcRow()
    {
        var reports = new List<string>();
        ItemRandomProperties roller = Roller(50f, reports);
        Assert.Equal(0, roller.Generate(Content.Find(PlainSword)!));
        Assert.Equal(0, roller.Generate(Content.Find(OrphanSword)!));
        Assert.Equal(0, roller.Generate(Content.Find(OrphanSword)!));
        Assert.Equal(0, roller.Generate(new ItemTemplate { Entry = 1, RandomProperty = 9 }));   // 4040 is not in the DBC
        Assert.Equal(2, reports.Count);   // once per content error
    }

    [Fact]
    public void ANewItem_RollsItsProperty_IntoTheFieldAndThePropertySlots()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Content;
        player.Inventory.Load([]);
        player.Inventory.RandomProperties = Roller(80f);

        Item sword = Give(player.Inventory, Sword);

        Assert.Equal(1002, sword.RandomPropertyId);
        Assert.Equal(1002, sword.GetInt32(UpdateFields.ItemFieldRandomPropertiesId));
        Assert.Equal((76u, 77u, 78u), (ItemEnchantments.Id(sword, EnchantSlots.Property0), ItemEnchantments.Id(sword, EnchantSlots.Property0 + 1),
            ItemEnchantments.Id(sword, EnchantSlots.Property0 + 2)));
        Assert.Equal(0u, ItemEnchantments.Id(sword, EnchantSlots.Permanent));
        ItemInstanceData saved = sword.ToData();
        Assert.Equal(1002, saved.RandomPropertyId);
        Assert.Equal(76u, saved.Enchantments[EnchantSlots.Property0 * EnchantSlots.FieldsPerSlot]);

        Item plain = Give(player.Inventory, PlainSword);
        Assert.Equal(0, plain.RandomPropertyId);
    }

    [Fact]
    public void WithoutContent_NewItemsHaveNoProperty()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Content;
        player.Inventory.Load([]);
        Assert.Equal(0, Give(player.Inventory, Sword).RandomPropertyId);
    }

    [Fact]
    public void AGivenProperty_IsUsedInsteadOfARoll()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Content;
        player.Inventory.Load([]);
        player.Inventory.RandomProperties = Roller(80f);
        var dest = new List<ItemPosCount>();
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStoreNewItem(Sword, 1, dest, out _));
        Item sword = player.Inventory.StoreNewItem(dest, Content.Find(Sword)!, 1, randomPropertyId: 1001);
        Assert.Equal(1001, sword.RandomPropertyId);
        Assert.Equal(74u, ItemEnchantments.Id(sword, EnchantSlots.Property0));
    }

    [Fact]
    public void AQuestRewardItem_RollsToo()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Templates = Content;
        player.Inventory.Load([]);
        player.Inventory.RandomProperties = Roller(10f);
        Assert.Equal(InventoryResult.Ok, player.Inventory.TryStageQuestRewards([new InventoryRewardGrant(Sword, 1)], out InventoryRewardStage? stage, out _));
        Assert.Equal(1001, stage!.After.Items.Single(r => r.Item.Entry == Sword).Item.RandomPropertyId);   // the persisted snapshot holds the roll
        player.Inventory.ApplyQuestRewardInventory(stage);
        Assert.Equal(1001, player.Inventory.AllItems.Single(i => i.Entry == Sword).RandomPropertyId);
    }
}
