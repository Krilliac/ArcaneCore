using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// vmangos QUEST_METHOD_AUTOCOMPLETE turn-in (Player.cpp:12682-12700, RewardQuest 13081-13250): a quest that was
/// never in the journal is rewarded in the same serializable transaction that writes money and inventory.
/// Run on every provider the machine offers; MariaDB and PostgreSQL run only on hosted CI (this machine has
/// SQLite only), where the insert-if-missing read takes next-key locks (MariaDB) or may raise a serialization
/// failure (PostgreSQL) that the caller's retry handles.
/// </summary>
public sealed class QuestAutoCompleteRewardStoreTests : IAsyncLifetime
{
    private const uint Quest = 900070;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NoRow_InsertsTheRewardedRow_AndCommitsMoneyAndInventory(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NonRepeatable_SecondSettlement_IsAlreadyRewarded_AndNeverDuplicatesTheRow(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, request));
        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Repeatable_WritesStatusNone_AndMayBeRewardedAgainFromTheHistoryRow(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest first) = await CreateAsync(provider, nonrepeatable: false);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, first));
        await AssertPersistedAsync(connection, first.After, first.RewardedQuest);

        // The second hand-in starts from the rewarded history the first one left (status NONE, rewarded).
        CharacterState after = first.After with { Money = first.After.Money + 10 };
        CharacterQuestStatus virtualRow = first.RewardedQuest with { Status = 1, Timer = 0 };
        var second = new CharacterQuestRewardRequest(first.After, after, virtualRow, first.RewardedQuest, InsertIfMissing: true);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, second));
        await AssertPersistedAsync(connection, after, second.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OrdinaryPath_StillRefusesAMissingRow(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(connection, request with { InsertIfMissing = false }));
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Empty((await new EfCharacterQuestStore(db).LoadAsync(request.Before.Id)).Quests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ChangedMoney_IsAConflict_AndInsertsNothing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(connection,
            request with { Before = request.Before with { Money = request.Before.Money + 1 } }));
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Empty((await new EfCharacterQuestStore(db).LoadAsync(request.Before.Id)).Quests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExpectedWithRewardedHistory_ButNoRow_IsAConflict(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: false);
        // A repeat claim whose history row vanished must not invent history.
        CharacterQuestRewardRequest forged = request with { ExpectedQuest = request.ExpectedQuest with { Rewarded = true } };
        Assert.Equal(QuestRewardCommitResult.Conflict, await CommitAsync(connection, forged));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletedCharacter_ReturnsCharacterMissing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        int ghost = request.Before.Id + 999;
        Assert.Equal(QuestRewardCommitResult.CharacterMissing, await CommitAsync(connection,
            request with
            {
                Before = request.Before with { Id = ghost },
                After = request.After with { Id = ghost },
                ExpectedQuest = request.ExpectedQuest with { CharacterId = ghost },
                RewardedQuest = request.RewardedQuest with { CharacterId = ghost },
            }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentIdenticalClaims_CommitExactlyOnce(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider, nonrepeatable: true);
        using var barrier = new Barrier(2);
        async Task<QuestRewardCommitResult> Claim()
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            try
            {
                return await CommitAsync(connection, request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A provider serialization failure (SQLite busy, PostgreSQL 40001, MariaDB deadlock) rolls back;
                // the retry observes the durable guard exactly as the settlement's reconciliation does.
                await Task.Delay(50);
                return await CommitAsync(connection, request);
            }
        }

        QuestRewardCommitResult[] results = await Task.WhenAll(Task.Run(Claim), Task.Run(Claim));
        Assert.Single(results, QuestRewardCommitResult.Committed);
        Assert.DoesNotContain(QuestRewardCommitResult.CharacterMissing, results);
        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions, CharacterQuestRewardRequest)> CreateAsync(DatabaseProvider provider, bool nonrepeatable)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = 78, Name = "Autocomp", Race = 1, Class = 1, Level = 10,
        });
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, LevelPlayedTime: 40, Money: 100,
            Inventory: new InventorySnapshot([new(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 4 })]));
        await characters.SaveStateAsync(before);
        CharacterState after = before with
        {
            Money = 110,
            Inventory = new InventorySnapshot([new(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 3 })]),
        };
        // The virtual complete row: the quest was never in the journal.
        var expected = new CharacterQuestStatus(character.Id, Quest, 1, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return (connection, new CharacterQuestRewardRequest(before, after, expected,
            expected with { Status = nonrepeatable ? (byte)1 : (byte)0, Rewarded = true }, InsertIfMissing: true));
    }

    private static async Task<QuestRewardCommitResult> CommitAsync(DatabaseConnectionOptions connection, CharacterQuestRewardRequest request)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfCharacterQuestRewardStore(db).CommitAsync(request);
    }

    private static async Task AssertPersistedAsync(DatabaseConnectionOptions connection, CharacterState state, CharacterQuestStatus quest)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord? character = await new EfCharacterStore(db).GetByIdAsync(state.Id);
        Assert.NotNull(character);
        Assert.Equal(state.Money, character.Money);
        Assert.Equal(state.Inventory!.Items.Sum(i => (long)i.Item.Count),
            (await new EfItemStore(db).GetInventoryAsync(state.Id)).Sum(i => (long)i.Item.Count));
        Assert.Equal(quest, Assert.Single((await new EfCharacterQuestStore(db).LoadAsync(state.Id)).Quests));
    }
}
