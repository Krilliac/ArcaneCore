using System.Data.Common;
using System.Transactions;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Ordinary nonrepeatable quest rewards use the existing disposable provider matrix.</summary>
public sealed class QuestRewardStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> Interruptions()
    {
        foreach (object[] provider in Providers())
        {
            yield return [provider[0], false];
            yield return [provider[0], true];
        }
    }

    public static IEnumerable<object[]> Preconditions()
    {
        foreach (object[] provider in Providers())
        {
            foreach (string conflict in new[] { "quest", "money", "count", "charges", "enchantments", "position" })
            {
                yield return [provider[0], conflict];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_RelogPreservesAllRewards_AndDuplicateCannotGrantAgain(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));
        await AssertPersistedAsync(seed, rewarded: true);

        // Duplicate detection precedes the now-stale preconditions. Even a different
        // proposed amount must leave the first durable reward intact.
        CharacterQuestRewardRequest duplicate = seed.Request with
        {
            After = seed.Request.After with { Money = 999_999 },
        };
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, duplicate));
        await AssertPersistedAsync(seed, rewarded: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OlderUnrewardedQuestDelta_CannotReopenRewardedHistory(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            CharacterQuestStatus old = seed.Request.ExpectedQuest with
            {
                Status = 3, MobCount1 = 0, ItemCount1 = 0, RewardChoice = 0,
            };
            await new EfCharacterQuestStore(db).SaveQuestsAsync(old.CharacterId, [old]);
        }

        await AssertPersistedAsync(seed, rewarded: true);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, seed.Request));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QuestSaveAlreadyHoldingOlderRow_CannotOverwriteAnInterleavedReward(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        var interceptor = new BeforeQuestSave(async () =>
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));
        });
        await using (CharacterDbContext db = Context(seed.Connection, interceptor))
        {
            // SavingChanges runs after SaveQuestsAsync has loaded the old row and
            // staged these counters, but before it issues its UPDATE statement.
            CharacterQuestStatus old = seed.Request.ExpectedQuest with { Status = 3, MobCount1 = 0 };
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new EfCharacterQuestStore(db)
                .SaveQuestsAsync(old.CharacterId, [old]));
            Assert.True(interceptor.Fired);
        }

        await AssertPersistedAsync(seed, rewarded: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerMutableSnapshotLists_AreDetachedBeforeDatabaseWork(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        InventoryItemData[] inventory = seed.Request.After.Inventory!.Items.ToArray();
        int[] charges = inventory[0].Item.Charges.ToArray();
        uint[] enchantments = inventory[0].Item.Enchantments.ToArray();
        inventory[0] = inventory[0] with { Item = inventory[0].Item with { Charges = charges, Enchantments = enchantments } };
        ActionButton[] buttons = seed.Request.After.ActionButtons!.ToArray();
        CharacterQuestRewardRequest request = seed.Request with
        {
            After = seed.Request.After with { Inventory = new InventorySnapshot(inventory), ActionButtons = buttons },
        };
        var interceptor = new BeforeFirstRead(() =>
        {
            // The first SELECT follows the store's synchronous snapshot capture,
            // and precedes any state or inventory staging.
            charges[0] = 888;
            enchantments[0] = 999;
            inventory[0] = inventory[0] with { Item = inventory[0].Item with { Count = 777 } };
            buttons[0] = new ActionButton(0, 999, 0);
        });
        await using (CharacterDbContext db = Context(seed.Connection, interceptor))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(request));
            Assert.True(interceptor.Fired);
        }

        await AssertPersistedAsync(seed, rewarded: true);
    }

    [Theory]
    [MemberData(nameof(Preconditions))]
    public async Task StaleQuestMoneyOrDeepInventory_PreventsEveryReward(DatabaseProvider provider, string conflict)
    {
        Seed seed = await CreateAsync(provider);
        CharacterQuestRewardRequest request = seed.Request;
        if (conflict == "quest")
        {
            CharacterQuestStatus expected = request.ExpectedQuest with { MobCount4 = 99 };
            request = request with
            {
                ExpectedQuest = expected,
                RewardedQuest = request.RewardedQuest with { MobCount4 = 99 },
            };
        }
        else if (conflict == "money")
        {
            request = request with { Before = request.Before with { Money = request.Before.Money + 1 } };
        }
        else
        {
            InventoryItemData[] items = request.Before.Inventory!.Items.ToArray();
            InventoryItemData first = items[0];
            items[0] = conflict switch
            {
                "count" => first with { Item = first.Item with { Count = first.Item.Count + 1 } },
                "charges" => first with { Item = first.Item with { Charges = [9, 0, 0, 0, 0] } },
                "enchantments" => first with { Item = first.Item with { Enchantments = [99, 0, 0] } },
                "position" => first with { Slot = 25 },
                _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
            };
            request = request with { Before = request.Before with { Inventory = new InventorySnapshot(items) } };
        }

        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(seed.Connection, request));
        await AssertPersistedAsync(seed, rewarded: false);
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task InterruptionAfterSaveChanges_RollsBackAllWrites_AndAllowsRetry(DatabaseProvider provider, bool cancel)
    {
        Seed seed = await CreateAsync(provider);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new AfterRewardSave(() =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new IOException("Synthetic interruption after reward rows were saved.");
            }
        });
        await using (CharacterDbContext db = Context(seed.Connection, interceptor))
        {
            ICharacterQuestRewardStore store = new EfCharacterQuestRewardStore(db);
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(seed.Request, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(seed.Request));
            }

            Assert.True(interceptor.Fired);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(0, await db.SaveChangesAsync());
        }

        await AssertPersistedAsync(seed, rewarded: false);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));
        await AssertPersistedAsync(seed, rewarded: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RewardGuidCollision_CannotTakeAnotherCharactersItem(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        InventorySnapshot otherInventory = new([new(0, 23, Item(102, 118, 7))]);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            await new EfItemStore(db).SaveInventoryAsync(seed.OtherId, otherInventory);
        }

        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(seed.Connection, seed.Request));
        await AssertPersistedAsync(seed, rewarded: false);
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        AssertInventory(otherInventory.Items, await new EfItemStore(verify).GetInventoryAsync(seed.OtherId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletedCharacter_CannotReceiveQueuedReward(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(seed.Request.Before.Id, 77));
        }

        Assert.Equal(QuestRewardCommitResult.CharacterMissing, await CommitAsync(seed.Connection, seed.Request));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Empty(await new EfItemStore(verify).GetInventoryAsync(seed.Request.Before.Id));
        Assert.False((await new EfCharacterQuestStore(verify).LoadAsync(seed.Request.Before.Id)).Quests.Single().Rewarded);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerTrackedOrTransactionalWork_IsRejectedWithoutSavingOrDetachingIt(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        CharacterRecord character = await db.Characters.SingleAsync(c => c.Id == seed.Request.Before.Id);
        character.Money = 999;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
        Assert.Same(character, Assert.Single(db.ChangeTracker.Entries()).Entity);
        db.ChangeTracker.Clear();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
        }

        await AssertPersistedAsync(seed, rewarded: false);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<Seed> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = 77, Name = "Rewarder", Race = 1, Class = 1, Level = 10,
        });
        CharacterRecord other = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = 78, Name = "Observer", Race = 1, Class = 1, Level = 10,
        });
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50,
            Money: 100, ActionButtons: [new(0, 117, 0)], Home: new(0, 12, 1, 2, 3),
            Inventory: new InventorySnapshot([new(0, 23, Item(100, 117, 4)), new(0, 24, Item(101, 25, 1))]));
        await characters.SaveStateAsync(before);
        var expected = new CharacterQuestStatus(character.Id, 900001, 1, false, true, 1_900_000_000,
            1, 2, 3, 4, 4, 5, 6, 7, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected]);
        CharacterState after = before with
        {
            X = 16, PlayedTime = 60, Money = 175, ActionBarToggles = 2,
            ActionButtons = [new(0, 118, 0)], Home = new(1, 99, 4, 5, 6),
            Inventory = new InventorySnapshot([
                new(0, 23, Item(100, 117, 2)), new(0, 24, Item(101, 25, 1)),
                new(0, 25, Item(102, 118, 3)), new(0, 26, Item(103, 25, 1)),
            ]),
        };
        var request = new CharacterQuestRewardRequest(before, after, expected,
            expected with { Rewarded = true, Timer = 0, RewardChoice = 25 });
        return new Seed(connection, request, other.Id);
    }

    private static async Task<QuestRewardCommitResult> CommitAsync(
        DatabaseConnectionOptions connection, CharacterQuestRewardRequest request)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        QuestRewardCommitResult result = await new EfCharacterQuestRewardStore(db).CommitAsync(request);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        return result;
    }

    private static async Task AssertPersistedAsync(Seed seed, bool rewarded)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        CharacterState expected = rewarded ? seed.Request.After : seed.Request.Before;
        CharacterRecord? character = await new EfCharacterStore(db).GetByIdAsync(expected.Id);
        Assert.NotNull(character);
        Assert.Equal((expected.MapId, expected.ZoneId, expected.X, expected.Y, expected.Z, expected.Orientation,
                expected.Level, expected.PlayedTime, expected.LevelPlayedTime, expected.Money, expected.ActionBarToggles),
            (character.MapId, character.ZoneId, character.X, character.Y, character.Z, character.Orientation,
                character.Level, character.PlayedTime, character.LevelPlayedTime, character.Money, character.ActionBarToggles));
        Assert.Equal(expected.Home, new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ));
        Assert.Equal(expected.ActionButtons, await new EfCharacterStore(db).GetActionButtonsAsync(expected.Id));
        AssertInventory(expected.Inventory!.Items, await new EfItemStore(db).GetInventoryAsync(expected.Id));
        CharacterQuestStatus expectedQuest = rewarded ? seed.Request.RewardedQuest : seed.Request.ExpectedQuest;
        Assert.Equal(expectedQuest, Assert.Single((await new EfCharacterQuestStore(db).LoadAsync(expected.Id)).Quests));
        Assert.Equal(0u, (await new EfCharacterStore(db).GetByIdAsync(seed.OtherId))!.Money);
    }

    private static void AssertInventory(IReadOnlyList<InventoryItemData> expected, IReadOnlyList<InventoryItemData> actual)
    {
        InventoryItemData[] left = expected.OrderBy(i => i.Item.Guid).ToArray();
        InventoryItemData[] right = actual.OrderBy(i => i.Item.Guid).ToArray();
        Assert.Equal(left.Length, right.Length);
        for (int i = 0; i < left.Length; i++)
        {
            Assert.Equal((left[i].ContainerGuid, left[i].Slot), (right[i].ContainerGuid, right[i].Slot));
            Assert.Equal(left[i].Item with { Charges = right[i].Item.Charges, Enchantments = right[i].Item.Enchantments }, right[i].Item);
            Assert.Equal(left[i].Item.Charges, right[i].Item.Charges);
            Assert.Equal(left[i].Item.Enchantments, right[i].Item.Enchantments);
        }
    }

    private static ItemInstanceData Item(uint guid, uint entry, uint count) => new()
    {
        Guid = guid, Entry = entry, Count = count, Durability = 17, Creator = 7, Flags = 1,
        Charges = [-1, 0, 0, 0, 2], Enchantments = Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(),
    };

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterQuestRewardRequest Request, int OtherId);

    private sealed class BeforeQuestSave(Func<Task> action) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await action();
            }

            return result;
        }
    }

    private sealed class BeforeFirstRead(Action action) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                action();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class AfterRewardSave(Action action) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context?.ChangeTracker.Entries<CharacterQuestStatusRow>().Any(e => e.Entity.Rewarded) == true)
            {
                Fired = true;
                action();
            }

            return ValueTask.FromResult(result);
        }
    }
}
