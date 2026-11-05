using System.Data.Common;
using System.Transactions;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
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
    public async Task QuestRowLoadedBeforeAReward_CannotOverwriteIt_ThroughTheRewardedConcurrencyToken(DatabaseProvider provider)
    {
        // The Rewarded column is a concurrency token (QuestNpcCharactersModule): a writer holding a row it loaded
        // before a durable reward must fail rather than reopen the history. The reward lands on a second
        // connection between this context's read and write, so nothing here touches the ownership fence and the
        // outcome is the same on every provider.
        Seed seed = await CreateAsync(provider);
        await using CharacterDbContext stale = TestContexts.Create<CharacterDbContext>(seed.Connection);
        CharacterQuestStatusRow loaded = await stale.Set<CharacterQuestStatusRow>()
            .SingleAsync(r => r.CharacterId == seed.Request.ExpectedQuest.CharacterId && r.Quest == seed.Request.ExpectedQuest.Quest);
        Assert.False(loaded.Rewarded);

        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));

        loaded.Status = 3;
        loaded.MobCount1 = 0;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());

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
        // The quest row went with the character (CharacterDeletionTests); the refused reward wrote none.
        Assert.Empty((await new EfCharacterQuestStore(verify).LoadAsync(seed.Request.Before.Id)).Quests);
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

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_PersistsLearnedSpellsAndReputationRowsAtomically(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        int id = seed.Request.Before.Id;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection))
        {
            // One learned spell is already stored (not duplicated), one faction already has a row (overwritten);
            // the other character holds look-alike rows that must stay untouched.
            db.Set<CharacterSpellRow>().AddRange(
                new CharacterSpellRow { CharacterId = id, Spell = 900 }, new CharacterSpellRow { CharacterId = seed.OtherId, Spell = 901 });
            db.Set<CharacterReputationEntity>().AddRange(
                new CharacterReputationEntity { CharacterId = id, Faction = 72, Standing = 5, Flags = 1 },
                new CharacterReputationEntity { CharacterId = seed.OtherId, Faction = 72, Standing = 7, Flags = 1 });
            await db.SaveChangesAsync();
        }

        CharacterQuestRewardRequest request = Granting(seed);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, request));
        await AssertPersistedAsync(seed, rewarded: true);
        await AssertGrantsAsync(seed, committed: true);

        // A retry observes the rewarded history before it could grant anything again.
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, request));
        await AssertGrantsAsync(seed, committed: true);
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task ConflictOrCancellation_RollsBackSpellAndReputationRows(DatabaseProvider provider, bool cancel)
    {
        Seed seed = await CreateAsync(provider);
        CharacterQuestRewardRequest request = Granting(seed);
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
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(request, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(request));
            }

            Assert.True(interceptor.Fired);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await AssertPersistedAsync(seed, rewarded: false);
        await AssertGrantsAsync(seed, committed: false);

        // A stale precondition refuses the whole reward, grants included.
        CharacterQuestRewardRequest stale = request with { Before = request.Before with { Money = request.Before.Money + 1 } };
        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(seed.Connection, stale));
        await AssertGrantsAsync(seed, committed: false);

        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, request));
        await AssertGrantsAsync(seed, committed: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Commit_DoesNotIssueIntermediateSaveChanges(DatabaseProvider provider)
    {
        // The module stores save and clear the tracker; the reward must stay one save in one transaction.
        Seed seed = await CreateAsync(provider);
        var counter = new SaveCounter();
        await using (CharacterDbContext db = Context(seed.Connection, counter))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(Granting(seed)));
        }

        Assert.Equal(1, counter.Saves);
        await AssertGrantsAsync(seed, committed: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidGrants_AreRejectedBeforeAnyWrite(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        int id = seed.Request.Before.Id;
        CharacterReputationRow Row(int character, uint faction) => new(character, faction, 1, 1);
        CharacterQuestRewardRequest[] invalid =
        [
            seed.Request with { LearnedSpells = [0u] },
            seed.Request with { LearnedSpells = [900u, 900u] },
            seed.Request with { ReputationAfter = [Row(seed.OtherId, 21)] },
            seed.Request with { ReputationAfter = [Row(id, 0)] },
            seed.Request with { ReputationAfter = [Row(id, 21), Row(id, 21)] },
        ];
        foreach (CharacterQuestRewardRequest request in invalid)
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
            await Assert.ThrowsAsync<ArgumentException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(request));
        }

        await AssertPersistedAsync(seed, rewarded: false);
        await AssertGrantsAsync(seed, committed: false);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GrantLists_AreDetachedBeforeDatabaseWork(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        uint[] spells = [900, 902];
        CharacterReputationRow[] factions = [.. Granting(seed).ReputationAfter!];
        var interceptor = new BeforeFirstRead(() =>
        {
            spells[1] = 12345;
            factions[0] = factions[0] with { Standing = 999_999 };
        });
        await using (CharacterDbContext db = Context(seed.Connection, interceptor))
        {
            CharacterQuestRewardRequest request = seed.Request with { LearnedSpells = spells, ReputationAfter = factions };
            // The first read happens after the store copied the lists: the committed rows are the originals.
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(request));
            Assert.True(interceptor.Fired);
        }

        await AssertGrantsAsync(seed, committed: true);
    }

    public static IEnumerable<object[]> RetryShapes()
    {
        foreach (object[] provider in Providers())
        {
            foreach (string shape in new[] { "begin", "save", "commit" })
            {
                yield return [provider[0], shape];
            }
        }
    }

    [Theory]
    [MemberData(nameof(RetryShapes))]
    public async Task SerializationFailureOnFirstAttempt_IsRetried_AndGrantsTheRewardExactlyOnce(DatabaseProvider provider, string shape)
    {
        Seed seed = await CreateAsync(provider);
        var fault = new InjectedFault(shape, failures: 1, () => new FakeDbException("40001"));
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        Assert.Equal(2, fault.Attempts);
        Assert.All(fault.Thrown, e => Assert.False(FaultLedger.WasRecorded(e)));
        await AssertPersistedAsync(seed, rewarded: true);
        // Money 100 -> 175 once: a second grant would have been refused as AlreadyRewarded or doubled.
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, seed.Request));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeadlockSqlState_IsRetriedThroughWrappers(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        var fault = new InjectedFault("save", failures: 2,
            () => new DbUpdateException("wrapped", new AggregateException(new FakeDbException("40P01"))));
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
        }

        Assert.Equal(3, fault.Attempts);
        await AssertPersistedAsync(seed, rewarded: true);
    }

    [Theory]
    [MemberData(nameof(RetryShapes))]
    public async Task ExhaustedRetries_ThrowTheLastError_RecordOneFault_AndChangeNothing(DatabaseProvider provider, string shape)
    {
        Seed seed = await CreateAsync(provider);
        var fault = new InjectedFault(shape, failures: int.MaxValue, () => new FakeDbException("40001"));
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            FakeDbException thrown = await Assert.ThrowsAsync<FakeDbException>(
                () => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
            Assert.Same(fault.Thrown[^1], thrown);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        Assert.Equal(4, fault.Attempts);
        Assert.Single(fault.Thrown, e => FaultLedger.WasRecorded(e));
        Assert.True(FaultLedger.WasRecorded(fault.Thrown[^1]));
        await AssertPersistedAsync(seed, rewarded: false);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(seed.Connection, seed.Request));
    }

    [Theory]
    [MemberData(nameof(RetryShapes))]
    public async Task NonRetryableFailure_IsNotRetried(DatabaseProvider provider, string shape)
    {
        Seed seed = await CreateAsync(provider);
        // A unique violation (23505) and a plain exception are both outside the retry set.
        var fault = new InjectedFault(shape, failures: int.MaxValue, () => new FakeDbException("23505"));
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            await Assert.ThrowsAsync<FakeDbException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
        }

        Assert.Equal(1, fault.Attempts);
        Assert.True(FaultLedger.WasRecorded(fault.Thrown[0]));
        await AssertPersistedAsync(seed, rewarded: false);

        var plain = new InjectedFault(shape, failures: int.MaxValue, () => new InvalidOperationException("not a serialization failure"));
        await using CharacterDbContext second = Context(seed.Connection, plain.Interceptors);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EfCharacterQuestRewardStore(second).CommitAsync(seed.Request));
        Assert.Equal(1, plain.Attempts);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancelledCaller_IsNotRetried(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        using var cts = new CancellationTokenSource();
        var fault = new InjectedFault("save", failures: int.MaxValue, () => new FakeDbException("40001"));
        fault.OnThrow = cts.Cancel;
        await using CharacterDbContext db = Context(seed.Connection, fault.Interceptors);
        await Assert.ThrowsAsync<FakeDbException>(() => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request, cts.Token));
        Assert.Equal(1, fault.Attempts);
        await AssertPersistedAsync(seed, rewarded: false);
    }

    [Fact]
    public async Task Rollback_OfACompletedTransaction_IsTolerated_OnlyAfterAServerError()
    {
        // The driver's "transaction has completed" state is an InvalidOperationException; no message text is read.
        var completed = new InvalidOperationException("driver wording is irrelevant");
        var serverError = new FakeDbException("40001");
        await StoreTransactionRollback.RollbackAfterFailureAsync(new RollbackStub(completed), serverError, "failed",
            _ => Assert.Fail("a completed transaction is not a rollback fault"));
        // A server error wrapped by EF (DbUpdateException) counts too.
        await StoreTransactionRollback.RollbackAfterFailureAsync(new RollbackStub(completed),
            new DbUpdateException("save", serverError), "failed", _ => Assert.Fail("tolerated"));

        // No server answer (a lost connection surfaces as something else): the rollback failure is real.
        foreach (Exception commitError in new Exception[] { new IOException("connection lost"), new TimeoutException(), new FakeDbException("") })
        {
            Exception? reported = null;
            AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
                StoreTransactionRollback.RollbackAfterFailureAsync(new RollbackStub(completed), commitError, "failed", e => reported = e));
            Assert.Same(completed, reported);
            Assert.Equal([commitError, completed], aggregate.InnerExceptions);
        }

        // A server error with a rollback failure that is not the completed state (or is a disposed object) still surfaces.
        foreach (Exception rollbackError in new Exception[] { new IOException("reset"), new ObjectDisposedException("tx"), new FakeDbException("08006") })
        {
            Exception? reported = null;
            AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
                StoreTransactionRollback.RollbackAfterFailureAsync(new RollbackStub(rollbackError), serverError, "failed", e => reported = e));
            Assert.Same(rollbackError, reported);
            Assert.Equal([serverError, rollbackError], aggregate.InnerExceptions);
        }

        Assert.True(EfCharacterQuestRewardStore.IsSerializationFailure(new AggregateException(new IOException(), serverError)));
        Assert.False(EfCharacterQuestRewardStore.IsSerializationFailure(new FakeDbException("23505")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ProviderTransactionEndedByTheServer_ThenCommitFails40001_IsRetried_AndGrantsOnce(DatabaseProvider provider)
    {
        // The injected step ends the real provider transaction before throwing, so EF's rollback meets the driver's own "completed" error.
        Seed seed = await CreateAsync(provider);
        var fault = new InjectedFault("commit", failures: 1, () => new FakeDbException("40001")) { EndTransactionFirst = true };
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        Assert.Equal(2, fault.Attempts);
        Assert.False(FaultLedger.WasRecorded(fault.Thrown[0]));
        await AssertPersistedAsync(seed, rewarded: true);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, seed.Request));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedRollbackAfter40001_IsNotRetried_AndSurfacesTheAggregate(DatabaseProvider provider)
    {
        Seed seed = await CreateAsync(provider);
        var lost = new IOException("connection lost during rollback");
        var fault = new InjectedFault("commit", failures: int.MaxValue, () => new FakeDbException("40001")) { RollbackFailure = () => lost };
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(
                () => new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request));
            Assert.Same(fault.Thrown[0], aggregate.InnerExceptions[0]);
            Assert.Same(lost, aggregate.InnerExceptions[1]);
        }

        Assert.Equal(1, fault.Attempts);
        Assert.True(FaultLedger.WasRecorded(fault.Thrown[0]));
        await AssertPersistedAsync(seed, rewarded: false);
    }

    [Theory]
    [MemberData(nameof(RetryShapes))]
    public async Task RetryThroughAGrantingRequest_WritesSpellsAndReputationOnce(DatabaseProvider provider, string shape)
    {
        Seed seed = await CreateAsync(provider);
        var fault = new InjectedFault(shape, failures: 1, () => new FakeDbException("40001"));
        await using (CharacterDbContext db = Context(seed.Connection, fault.Interceptors))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db).CommitAsync(Granting(seed)));
        }

        Assert.Equal(2, fault.Attempts);
        await AssertPersistedAsync(seed, rewarded: true);
        await AssertGrantsAsync(seed, committed: true);
    }

    [Upgrade.PostgresFact]
    public async Task RealPostgreSqlSerializationFailure_IsRetried_AndGrantsExactlyOnce()
    {
        Seed seed = await CreateAsync(DatabaseProvider.PostgreSql);
        int id = seed.Request.Before.Id;
        // Mid-attempt, a rival Serializable transaction reads the character row (which the reward writes) and inserts an
        // unrelated quest row (the reward reads that table), then commits: a read/write dependency cycle the server resolves
        // by failing the reward's transaction with 40001. The rival touches nothing the reward checks, so the retry passes.
        var rival = new RivalOnFirstSave(async () =>
        {
            await using CharacterDbContext other = TestContexts.Create<CharacterDbContext>(seed.Connection);
            await using var tx = await other.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            _ = await other.Characters.AsNoTracking().FirstAsync(c => c.Id == id);
            other.Set<CharacterQuestStatusRow>().Add(new CharacterQuestStatusRow { CharacterId = id, Quest = 900002 });
            await other.SaveChangesAsync();
            await tx.CommitAsync();
        });
        var log = new CapturingLogger();
        await using (CharacterDbContext db = Context(seed.Connection, rival))
        {
            Assert.Equal(QuestRewardCommitResult.Committed, await new EfCharacterQuestRewardStore(db, log).CommitAsync(seed.Request));
        }

        Assert.True(rival.Fired);
        DbException serialization = Assert.IsAssignableFrom<DbException>(Assert.Single(log.Errors).GetBaseException());
        Assert.Equal("40001", serialization.SqlState);
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(seed.Connection);
        Assert.Equal(175u, (await new EfCharacterStore(verify).GetByIdAsync(id))!.Money);
        Assert.True((await verify.Set<CharacterQuestStatusRow>().AsNoTracking().SingleAsync(r => r.CharacterId == id && r.Quest == 900001)).Rewarded);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(seed.Connection, seed.Request));
    }

    private sealed class RivalOnFirstSave(Func<Task> rival) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await rival().ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<EfCharacterQuestRewardStore>
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }

    private sealed class RollbackStub(Exception toThrow) : Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();
        public void Commit() { }
        public void Rollback() => throw toThrow;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw toThrow;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptors).Options);

    private static CharacterQuestRewardRequest Granting(Seed seed) => seed.Request with
    {
        LearnedSpells = [900u, 902u],
        ReputationAfter = [new(seed.Request.Before.Id, 21, 150, 1), new(seed.Request.Before.Id, 72, -20, 17)],
    };

    /// <summary>Spell 900 may pre-exist, 901 belongs to the other character; faction 72 may pre-exist for both characters.</summary>
    private static async Task AssertGrantsAsync(Seed seed, bool committed)
    {
        int id = seed.Request.Before.Id;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        List<uint> own = await db.Set<CharacterSpellRow>().AsNoTracking().Where(r => r.CharacterId == id)
            .Select(r => r.Spell).OrderBy(s => s).ToListAsync();
        List<CharacterReputationEntity> factions = await db.Set<CharacterReputationEntity>().AsNoTracking()
            .Where(r => r.CharacterId == id).OrderBy(r => r.Faction).ToListAsync();
        if (committed)
        {
            Assert.Contains(900u, own);
            Assert.Contains(902u, own);
            Assert.DoesNotContain(12345u, own);
            Assert.Equal(own.Distinct().Count(), own.Count);
            Assert.Equal((150, 1u), factions.Where(f => f.Faction == 21).Select(f => (f.Standing, f.Flags)).Single());
            Assert.Equal((-20, 17u), factions.Where(f => f.Faction == 72).Select(f => (f.Standing, f.Flags)).Single());
        }
        else
        {
            Assert.DoesNotContain(902u, own);
            Assert.DoesNotContain(factions, f => f.Faction == 21);
            Assert.DoesNotContain(factions, f => f.Faction == 72 && f.Standing == -20);
        }

        // The other character's rows are never touched by this reward.
        Assert.DoesNotContain(902u, await db.Set<CharacterSpellRow>().AsNoTracking().Where(r => r.CharacterId == seed.OtherId)
            .Select(r => r.Spell).ToListAsync());
        Assert.DoesNotContain(await db.Set<CharacterReputationEntity>().AsNoTracking().Where(r => r.CharacterId == seed.OtherId)
            .ToListAsync(), r => r.Faction == 72 && r.Standing == -20);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return ValueTask.FromResult(result);
        }
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
