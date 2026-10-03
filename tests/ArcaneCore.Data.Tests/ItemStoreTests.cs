using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Item persistence (item_instance, character_inventory) and item content (item_template, playercreateinfo_item) on every engine.</summary>
public sealed class ItemStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ItemStore_RoundTripsAndReplacesInventories(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfItemStore(db);
        Assert.Equal(0u, await store.GetMaxItemGuidAsync());

        var bag = Item(100, 4496);
        var sword = Item(101, 25) with { Durability = 17, Creator = 0x0000000000000007, Flags = 1 };
        var potion = Item(102, 118) with { Count = 3, Charges = [-1, 0, 0, 0, 2], Enchantments = Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(), RandomPropertyId = -5, Duration = 3600 };
        await store.SaveInventoryAsync(7,
            new InventorySnapshot([new(0, 19, bag), new(0, 15, sword), new(100, 2, potion)]));

        IReadOnlyList<InventoryItemData> loaded = await store.GetInventoryAsync(7);
        Assert.Equal([(0u, (byte)15, 101u), (0u, (byte)19, 100u), (100u, (byte)2, 102u)], loaded.Select(r => (r.ContainerGuid, r.Slot, r.Item.Guid)));
        ItemInstanceData potionBack = loaded[2].Item;
        Assert.Equal((118u, 3u, -5, 3600u), (potionBack.Entry, potionBack.Count, potionBack.RandomPropertyId, potionBack.Duration));
        Assert.Equal(potion.Charges, potionBack.Charges);
        Assert.Equal(potion.Enchantments, potionBack.Enchantments);
        Assert.Equal((17u, 7ul, 1u), (loaded[0].Item.Durability, loaded[0].Item.Creator, loaded[0].Item.Flags));
        Assert.Equal(102u, await store.GetMaxItemGuidAsync());

        IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>> equipped = await store.GetEquippedEntriesAsync([7, 8]);
        Assert.Equal(new Dictionary<byte, uint> { [15] = 25, [19] = 4496 }, equipped[7]);
        Assert.False(equipped.ContainsKey(8));

        // Replace: the sword moves, the potion is used up, a new item arrives.
        await store.SaveInventoryAsync(7,
            new InventorySnapshot([new(0, 19, bag), new(0, 23, sword with { Durability = 10 }), new(0, 24, Item(103, 117) with { Count = 4 })]));
        loaded = await store.GetInventoryAsync(7);
        Assert.Equal([(0u, (byte)19, 100u), (0u, (byte)23, 101u), (0u, (byte)24, 103u)], loaded.Select(r => (r.ContainerGuid, r.Slot, r.Item.Guid)));
        Assert.Equal(10u, loaded[1].Item.Durability);

        // Another character's items are untouched; an item handed over changes owner.
        await store.SaveInventoryAsync(8, new InventorySnapshot([new(0, 23, Item(200, 117))]));
        await store.SaveInventoryAsync(8, new InventorySnapshot([new(0, 23, Item(200, 117)), new(0, 24, sword)]));
        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 19, bag), new(0, 24, Item(103, 117) with { Count = 4 })]));
        Assert.Equal([200u, 101u], (await store.GetInventoryAsync(8)).Select(r => r.Item.Guid));
        Assert.Equal([100u, 103u], (await store.GetInventoryAsync(7)).Select(r => r.Item.Guid));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterSave_WritesTheInventory_AndDeleteRemovesIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        var items = new EfItemStore(db);
        CharacterRecord created = await characters.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Packer", Race = 1, Class = 1, Level = 1 });

        await characters.SaveStateAsync(new CharacterState(created.Id, 0, 12, 1, 2, 3, 0, 1, 10,
            Inventory: new InventorySnapshot([new(0, 23, Item(5, 117) with { Count = 2 })])));
        Assert.Equal(2u, Assert.Single(await items.GetInventoryAsync(created.Id)).Item.Count);

        // A save without an inventory leaves the items alone.
        await characters.SaveStateAsync(new CharacterState(created.Id, 0, 12, 1, 2, 3, 0, 1, 11));
        Assert.Single(await items.GetInventoryAsync(created.Id));

        Assert.True(await characters.DeleteAsync(created.Id, 3));
        Assert.Empty(await items.GetInventoryAsync(created.Id));
        Assert.Equal(0u, await items.GetMaxItemGuidAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TemplateSource_ReadsVmangosShapedContent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

        var hammer = new ItemTemplate
        {
            Entry = 7230, Class = 2, SubClass = 5, Name = "Smite's Mighty Hammer", Description = "It's heavy.", DisplayId = 19610, Quality = 3,
            Flags = 0x80000, BuyCount = 1, BuyPrice = 15515, SellPrice = 3103, InventoryType = 17, AllowableClass = 1503, AllowableRace = 511,
            ItemLevel = 23, RequiredLevel = 18, RequiredSkill = 160, RequiredSkillRank = 1, RequiredSpell = 2, RequiredHonorRank = 3,
            RequiredCityRank = 4, RequiredReputationFaction = 5, RequiredReputationRank = 6, MaxCount = 1, Stackable = 1, ContainerSlots = 0,
            Stats = [new ItemStat(4, 11), new ItemStat(3, -4)], Delay = 3500, RangedModRange = 100f, AmmoType = 2,
            Damages = [new ItemDamage(55, 83, 0), new ItemDamage(1, 2, 2)], Block = 7, Armor = 8, HolyRes = 1, FireRes = 2, NatureRes = 3,
            FrostRes = 4, ShadowRes = 5, ArcaneRes = 6, Spells = [new ItemSpell(0, 0, 0, 0, -1, 0, -1), new ItemSpell(18833, 2, -1, 1.5f, 1000, 4, 2000)],
            Bonding = 2, PageText = 1, PageLanguage = 7, PageMaterial = 2, StartQuest = 9, LockId = 10, Material = 1, Sheath = 1,
            RandomProperty = 11, SetId = 12, MaxDurability = 80, AreaBound = 13, MapBound = 14, Duration = 15, BagFamily = 0,
            DisenchantId = 16, FoodType = 0, MinMoneyLoot = 17, MaxMoneyLoot = 18, WrappedGift = 19, ExtraFlags = 20, OtherTeamEntry = 21,
        }.Normalized();
        db.Add(ItemTemplateRow.FromTemplate(hammer));
        db.Add(ItemTemplateRow.FromTemplate(new ItemTemplate { Entry = 117, Name = "Tough Jerky", Stackable = 0 }));
        db.Add(new PlayerCreateInfoItemRow { Race = 1, Class = 1, ItemId = 117, Amount = 4 });
        db.Add(new PlayerCreateInfoItemRow { Race = 1, Class = 1, ItemId = 25, Amount = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var source = new EfItemTemplateSource(db);
        IReadOnlyList<ItemTemplate> templates = await source.LoadTemplatesAsync();
        ItemTemplate back = templates.Single(t => t.Entry == 7230);
        Assert.Equal(hammer with { Stats = back.Stats, Damages = back.Damages, Spells = back.Spells }, back);
        Assert.Equal(hammer.Stats, back.Stats);
        Assert.Equal(hammer.Damages, back.Damages);
        Assert.Equal(hammer.Spells, back.Spells);
        Assert.Equal(1u, templates.Single(t => t.Entry == 117).Stackable); // vmangos load-time correction

        Assert.Equal([new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, 117, 4)], await source.LoadStartingItemsAsync());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ItemInstanceData Item(uint guid, uint entry) => new() { Guid = guid, Entry = entry, Count = 1 };
}
