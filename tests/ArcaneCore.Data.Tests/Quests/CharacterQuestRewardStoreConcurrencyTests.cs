using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Quests;

/// <summary>
/// File-backed SQLite coverage for the process-local reward writer coordinator. These tests
/// deliberately use the public store contract: the coordinator is an implementation detail,
/// while atomic commit, cancellation, and file isolation are observable guarantees.
/// </summary>
public sealed class CharacterQuestRewardStoreConcurrencyTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task ConcurrentRewardsInOneFile_AreSerializedAndBothRemainDurable()
    {
        DatabaseConnectionOptions connection = await CreateSqliteAsync();
        Seed first = await SeedAsync(connection, "FirstRewarder", 11);
        Seed second = await SeedAsync(connection, "SecondRewarder", 22);

        Task<QuestRewardCommitResult> firstCommit;
        Task<QuestRewardCommitResult> secondCommit;
        await using CharacterDbContext holder = TestContexts.Create<CharacterDbContext>(connection);
        await using (SqliteRewardWriterCoordinator.Lease held =
            await SqliteRewardWriterCoordinator.AcquireAsync(holder, CancellationToken.None))
        {
            firstCommit = CommitAsync(connection, first.Request);
            secondCommit = CommitAsync(connection, second.Request);
            await Task.Delay(25);
            Assert.False(firstCommit.IsCompleted);
            Assert.False(secondCommit.IsCompleted);
        }

        QuestRewardCommitResult[] results = await Task.WhenAll(firstCommit, secondCommit);

        Assert.All(results, result => Assert.Equal(QuestRewardCommitResult.Committed, result));
        await AssertRewardedAsync(connection, first);
        await AssertRewardedAsync(connection, second);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, first.Request));
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, second.Request));
    }

    [Fact]
    public async Task CancellationBeforeCommit_DoesNotLeaveWriterLeaseOrPartialReward()
    {
        DatabaseConnectionOptions connection = await CreateSqliteAsync();
        Seed seed = await SeedAsync(connection, "CancelledRewarder", 31);
        await using CharacterDbContext holder = TestContexts.Create<CharacterDbContext>(connection);
        await using (SqliteRewardWriterCoordinator.Lease held =
            await SqliteRewardWriterCoordinator.AcquireAsync(holder, CancellationToken.None))
        {
            using var cancelled = new CancellationTokenSource();
            Task<QuestRewardCommitResult> pending = CommitAsync(connection, seed.Request, cancelled.Token);
            await Task.Delay(25);
            Assert.False(pending.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }

        await AssertUnrewardedAsync(connection, seed);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, seed.Request));
        await AssertRewardedAsync(connection, seed);
    }

    [Fact]
    public async Task CancelledWriterWait_DoesNotRetainFileLease()
    {
        DatabaseConnectionOptions connection = await CreateSqliteAsync();
        await using CharacterDbContext holder = TestContexts.Create<CharacterDbContext>(connection);
        await using CharacterDbContext waiterDb = TestContexts.Create<CharacterDbContext>(connection);
        await using (SqliteRewardWriterCoordinator.Lease held =
            await SqliteRewardWriterCoordinator.AcquireAsync(holder, CancellationToken.None))
        {
            using var cancelled = new CancellationTokenSource();
            Task<SqliteRewardWriterCoordinator.Lease> waiting =
                SqliteRewardWriterCoordinator.AcquireAsync(waiterDb, cancelled.Token).AsTask();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        }

        await using SqliteRewardWriterCoordinator.Lease recovered =
            await SqliteRewardWriterCoordinator.AcquireAsync(waiterDb, CancellationToken.None);
    }

    [Fact]
    public async Task EqualCharacterIdsInDifferentFiles_DoNotShareWriterLeaseOrState()
    {
        DatabaseConnectionOptions firstConnection = await CreateSqliteAsync();
        DatabaseConnectionOptions secondConnection = await CreateSqliteAsync();
        Seed first = await SeedAsync(firstConnection, "FirstFileRewarder", 41);
        Seed second = await SeedAsync(secondConnection, "SecondFileRewarder", 42);
        Assert.Equal(first.Request.Before.Id, second.Request.Before.Id);

        Task<QuestRewardCommitResult> firstCommit;
        await using CharacterDbContext holder = TestContexts.Create<CharacterDbContext>(firstConnection);
        await using (SqliteRewardWriterCoordinator.Lease held =
            await SqliteRewardWriterCoordinator.AcquireAsync(holder, CancellationToken.None))
        {
            firstCommit = CommitAsync(firstConnection, first.Request);
            Task<QuestRewardCommitResult> secondCommit = CommitAsync(secondConnection, second.Request);
            Assert.False(firstCommit.IsCompleted);
            Assert.Equal(QuestRewardCommitResult.Committed, await secondCommit.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(firstCommit.IsCompleted);
        }

        Assert.Equal(QuestRewardCommitResult.Committed, await firstCommit);
        await AssertRewardedAsync(firstConnection, first);
        await AssertRewardedAsync(secondConnection, second);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<DatabaseConnectionOptions> CreateSqliteAsync()
    {
        DatabaseConnectionOptions created = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var connection = new DatabaseConnectionOptions
        {
            Provider = created.Provider,
            ConnectionString = created.ConnectionString + ";Default Timeout=1;Pooling=False",
        };
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return connection;
    }

    private static async Task<Seed> SeedAsync(DatabaseConnectionOptions connection, string name, uint quest)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
        {
            AccountId = 1000 + (int)quest, Name = name, Race = 1, Class = 1, Level = 10,
        });
        CharacterState before = new(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, Money: 100,
            Inventory: new InventorySnapshot([]));
        await new EfCharacterStore(db).SaveStateAsync(before);
        CharacterQuestStatus expected = new(character.Id, quest, 1, false, true, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected]);
        CharacterState after = before with { Money = 150, PlayedTime = 51 };
        CharacterQuestStatus rewarded = expected with { Rewarded = true };
        return new Seed(connection, new CharacterQuestRewardRequest(before, after, expected, rewarded));
    }

    private static async Task<QuestRewardCommitResult> CommitAsync(
        DatabaseConnectionOptions connection, CharacterQuestRewardRequest request,
        CancellationToken cancellationToken = default)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        QuestRewardCommitResult result = await new EfCharacterQuestRewardStore(db).CommitAsync(request, cancellationToken);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        return result;
    }

    private static async Task AssertRewardedAsync(DatabaseConnectionOptions connection, Seed seed)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord? character = await new EfCharacterStore(db).GetByIdAsync(seed.Request.Before.Id);
        Assert.NotNull(character);
        Assert.Equal(seed.Request.After.Money, character!.Money);
        Assert.Equal(seed.Request.After.PlayedTime, character.PlayedTime);
        Assert.Equal(seed.Request.RewardedQuest, Assert.Single((await new EfCharacterQuestStore(db)
            .LoadAsync(seed.Request.Before.Id)).Quests));
    }

    private static async Task AssertUnrewardedAsync(DatabaseConnectionOptions connection, Seed seed)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord? character = await new EfCharacterStore(db).GetByIdAsync(seed.Request.Before.Id);
        Assert.NotNull(character);
        Assert.Equal(seed.Request.Before.Money, character!.Money);
        Assert.Equal(seed.Request.ExpectedQuest, Assert.Single((await new EfCharacterQuestStore(db)
            .LoadAsync(seed.Request.Before.Id)).Quests));
    }

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterQuestRewardRequest Request);
}
