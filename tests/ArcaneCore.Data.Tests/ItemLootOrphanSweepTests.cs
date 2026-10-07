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
/// The startup sweep of generated container loot whose item no longer exists (vmangos CharacterDatabaseCleaner::CleanOrphanedItemData,
/// <c>RemoveOrphanedRows("item_loot", "guid", "item_instance", "guid")</c>, and ObjectMgr::SetHighestGuids): rows left by older builds, before
/// the escrow paths removed loot with their items, are deleted; loot of owned and escrowed (owner 0) items stays.
/// </summary>
public sealed class ItemLootOrphanSweepTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Sweep_RemovesLootOfMissingItems_AndKeepsOwnedAndEscrowedLoot(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        var owned = new ItemLootData(10, [new(0, 117, 2, false)]);
        var escrowed = new ItemLootData(0, [new(0, 118, 1, false), new(1, 119, 1, true)]);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Sweeper", Race = 1, Class = 1, Level = 1 });
            foreach ((uint guid, int owner, ItemLootData contents) in new[] { (100u, character.Id, owned), (200u, 0, escrowed) })
            {
                var row = new ItemInstanceRow { Guid = guid };
                row.CopyFrom(owner, new ItemInstanceData { Guid = guid, Entry = 4632, Loot = contents });
                db.Add(row);
            }

            // Orphans: a money-only state, a state with stacks, and stacks without a state row, none with an item_instance row.
            ItemInstanceData[] orphans =
            [
                new() { Guid = 300, Entry = 4632, Loot = new ItemLootData(25, []) },
                new() { Guid = 400, Entry = 4632, Loot = new ItemLootData(5, [new(0, 117, 1, false), new(1, 118, 3, false)]) },
            ];
            await ItemLootPersistence.StageReplaceAsync(db, [new() { Guid = 100, Entry = 4632, Loot = owned }, new() { Guid = 200, Entry = 4632, Loot = escrowed }, .. orphans],
                [100, 200, 300, 400], CancellationToken.None);
            db.Add(new ItemLootRow { ItemGuid = 500, Slot = 0, ItemId = 117, Amount = 1 });
            await db.SaveChangesAsync();
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(5, await new EfItemLootMaintenance(db).DeleteOrphanedLootAsync());
            Assert.Equal(0, await new EfItemLootMaintenance(db).DeleteOrphanedLootAsync());
        }

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        uint[] states = await check.Set<ItemLootStateRow>().OrderBy(r => r.ItemGuid).Select(r => r.ItemGuid).ToArrayAsync();
        uint[] stacks = await check.Set<ItemLootRow>().OrderBy(r => r.ItemGuid).ThenBy(r => r.Slot).Select(r => r.ItemGuid).ToArrayAsync();
        Assert.Equal(new uint[] { 100, 200 }, states);
        Assert.Equal(new uint[] { 100, 200, 200 }, stacks);
        Dictionary<uint, ItemLootData> loot = await ItemLootPersistence.LoadAsync(check, [100, 200], CancellationToken.None);
        Assert.Equal(owned, loot[100]);
        Assert.Equal(escrowed, loot[200]);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
