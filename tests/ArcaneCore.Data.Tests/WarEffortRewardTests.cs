using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class WarEffortRewardTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TurnInAndRepeatableTurnInIncrementOnlyWhenTheirQuestRewardCommits(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest first) = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Gathering, 0);

        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, first));
        Assert.Equal(20, (await LoadAsync(connection)).Counters[0]);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, first));
        Assert.Equal(20, (await LoadAsync(connection)).Counters[0]);

        CharacterQuestStatus again = first.RewardedQuest with { Status = 1, MobCount1 = 1 };
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfCharacterQuestStore(db).SaveQuestsAsync(again.CharacterId, [again]);
        var second = new CharacterQuestRewardRequest(first.After, first.After with { Money = first.After.Money + 1 },
            again, again with { Status = 0, Timer = 0 }, WarEffort: new WarEffortContribution(0, 20));
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, second));
        Assert.Equal(40, (await LoadAsync(connection)).Counters[0]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DisabledPhaseDoesNotCollectAndInvalidContributionRollsBackReward(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) = await CreateAsync(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(connection,
            request with { WarEffort = new WarEffortContribution(29, 20) }));
        Assert.Equal(0, (await LoadAsync(connection)).Counters[0]);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        Assert.Equal(0, (await LoadAsync(connection)).Counters[0]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LastResourceTurnInAdvancesTheDurablePhase(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest last) = await CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Gathering, 0);
            foreach (WarEffortResource resource in WarEffortCatalog.Resources)
                db.Set<WarEffortCounterRow>().Add(new WarEffortCounterRow
                {
                    ResourceId = resource.Id,
                    Count = resource.Goal - (resource.Id == 0 ? 20 : 0),
                });
            await db.SaveChangesAsync();
        }

        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, last));
        WarEffortSnapshot state = await LoadAsync(connection);
        Assert.Equal(WarEffortPhase.Transporting, state.Phase);
        Assert.Equal(WarEffortCatalog.Resources[0].Goal, state.Counters[0]);
        Assert.InRange(state.PhaseEndsAtUnix, before + 5 * 86_400, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5 * 86_400);
        Assert.Equal(true, state.WorldScriptCondition(2021, 0, DateTimeOffset.UtcNow));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GongRewardStartsTenHourWarAtomicallyAndOnlyOnce(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) =
            await CreateAsync(provider, WarEffortCatalog.GongQuest, repeatable: false);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Gong, 0);

        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, request));
        WarEffortSnapshot state = await LoadAsync(connection);
        Assert.Equal(WarEffortPhase.TenHourWar, state.Phase);
        // The ten hours start after the scarab_gongAI opening sequence.
        int war = WarEffortCatalog.WarStartsAfterSeconds + 10 * 3_600;
        Assert.InRange(state.PhaseEndsAtUnix, before + war, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + war);
        Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitAsync(connection, request));
        WarEffortSnapshot afterDuplicate = await LoadAsync(connection);
        Assert.Equal(state.Phase, afterDuplicate.Phase);
        Assert.Equal(state.PhaseEndsAtUnix, afterDuplicate.PhaseEndsAtUnix);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlyTheFirstGongRingerOpensTheGateAndLaterRingsOnlyCount(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest first) =
            await CreateAsync(provider, WarEffortCatalog.GongQuest, repeatable: false);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Gong, 0);

        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, first));
        WarEffortSnapshot opened = await LoadAsync(connection);
        Assert.Equal(WarEffortPhase.TenHourWar, opened.Phase);
        Assert.Equal(1, opened.GongRingCount);
        Assert.Equal(first.After.Id, opened.GongFirstRingerId);
        Assert.InRange(opened.GongFirstRungAtUnix, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(opened.GongFirstRungAtUnix + WarEffortCatalog.WarStartsAfterSeconds + WarEffortCatalog.TenHourWarSeconds,
            opened.PhaseEndsAtUnix);

        CharacterQuestRewardRequest second = await AddRingerAsync(connection, "SecondRinger");
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, second));
        WarEffortSnapshot later = await LoadAsync(connection);
        Assert.Equal(2, later.GongRingCount);
        Assert.Equal(opened.GongFirstRingerId, later.GongFirstRingerId);
        Assert.Equal(opened.GongFirstRungAtUnix, later.GongFirstRungAtUnix);
        Assert.Equal(opened.PhaseEndsAtUnix, later.PhaseEndsAtUnix);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task GongRingBeforeTheGongPhaseOrAfterTheWarIsNotCounted(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest early) =
            await CreateAsync(provider, WarEffortCatalog.GongQuest, repeatable: false);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Transporting, 123);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, early));
        WarEffortSnapshot state = await LoadAsync(connection);
        Assert.Equal(WarEffortPhase.Transporting, state.Phase);
        Assert.Equal(123, state.PhaseEndsAtUnix);
        Assert.Equal(0, state.GongRingCount);
        Assert.Equal(0, state.GongFirstRungAtUnix);

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Done, 0);
        Assert.Equal(QuestRewardCommitResult.Committed, await CommitAsync(connection, await AddRingerAsync(connection, "LateRinger")));
        Assert.Equal(0, (await LoadAsync(connection)).GongRingCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RolledBackGongRewardLeavesTheGateClosed(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterQuestRewardRequest request) =
            await CreateAsync(provider, WarEffortCatalog.GongQuest, repeatable: false);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await new EfWarEffortStateStore(db).SetPhaseAsync(WarEffortPhase.Gong, 0);
        // An invalid contribution in the same reward transaction throws; the gong ring must not commit either.
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(connection,
            request with { WarEffort = new WarEffortContribution(0, 20) }));
        WarEffortSnapshot state = await LoadAsync(connection);
        Assert.Equal(WarEffortPhase.Gong, state.Phase);
        Assert.Equal(0, state.GongRingCount);
        Assert.Equal(0, state.GongFirstRungAtUnix);
    }

    private static async Task<CharacterQuestRewardRequest> AddRingerAsync(DatabaseConnectionOptions connection, string name)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
        {
            AccountId = 78, Name = name, Race = 1, Class = 1, Level = 60,
        });
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 60, 50, Money: 100,
            Inventory: new InventorySnapshot([]));
        await new EfCharacterStore(db).SaveStateAsync(before);
        var expected = new CharacterQuestStatus(character.Id, WarEffortCatalog.GongQuest, 1, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected]);
        return new CharacterQuestRewardRequest(before, before with { Money = 101 }, expected,
            expected with { Status = 1, Rewarded = true }, WarEffortGong: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ColossusDeathFlagsAreDurableAndIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfWarEffortStateStore(db);
            Assert.True(await store.MarkBossKilledAsync(0));
            Assert.False(await store.MarkBossKilledAsync(0));
            Assert.True(await store.MarkBossKilledAsync(2));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.MarkBossKilledAsync(3));
        }

        WarEffortSnapshot saved = await LoadAsync(connection);
        Assert.Equal((byte)0b101, saved.KilledBossMask);
    }

    private async Task<(DatabaseConnectionOptions, CharacterQuestRewardRequest)> CreateAsync(
        DatabaseProvider provider, uint questId = 8549, bool repeatable = true)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
        {
            AccountId = 77, Name = "WarDonor", Race = 1, Class = 1, Level = 10,
        });
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50, Money: 100,
            Inventory: new InventorySnapshot([]));
        await new EfCharacterStore(db).SaveStateAsync(before);
        var expected = new CharacterQuestStatus(character.Id, questId, 1, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected]);
        var after = before with { Money = 101 };
        return (connection, new CharacterQuestRewardRequest(before, after, expected,
            expected with { Status = repeatable ? (byte)0 : (byte)1, Rewarded = true },
            WarEffort: questId == 8549 ? new WarEffortContribution(0, 20) : null,
            WarEffortGong: questId == WarEffortCatalog.GongQuest));
    }

    private static async Task<QuestRewardCommitResult> CommitAsync(DatabaseConnectionOptions connection, CharacterQuestRewardRequest request)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfCharacterQuestRewardStore(db).CommitAsync(request);
    }

    private static async Task<WarEffortSnapshot> LoadAsync(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfWarEffortStateStore(db).LoadAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
