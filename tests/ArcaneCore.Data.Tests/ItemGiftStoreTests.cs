using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The gift-wrap columns on <c>item_instance</c> (characters <see cref="ItemGiftDataModule.Version"/>; vmangos keeps the same two values in
/// <c>character_gifts</c>): they round-trip with the inventory, and a database from before the step gains them with zero defaults and keeps its rows.
/// </summary>
public sealed class ItemGiftStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AWrappedItem_RoundTripsItsOwnEntryAndFlags(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Giver", Race = 1, Class = 1, Level = 1 });
        var wrapped = new ItemInstanceData
        {
            Guid = 4100, Entry = 5043, Flags = 0x8, GiftCreator = (ulong)character.Id, GiftEntry = 2589, GiftFlags = 0x1, Durability = 7,
            Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        };
        var store = new EfItemStore(db);
        await store.SaveInventoryAsync(character.Id, new InventorySnapshot([new InventoryItemData(0, 23, wrapped)]));

        InventoryItemData loaded = Assert.Single(await store.GetInventoryAsync(character.Id));
        Assert.Equal((5043u, 0x8u, 2589u, 0x1u, 7u), (loaded.Item.Entry, loaded.Item.Flags, loaded.Item.GiftEntry, loaded.Item.GiftFlags, loaded.Item.Durability));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADatabaseBeforeTheGiftStep_GainsTheColumnsWithZeroDefaults_AndKeepsItsItems(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            var row = new ItemInstanceRow { Guid = 4200 };
            row.CopyFrom(9, new ItemInstanceData { Guid = 4200, Entry = 25, Durability = 20, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] });
            db.Add(row);
            await db.SaveChangesAsync();
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            foreach (string column in new[] { ItemGiftDataModule.GiftEntryColumn, ItemGiftDataModule.GiftFlagsColumn })
            {
                string drop = $"ALTER TABLE {sql.DelimitIdentifier("item_instance")} DROP COLUMN {sql.DelimitIdentifier(column)}";
                await db.Database.ExecuteSqlRawAsync(drop);
            }

            SchemaVersionRow version = await db.Set<SchemaVersionRow>().SingleAsync();
            version.Version = ItemGiftDataModule.Version - 1;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        // Two startups: the step runs once and a repeat has nothing left to add.
        for (int pass = 0; pass < 2; pass++)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            ItemInstanceRow kept = await db.Set<ItemInstanceRow>().AsNoTracking().SingleAsync(r => r.Guid == 4200);
            Assert.Equal((25u, 20u, 0u, 0u), (kept.ItemId, kept.Durability, kept.GiftEntry, kept.GiftFlags));
        }
    }

    [Fact]
    public void Module_AddsTwoItemColumns_AtTheReservedVersion()
    {
        var module = new ItemGiftDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(ItemGiftDataModule.Version, module.SchemaVersion);
        Assert.Equal(
            [new AddColumnChange("item_instance", "gift_entry"), new AddColumnChange("item_instance", "gift_flags")],
            module.SchemaChanges);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
