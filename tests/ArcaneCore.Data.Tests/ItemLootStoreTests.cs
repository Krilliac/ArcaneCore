using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The generated loot of container items (vmangos item_loot + generated_loot) kept with the inventory: written in the same SaveChanges as the
/// inventory snapshot, loaded with it, removed with the item or the character. Every store test is a provider theory over
/// <see cref="TestDatabases.AvailableProviders"/> (SQLite only on a machine without the MariaDB/PostgreSQL servers: the hosted CI runs the rest).
/// </summary>
public sealed class ItemLootStoreTests : IAsyncLifetime
{
    private const uint Lockbox = 4632;
    private const uint Junkbox = 16882;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ItemInstanceData Box(uint guid, uint entry = Lockbox, ItemLootData? loot = null)
        => new() { Guid = guid, Entry = entry, Count = 1, Loot = loot };

    private static ItemLootData Loot(uint gold, params (byte Slot, uint Item, uint Count, bool Quest)[] items)
        => new(gold, [.. items.Select(i => new ItemLootEntry(i.Slot, i.Item, i.Count, i.Quest))]);

    [Fact]
    public void ItemInstanceData_EqualityIgnoresLootIdentity_ButNotContent()
    {
        ItemInstanceData none = Box(1);
        Assert.Equal(none, Box(1));                                                  // items without loot compare as before
        Assert.Equal(Box(1, loot: Loot(5, (0, 117, 2, false))), Box(1, loot: Loot(5, (0, 117, 2, false))));
        Assert.NotEqual(Box(1, loot: Loot(5, (0, 117, 2, false))), Box(1, loot: Loot(5, (0, 117, 3, false))));
        Assert.NotEqual(none, Box(1, loot: Loot(0)));
    }

    [Fact]
    public void Module_IsTheNextCharactersStep_WithTwoTablesAndACleanup()
    {
        SchemaStep step = Assert.Single(CharacterDbContext.Schema.Steps, s => s.Version == ItemLootDataModule.Version);
        Assert.Equal(ItemLootDataModule.Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(CharacterDataCleanups.All, c => c is ItemLootDataModule);
        Assert.DoesNotContain(CharacterDataCleanups.Missing, m => m is ItemLootDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SaveInventory_WithLoot_RoundTripsItemsGoldAndSlots_IncludingTwoRollsOfTheSameItem(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfItemStore(db);
        ItemLootData loot = Loot(125, (0, 117, 2, false), (1, 117, 1, false), (2, 91000, 1, true));

        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 23, Box(100, loot: loot)), new(0, 24, Box(101, Junkbox))]));

        IReadOnlyList<InventoryItemData> loaded = await store.GetInventoryAsync(7);
        Assert.Equal(loot, loaded.Single(r => r.Item.Guid == 100).Item.Loot);
        Assert.Null(loaded.Single(r => r.Item.Guid == 101).Item.Loot);               // never generated: no generated_loot
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SaveInventory_UpdatesTheLootWhenStacksAreTaken_AndClearsItWhenTheItemHasNone(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfItemStore(db);
        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 23, Box(100, loot: Loot(125, (0, 117, 2, false), (1, 118, 1, false), (2, 119, 4, false))))]));

        // Money and the first stack taken, the second one's count changed by nothing, a third remains.
        ItemLootData rest = Loot(0, (1, 118, 1, false), (2, 119, 4, false));
        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 23, Box(100, loot: rest))]));
        Assert.Equal(rest, (await store.GetInventoryAsync(7)).Single().Item.Loot);

        // An opened item whose loot is gone: state and rows disappear, the item stays (loot cleared).
        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 23, Box(100))]));
        Assert.Null((await store.GetInventoryAsync(7)).Single().Item.Loot);
        Assert.Empty(await db.Set<ItemLootStateRow>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<ItemLootRow>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RemovingTheItem_RemovesItsLootInTheSameSave_NoOrphans(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfItemStore(db);
        await store.SaveInventoryAsync(7, new InventorySnapshot(
            [new(0, 23, Box(100, loot: Loot(5, (0, 117, 1, false)))), new(0, 24, Box(101, Junkbox, Loot(0, (0, 118, 1, false))))]));

        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 24, Box(101, Junkbox, Loot(0, (0, 118, 1, false))))]));

        Assert.Equal([101u], (await db.Set<ItemLootStateRow>().AsNoTracking().ToListAsync()).Select(r => r.ItemGuid));
        Assert.Equal([101u], (await db.Set<ItemLootRow>().AsNoTracking().ToListAsync()).Select(r => r.ItemGuid));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AnItemHandedToAnotherCharacter_KeepsItsLoot(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfItemStore(db);
        ItemLootData loot = Loot(0, (0, 117, 1, false));
        await store.SaveInventoryAsync(7, new InventorySnapshot([new(0, 23, Box(100, loot: loot))]));

        // Trade/mail hand-over: the receiver's snapshot carries the item, the giver's no longer does.
        await store.SaveInventoryAsync(8, new InventorySnapshot([new(0, 23, Box(100, loot: loot))]));
        await store.SaveInventoryAsync(7, new InventorySnapshot([]));

        Assert.Equal(loot, (await store.GetInventoryAsync(8)).Single().Item.Loot);
        Assert.Empty(await store.GetInventoryAsync(7));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterSaveAndDeletion_CarryTheLoot(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        var items = new EfItemStore(db);
        CharacterRecord created = await characters.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Looter", Race = 1, Class = 4, Level = 1 });
        ItemLootData loot = Loot(40, (0, 117, 1, false));

        await characters.SaveStateAsync(new CharacterState(created.Id, 0, 12, 1, 2, 3, 0, 1, 10,
            Inventory: new InventorySnapshot([new(0, 23, Box(5, loot: loot))])));
        Assert.Equal(loot, Assert.Single(await items.GetInventoryAsync(created.Id)).Item.Loot);

        Assert.True(await characters.DeleteAsync(created.Id, 3));

        Assert.Empty(await items.GetInventoryAsync(created.Id));
        Assert.Empty(await db.Set<ItemLootStateRow>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<ItemLootRow>().AsNoTracking().ToListAsync());
    }
}