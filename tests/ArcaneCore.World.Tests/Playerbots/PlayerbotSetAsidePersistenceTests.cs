using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotSetAsidePersistenceTests
{
    [Fact]
    public void RestoreTwice_KeepsEachLocalSetBounded()
    {
        Guid bot = Guid.NewGuid();
        PlayerbotSetAside[] first = [.. Enumerable.Range(1, 80).Select(id =>
            new PlayerbotSetAside(bot, 7, PlayerbotSetAsideKind.Quest, (uint)id, 1_500_000))];
        PlayerbotSetAside[] second = [.. Enumerable.Range(81, 80).Select(id =>
            new PlayerbotSetAside(bot, 7, PlayerbotSetAsideKind.Quest, (uint)id, 1_500_000))];
        var suspensions = new PlayerbotSuspensions();
        suspensions.Restore(first, 0, 1_000_000);
        suspensions.Restore(second, 0, 1_000_000);
        Assert.True(suspensions.Export(bot, 7, 0, 1_000_000).Count(row => row.Kind == PlayerbotSetAsideKind.Quest) <= 64);
    }

    [Fact]
    public void UnreachableQuestRow_NeverExpiring_SurvivesRestoreAndAnyClockAdvance()
    {
        Guid bot = Guid.NewGuid();
        PlayerbotSetAside[] saved = [new(bot, 7, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue)];
        var after = new PlayerbotSuspensions();
        after.Restore(saved, 10, 1_000_000);
        Assert.True(after.IsQuestSuspended(1361, 10));
        Assert.True(after.IsQuestSuspended(1361, 10 + PlayerbotSuspensions.SuspendMs * 1000));
        Assert.True(after.IsQuestSuspended(1361, uint.MaxValue));
        PlayerbotSetAside row = Assert.Single(after.Export(bot, 7, 10, 1_000_000));
        Assert.Equal(long.MaxValue, row.ExpiresUnixMs);

        // A shifted wall clock cannot overflow the remaining-time conversion, and a bad (non-sentinel) row is ignored.
        var late = new PlayerbotSuspensions();
        late.Restore(saved, 0, long.MaxValue - 1);
        Assert.True(late.IsQuestSuspended(1361, 0));
        var other = new PlayerbotSuspensions();
        other.Restore([new(bot, 7, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue - 5)], 0, 1_000_000);
        Assert.False(other.IsQuestSuspended(1361, 0));
    }

    [Fact]
    public void BotSetAside_RestoresRemainingLifetime_AndDoesNotExtendOnRestart()
    {
        Guid bot = Guid.NewGuid();
        var before = new PlayerbotSuspensions();
        before.SuspendQuest(1361, 100);
        before.SuspendEntry(123, 100);
        before.SuspendTraining(100);
        PlayerbotSetAside[] saved = before.Export(bot, 7, 200, 1_000_000);

        var after = new PlayerbotSuspensions();
        after.Restore(saved, 50, 1_000_100);
        Assert.True(after.IsQuestSuspended(1361, 50));
        Assert.True(after.IsEntrySuspended(123, 50));
        Assert.True(after.IsTrainingSuspended(50));
        Assert.False(after.IsQuestSuspended(1361, 50 + PlayerbotSuspensions.SuspendMs - 200));

        var expired = new PlayerbotSuspensions();
        expired.Restore(saved, 0, 1_000_000 + PlayerbotSuspensions.SuspendMs);
        Assert.False(expired.IsQuestSuspended(1361, 0));
    }

    [Fact]
    public void SharedSetAside_RestoresForOtherBots_AndExpiresAtOriginalDeadline()
    {
        var before = new PlayerbotSharedSetAsides();
        for (ulong bot = 1; bot <= 3; bot++) before.Report(PlayerbotGoalKind.Quest, 42, 6605, bot, 100);
        PlayerbotSetAside[] saved = before.Export(200, 1_000_000);

        var after = new PlayerbotSharedSetAsides();
        after.Restore(saved, 50, 1_000_100);
        Assert.True(after.IsQuestSetAside(6605, 50));
        Assert.False(after.IsQuestSetAside(6605, 50 + PlayerbotSharedSetAsides.SetAsideMs - 200));
    }
}
