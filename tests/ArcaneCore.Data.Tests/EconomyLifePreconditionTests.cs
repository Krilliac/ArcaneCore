using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class EconomyLifePreconditionTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StaleLife_IsAConflict_AndLeavesMoneyInventoryAndLifeUntouched(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int id;
        CharacterState durable;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "LifeEconomy", Race = 1, Class = 1, Level = 10 });
            durable = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, Money: 100,
                Inventory: new InventorySnapshot([new InventoryItemData(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 2 })]),
                Life: Life());
            id = character.Id;
            await new EfCharacterStore(db).SaveStateAsync(durable);
        }

        CharacterLife[] staleLives =
        [
            Life() with { Health = 431 },
            Life() with { Powers = [10, 21, 30, 40, 50] },
            Life() with { Xp = 1235 },
            Life() with { DeathExpireUnix = 1_700_000_301 },
            Life() with { IsGhost = true },
            Life() with { Corpse = new CorpseSnapshot(1, 2, 3, 4, 5, 6, 1) },
        ];

        foreach (CharacterLife stale in staleLives)
        {
            CharacterState before = durable with { Life = stale };
            CharacterState after = before with { Money = 999 };
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
            Assert.Equal(EconomyCommitResult.Conflict, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [new EconomyParticipant(before, after)], [])));
        }

        await using (CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(100u, (await new EfCharacterStore(check).GetByIdAsync(id))!.Money);
            Assert.Single(await new EfItemStore(check).GetInventoryAsync(id));
            CharacterLife actual = Assert.IsType<CharacterLife>(await new EfCharacterLifeStore(check).LoadAsync(id));
            Assert.Equal((432u, 1234u, 1_700_000_300L, false), (actual.Health, actual.Xp, actual.DeathExpireUnix, actual.IsGhost));
            Assert.Equal([10u, 20u, 30u, 40u, 50u], actual.Powers);
            Assert.Null(actual.Corpse);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MatchingLife_AllowsAtomicMoneyInventoryAndLifeUpdate(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterState durable) = await CreateSeedAsync(provider, includeLife: true);
        CharacterState after = durable with
        {
            Money = 250,
            Inventory = new InventorySnapshot([new InventoryItemData(0, 24, new ItemInstanceData { Guid = 101, Entry = 118, Count = 1 })]),
            Life = Life() with { Health = 400, Powers = [11, 21, 31, 41, 51], Xp = 1300 },
        };

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [new EconomyParticipant(durable, after, [100])], [])));
        }

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(250u, (await new EfCharacterStore(check).GetByIdAsync(durable.Id))!.Money);
        Assert.Equal(101u, Assert.Single(await new EfItemStore(check).GetInventoryAsync(durable.Id)).Item.Guid);
        CharacterLife actual = Assert.IsType<CharacterLife>(await new EfCharacterLifeStore(check).LoadAsync(durable.Id));
        Assert.Equal((400u, 1300u), (actual.Health, actual.Xp));
        Assert.Equal([11u, 21u, 31u, 41u, 51u], actual.Powers);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NonNullBeforeLife_ConflictsWhenDurableLifeIsMissing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterState durable) = await CreateSeedAsync(provider, includeLife: false);
        CharacterState before = durable with { Life = Life() };
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(EconomyCommitResult.Conflict, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [new EconomyParticipant(before, before with { Money = 999 })], [])));
    }

    [Fact]
    public async Task ConsumedItemDeclaration_RejectsDuplicateAndKeptGuids()
    {
        (DatabaseConnectionOptions connection, CharacterState durable) = await CreateSeedAsync(TestDatabases.AvailableProviders().First().Cast<DatabaseProvider>().Single(), includeLife: true);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Throws<ArgumentException>(() => EconomyRequestValidation.Validate(new EconomyCommitRequest(
            Guid.NewGuid(), [new EconomyParticipant(durable, durable, [100, 100])], [])));
        Assert.Throws<ArgumentException>(() => EconomyRequestValidation.Validate(new EconomyCommitRequest(
            Guid.NewGuid(), [new EconomyParticipant(durable, durable, [999])], [])));
    }

    private static CharacterLife Life() => new(432, [10, 20, 30, 40, 50], 1234, 1_700_000_300, false, null);

    private async Task<(DatabaseConnectionOptions Connection, CharacterState State)> CreateSeedAsync(DatabaseProvider provider, bool includeLife)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = $"LifeEconomy{Guid.NewGuid():N}", Race = 1, Class = 1, Level = 10 });
        CharacterState state = new(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, Money: 100,
            Inventory: new InventorySnapshot([
                new InventoryItemData(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 2 }),
                new InventoryItemData(0, 24, new ItemInstanceData { Guid = 101, Entry = 118, Count = 1 }),
            ]),
            Life: includeLife ? Life() : null);
        await new EfCharacterStore(db).SaveStateAsync(state);
        return (connection, state);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
