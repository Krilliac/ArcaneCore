using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Bank;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CharacterBankSlotsTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SlotCountAndPurchaseMoney_RoundTripTogether(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "BankCustomer" });

        await store.SaveStateAsync(new CharacterState(created.Id, 0, 0, 0, 0, 0, 0, 1, 10,
            Money: 8000, BankBagSlotCount: 1));

        CharacterRecord reloaded = (await store.GetByIdAsync(created.Id))!;
        Assert.Equal((8000u, (byte)1), (reloaded.Money, reloaded.BankBagSlotCount));
        Assert.True(await store.DeleteAsync(created.Id, 3));
        Assert.Null(await store.GetByIdAsync(created.Id));
    }

    [Fact]
    public void Module_AddsOneCharacterColumn_AndOwnsDeletionCleanup()
    {
        var module = new CharacterBankSlotsDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterBankSlotsDataModule.Version, module.SchemaVersion);
        Assert.Contains(module.SchemaChanges, change => change is AddColumnChange
        {
            Table: "characters", Column: "bank_bag_slots",
        });
        Assert.IsAssignableFrom<ICharacterDataCleanup>(module);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
