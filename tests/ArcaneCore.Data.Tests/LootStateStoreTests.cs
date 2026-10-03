using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Loot;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Durable chest loot of dungeon instances on the provider matrix (SQLite always; MariaDB and
/// PostgreSQL when their test connection strings are set): a commit writes the chest and the
/// awarded inventory in one transaction, a retry is AlreadyCommitted, a stale expected state, a
/// forged successor, an award that differs from the inventory change or a missing instance
/// writes nothing, and the rows follow the instance and survive character deletion.
/// </summary>
public sealed class LootStateStoreTests : IAsyncLifetime
{
    private const uint Instance = 101;
    private const uint Chest = 77002;
    private const long Respawn = 1_900_000_600;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_WritesStateAndParticipantInOneTransaction_RetryIsAlreadyCommitted(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, Generation(generated)));
        Assert.Equal(generated, Assert.Single(await LoadAsync(seed)));

        LootCommitRequest take = Take(seed, generated, slot: 0, newGuid: 102);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, take));
        Assert.Equal(take.Updated, Assert.Single(await LoadAsync(seed)));
        await AssertInventoryAsync(seed, seed.A.Id, take.Participants[0].After);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(r => r.Guid == 102));
            Assert.True(await new EfLootStateStore(db).IsCommittedAsync(take.OperationId));
            Assert.False(await new EfLootStateStore(db).IsCommittedAsync(Guid.NewGuid()));
        }

        // The retried acknowledgement applies nothing twice.
        Assert.Equal(LootCommitResult.AlreadyCommitted, await CommitAsync(seed, take));
        await AssertInventoryAsync(seed, seed.A.Id, take.Participants[0].After);
        Assert.Equal(take.Updated, Assert.Single(await LoadAsync(seed)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_StaleExpected_IsConflict_WritesNothing(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed);
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest first = Take(seed, generated, slot: 0, newGuid: 102);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, first));

        // A second take planned from the state before the first one: stored state moved on.
        LootCommitRequest stale = Take(seed with { A = first.Participants[0].After }, generated, slot: 0, newGuid: 103);
        Assert.Equal(LootCommitResult.Conflict, await CommitAsync(seed, stale));

        // A generation planned against "no state" while a chest is stored.
        Assert.Equal(LootCommitResult.Conflict, await CommitAsync(seed, Generation(generated with { Generation = 1 })));

        Assert.Equal(first.Updated, Assert.Single(await LoadAsync(seed)));
        await AssertInventoryAsync(seed, seed.A.Id, first.Participants[0].After);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(0, await db.Set<ItemInstanceRow>().CountAsync(r => r.Guid == 103));
        Assert.Equal(2, await db.Set<LootOperationRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_UpdatedNotASuccessorOfExpected_IsInvalidTransition(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed);
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest first = Take(seed, generated, slot: 0, newGuid: 102);
        await CommitAsync(seed, first);
        LootStateRecord current = first.Updated;
        CharacterState after = first.Participants[0].After;

        // Re-mark the already taken slot and grant a fresh item for it: the duplicate award.
        LootAward again = new(seed.A.Id, 0, 117, 2);
        CharacterState regrant = after with
        {
            Inventory = new InventorySnapshot([.. after.Inventory!.Items, new InventoryItemData(0, 30, Item(103, 117, 2))]),
        };
        var duplicate = new LootCommitRequest(Guid.NewGuid(), current.Key, current, current, [new EconomyParticipant(after, regrant)], [again]);
        Assert.Equal(LootCommitResult.InvalidTransition, await CommitAsync(seed, duplicate));

        // Updated that rewinds a taken slot to "available", with no awards at all.
        LootStateRecord rewound = current with { Items = [.. current.Items.Select(i => i.Slot == 0 ? i with { IsLooted = false } : i)] };
        Assert.Equal(LootCommitResult.InvalidTransition,
            await CommitAsync(seed, new LootCommitRequest(Guid.NewGuid(), current.Key, current, rewound, [], [])));

        // A forged owner, a forged generation and a forged item stack.
        LootStateRecord reowned = current with { LootOwnerCharacterId = seed.B.Id };
        Assert.Equal(LootCommitResult.InvalidTransition,
            await CommitAsync(seed, new LootCommitRequest(Guid.NewGuid(), current.Key, current, reowned, [], [])));
        LootStateRecord regenerated = current with { Generation = current.Generation + 1 };
        Assert.Equal(LootCommitResult.InvalidTransition,
            await CommitAsync(seed, new LootCommitRequest(Guid.NewGuid(), current.Key, current, regenerated, [], [])));

        Assert.Equal(current, Assert.Single(await LoadAsync(seed)));
        await AssertInventoryAsync(seed, seed.A.Id, after);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(0, await db.Set<ItemInstanceRow>().CountAsync(r => r.Guid == 103));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_ParticipantDeltaDiffersFromAwards_IsInvalidTransition(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed);
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest honest = Take(seed, generated, slot: 0, newGuid: 102);

        // More than the awarded count, another entry, a missing grant and a money change.
        CharacterState before = honest.Participants[0].Before;
        CharacterState[] forged =
        [
            before with { Inventory = Add(before.Inventory!, Item(102, 117, 3)) },
            before with { Inventory = Add(before.Inventory!, Item(102, 118, 2)) },
            before,
            before with { Money = before.Money + 1, Inventory = Add(before.Inventory!, Item(102, 117, 2)) },
        ];
        foreach (CharacterState after in forged)
        {
            Assert.Equal(LootCommitResult.InvalidTransition, await CommitAsync(seed,
                honest with { OperationId = Guid.NewGuid(), Participants = [new EconomyParticipant(before, after)] }));
        }

        Assert.Equal(generated, Assert.Single(await LoadAsync(seed)));
        await AssertInventoryAsync(seed, seed.A.Id, seed.A);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, honest));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_ChestScopeWithoutInstanceRow_IsScopeMissing(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord orphan = Generated(seed) with { Key = new LootStateKey(Instance + 1, Chest) };
        Assert.Equal(LootCommitResult.ScopeMissing, await CommitAsync(seed, Generation(orphan)));
        Assert.Empty(await LoadAsync(seed));
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Empty(await db.Set<LootOperationRow>().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_StaleParticipantNewItemCollisionOrMissingCharacter_WritesNothing(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed);
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest honest = Take(seed, generated, slot: 0, newGuid: 102);
        CharacterState before = honest.Participants[0].Before;

        // Durable money differs from the planned Before.
        CharacterState staleMoney = before with { Money = before.Money + 1 };
        Assert.Equal(LootCommitResult.Conflict, await CommitAsync(seed, honest with
        {
            OperationId = Guid.NewGuid(),
            Participants = [new EconomyParticipant(staleMoney, staleMoney with { Inventory = honest.Participants[0].After.Inventory })],
        }));

        // Durable inventory differs (a stack count changed since the snapshot).
        CharacterState staleInventory = before with
        {
            Inventory = new InventorySnapshot([.. before.Inventory!.Items.Select(i => i.Item.Guid == 101 ? i with { Item = i.Item with { Count = 9 } } : i)]),
        };
        Assert.Equal(LootCommitResult.Conflict, await CommitAsync(seed, honest with
        {
            OperationId = Guid.NewGuid(),
            Participants = [new EconomyParticipant(staleInventory, staleInventory with { Inventory = Add(staleInventory.Inventory!, Item(102, 117, 2)) })],
        }));

        // The granted stack's GUID already belongs to another character's item.
        CharacterState collide = before with { Inventory = Add(before.Inventory!, Item(200, 117, 2)) };
        Assert.Equal(LootCommitResult.Conflict, await CommitAsync(seed, honest with
        {
            OperationId = Guid.NewGuid(), Participants = [new EconomyParticipant(before, collide)],
        }));

        // A participant that does not exist.
        CharacterState ghost = before with { Id = 9999 };
        Assert.Equal(LootCommitResult.CharacterMissing, await CommitAsync(seed, honest with
        {
            OperationId = Guid.NewGuid(),
            Awards = [honest.Awards[0] with { CharacterId = 9999 }],
            Participants = [new EconomyParticipant(ghost, ghost with { Inventory = honest.Participants[0].After.Inventory })],
            Updated = LootStateRules.Replay(generated, [seed.A.Id, seed.B.Id, 9999], 0, [honest.Awards[0] with { CharacterId = 9999 }], Respawn)!,
        }));

        Assert.Equal(generated, Assert.Single(await LoadAsync(seed)));
        await AssertInventoryAsync(seed, seed.A.Id, seed.A);
        await AssertInventoryAsync(seed, seed.B.Id, seed.B);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_TakingEverything_ConsumesTheChest_AndTheNextGenerationFollowsOnlyAfterwards(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed) with { Recipients = [seed.A.Id] };
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest one = Take(seed, generated, slot: 0, newGuid: 102);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, one));
        Assert.False(one.Updated.Consumed);
        LootCommitRequest two = Take(seed with { A = one.Participants[0].After }, one.Updated, slot: 1, newGuid: 103);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, two));
        Assert.False(two.Updated.Consumed);
        LootCommitRequest three = Take(seed with { A = two.Participants[0].After }, two.Updated, slot: 2, newGuid: 104);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, three));
        Assert.True(three.Updated.Consumed);
        Assert.Equal(Respawn, three.Updated.RespawnAtUnix);
        LootStateRecord stored = Assert.Single(await LoadAsync(seed));
        Assert.Equal(three.Updated, stored);

        // A consumed chest cannot be regenerated with the same generation, from "no state", or with items already taken.
        Assert.Equal(LootCommitResult.InvalidTransition, await CommitAsync(seed, Generation(stored with { Consumed = false })));
        LootStateRecord next = generated with { Generation = 2 };
        Assert.Equal(LootCommitResult.InvalidTransition, await CommitAsync(seed, new LootCommitRequest(Guid.NewGuid(), stored.Key, null, next, [], [])));
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, new LootCommitRequest(Guid.NewGuid(), stored.Key, stored, next, [], [])));
        Assert.Equal(next, Assert.Single(await LoadAsync(seed)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LoadInstanceStates_PurgesRowsOfMissingInstances_AndKeepsTheRest(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(Instance + 1, 36, 1_700_000_000));
        }

        LootStateRecord kept = Generated(seed);
        LootStateRecord orphaned = Generated(seed) with { Key = new LootStateKey(Instance + 1, Chest) };
        await CommitAsync(seed, Generation(kept));
        await CommitAsync(seed, Generation(orphaned));

        // The instance row goes without its delete cleaning the chest (a lost delete, or one that raced a commit).
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await db.Set<InstanceRow>().Where(i => i.Id == (int)(Instance + 1)).ExecuteDeleteAsync();
            Assert.Equal(2, await db.Set<LootStateRow>().CountAsync());
        }

        Assert.Equal(kept, Assert.Single(await LoadAsync(seed)));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(1, await verify.Set<LootStateRow>().CountAsync());
        Assert.DoesNotContain(await verify.Set<LootStateItemRow>().ToListAsync(), r => r.InstanceId == (int)(Instance + 1));
        Assert.DoesNotContain(await verify.Set<LootStatePlayerRow>().ToListAsync(), r => r.InstanceId == (int)(Instance + 1));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteInstance_AlsoRemovesItsChestLootState(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(Instance + 1, 36, 1_700_000_000));
        }

        LootStateRecord other = Generated(seed) with { Key = new LootStateKey(Instance + 1, Chest) };
        await CommitAsync(seed, Generation(Generated(seed)));
        await CommitAsync(seed, Generation(other));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await new EfInstanceStore(db).DeleteInstanceAsync(Instance);
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(other, Assert.Single(await new EfLootStateStore(verify).LoadInstanceStatesAsync()));
        Assert.DoesNotContain(await verify.Set<LootStateItemRow>().ToListAsync(), r => r.InstanceId == (int)Instance);
        Assert.DoesNotContain(await verify.Set<LootStatePlayerRow>().ToListAsync(), r => r.InstanceId == (int)Instance);
        Assert.Equal(LootCommitResult.ScopeMissing, await CommitAsync(seed, Generation(Generated(seed))));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletion_KeepsChestMarks_BecauseDroppingARecipientWouldOpenTheChestToEveryone(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        LootStateRecord generated = Generated(seed) with { Recipients = [seed.A.Id] };
        await CommitAsync(seed, Generation(generated));
        LootCommitRequest take = Take(seed, generated, slot: 2, newGuid: 102);
        Assert.Equal(LootCommitResult.Committed, await CommitAsync(seed, take));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.A.Id, accountId: 1));
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        LootStateRecord stored = Assert.Single(await new EfLootStateStore(verify).LoadInstanceStatesAsync());
        Assert.Equal(take.Updated, stored);
        Assert.Equal([seed.A.Id], stored.Recipients);
        Assert.Equal([seed.A.Id], stored.Items.Single(i => i.Slot == 2).LootedBy);
        Assert.Contains(typeof(LootStateDataModule), CharacterDataCleanups.All.Select(c => c.GetType()));
    }

    // --- fixtures ----------------------------------------------------------------------------

    /// <summary>A shared stack, a per-player stack and a quest stack for the first character.</summary>
    private static LootStateRecord Generated(Seed seed) => new(
        new LootStateKey(Instance, Chest), SourceEntry: 3000, LootOwnerCharacterId: 0, Generation: 1, Consumed: false, RespawnAtUnix: 0,
        Recipients: [seed.A.Id, seed.B.Id],
        Items:
        [
            new LootStateItem(0, 117, 2, IsQuest: false, IsPerPlayer: false, IsLooted: false, [], []),
            new LootStateItem(1, 118, 1, IsQuest: false, IsPerPlayer: true, IsLooted: false, [], []),
            new LootStateItem(2, 5000, 1, IsQuest: true, IsPerPlayer: false, IsLooted: false, [seed.A.Id], []),
        ]);

    private static LootCommitRequest Generation(LootStateRecord record)
        => new(Guid.NewGuid(), record.Key, null, record, [], []);

    /// <summary>The first character takes <paramref name="slot"/> of <paramref name="from"/> into a new stack.</summary>
    private static LootCommitRequest Take(Seed seed, LootStateRecord from, byte slot, uint newGuid)
    {
        LootStateItem item = from.Items.Single(i => i.Slot == slot);
        var award = new LootAward(seed.A.Id, slot, item.ItemId, item.Count);
        LootStateRecord updated = LootStateRules.Replay(from, from.Recipients, from.LootOwnerCharacterId, [award], Respawn)
            ?? throw new InvalidOperationException("the take is not legal");
        CharacterState after = seed.A with { Inventory = Add(seed.A.Inventory!, Item(newGuid, item.ItemId, item.Count)) };
        return new LootCommitRequest(Guid.NewGuid(), from.Key, from, updated, [new EconomyParticipant(seed.A, after)], [award]);
    }

    private static InventorySnapshot Add(InventorySnapshot inventory, ItemInstanceData item)
        => new([.. inventory.Items, new InventoryItemData(0, (byte)(30 + (item.Guid % 20)), item)]);

    private async Task<Seed> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord a = await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Looter", Race = 1, Class = 1, Level = 10 });
        CharacterRecord b = await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Bystander", Race = 1, Class = 1, Level = 10 });
        CharacterState aState = State(a.Id, 1000, [new(0, 23, Item(100, 117, 4)), new(0, 24, Item(101, 25, 1))]);
        CharacterState bState = State(b.Id, 500, [new(0, 23, Item(200, 118, 2))]);
        await characters.SaveStateAsync(aState);
        await characters.SaveStateAsync(bState);
        await new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(Instance, 36, 1_700_000_000));
        return new Seed(connection, aState, bState);
    }

    private static CharacterState State(int id, uint money, InventoryItemData[] items)
        => new(id, 0, 12, 1, 2, 3, 0, 10, 50, Money: money, ActionButtons: [], Home: new(0, 12, 1, 2, 3), Inventory: new InventorySnapshot(items));

    private static ItemInstanceData Item(uint guid, uint entry, uint count) => new()
    {
        Guid = guid, Entry = entry, Count = count, Durability = 17, Creator = 7, Flags = 0,
        Charges = [-1, 0, 0, 0, 2], Enchantments = Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(),
    };

    private static async Task<LootCommitResult> CommitAsync(Seed seed, LootCommitRequest request)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        LootCommitResult result = await new EfLootStateStore(db).CommitAsync(request);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        return result;
    }

    private static async Task<IReadOnlyList<LootStateRecord>> LoadAsync(Seed seed)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        return await new EfLootStateStore(db).LoadInstanceStatesAsync();
    }

    private static async Task AssertInventoryAsync(Seed seed, int characterId, CharacterState expected)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(expected.Money, (await new EfCharacterStore(db).GetByIdAsync(characterId))!.Money);
        Assert.True(EconomyRequestValidation.SameInventory(expected.Inventory!.Items, await new EfItemStore(db).GetInventoryAsync(characterId)));
    }

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterState A, CharacterState B);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();
}
