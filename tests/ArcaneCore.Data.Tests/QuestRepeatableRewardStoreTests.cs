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
/// feat/quest-progression: repeatable rewards (status NONE after the reward, vmangos RewardQuest)
/// and quest level-ups ride the same single reward transaction.
/// </summary>
public sealed class QuestRepeatableRewardStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RepeatableReward_CommitsNoneWithLevel_AndADuplicateIsRefused(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, request));
        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RepeatableTakenAgain_PersistsItsNewProgress_AndCanBeRewardedAgain(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest first) = await CreateAsync(provider);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, first));

        // Re-accepted and completed: the ordinary delta carries the earlier reward flag.
        CharacterQuestStatus again = first.RewardedQuest with { Status = 1, MobCount1 = 2 };
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await new EfCharacterQuestStore(db).SaveQuestsAsync(again.CharacterId, [again]);
        }

        CharacterState after = first.After with { Money = first.After.Money + 10 };
        var second = new CharacterQuestRewardRequest(first.After, after, again, again with { Status = 0, Timer = 0 });
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, second));
        await AssertPersistedAsync(connection, after, second.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OlderUnrewardedDelta_CannotReopenRepeatableHistory(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await new EfCharacterQuestStore(db).SaveQuestsAsync(request.ExpectedQuest.CharacterId,
                [request.ExpectedQuest with { Status = 3, MobCount1 = 0 }]);
        }

        await AssertPersistedAsync(connection, request.After, request.RewardedQuest);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IncoherentRewardRows_AreRejectedBeforeTouchingStorage(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider);
        // A nonrepeatable (status COMPLETE) reward may not start from rewarded history.
        CharacterQuestRewardRequest rewardedExpected = request with
        {
            ExpectedQuest = request.ExpectedQuest with { Rewarded = true },
            RewardedQuest = request.RewardedQuest with { Status = 1 },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => CommitAsync(connection, rewardedExpected));
        await Assert.ThrowsAsync<ArgumentException>(() => CommitAsync(connection,
            request with { RewardedQuest = request.RewardedQuest with { Status = 3 } }));
        await Assert.ThrowsAsync<ArgumentException>(() => CommitAsync(connection,
            request with { RewardedQuest = request.RewardedQuest with { MobCount2 = 9 } }));
        await AssertPersistedAsync(connection, request.Before, request.ExpectedQuest);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions, CharacterQuestRewardRequest)> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var characters = new EfCharacterStore(db);
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = 77, Name = "Repeater", Race = 1, Class = 1, Level = 10,
        });
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, LevelPlayedTime: 40, Money: 100,
            Inventory: new InventorySnapshot([new(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 4 })]));
        await characters.SaveStateAsync(before);
        var expected = new CharacterQuestStatus(character.Id, 900060, 1, false, false, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected]);
        CharacterState after = before with
        {
            Level = 11, LevelPlayedTime = 0, Money = 110,
            Inventory = new InventorySnapshot([new(0, 23, new ItemInstanceData { Guid = 100, Entry = 117, Count = 3 })]),
        };
        return (connection, new CharacterQuestRewardRequest(before, after, expected, expected with { Status = 0, Rewarded = true }));
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
        Assert.Equal((state.Level, state.LevelPlayedTime, state.Money), (character.Level, character.LevelPlayedTime, character.Money));
        Assert.Equal(state.Inventory!.Items.Sum(i => (long)i.Item.Count),
            (await new EfItemStore(db).GetInventoryAsync(state.Id)).Sum(i => (long)i.Item.Count));
        Assert.Equal(quest, Assert.Single((await new EfCharacterQuestStore(db).LoadAsync(state.Id)).Quests));
    }
}
