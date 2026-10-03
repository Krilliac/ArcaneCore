using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The selected ammo (PLAYER_AMMO_ID) persisted with the inventory, on every engine.</summary>
public sealed class CharacterItemStateStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Ammo_SavesWithTheInventory_NullLeavesItAlone_ReplacesAndFollowsDeletion(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var items = new EfItemStore(db);
        var states = new EfItemStateStore(db);
        var characters = new EfCharacterStore(db);
        CharacterRecord created = await characters.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Hunter", Race = 1, Class = 3, Level = 1 });

        Assert.Equal(0u, await states.GetAmmoAsync(created.Id));

        await items.SaveInventoryAsync(created.Id, new InventorySnapshot([], AmmoId: 2512));
        Assert.Equal(2512u, await states.GetAmmoAsync(created.Id));

        // A snapshot that does not carry the ammo (an economy or reward copy) leaves the stored one.
        await items.SaveInventoryAsync(created.Id, new InventorySnapshot([]));
        Assert.Equal(2512u, await states.GetAmmoAsync(created.Id));

        await items.SaveInventoryAsync(created.Id, new InventorySnapshot([], AmmoId: 2516));
        Assert.Equal(2516u, await states.GetAmmoAsync(created.Id));
        await items.SaveInventoryAsync(created.Id, new InventorySnapshot([], AmmoId: 0));
        Assert.Equal(0u, await states.GetAmmoAsync(created.Id));

        // The character save path carries it too.
        await characters.SaveStateAsync(new CharacterState(created.Id, 0, 12, 1, 2, 3, 0, 1, 10,
            Inventory: new InventorySnapshot([], AmmoId: 2512)));
        Assert.Equal(2512u, await states.GetAmmoAsync(created.Id));

        Assert.True(await characters.DeleteAsync(created.Id, 3));
        Assert.Equal(0u, await states.GetAmmoAsync(created.Id));
    }

    [Fact]
    public void Module_IsACharactersTable_WithACleanup()
    {
        var module = new CharacterItemStateDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterItemStateDataModule.Version, module.SchemaVersion);
        Assert.Contains(module.SchemaChanges, c => c is CreateTableChange { Table: CharacterItemStateDataModule.Table });
        Assert.IsAssignableFrom<ICharacterDataCleanup>(module);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
