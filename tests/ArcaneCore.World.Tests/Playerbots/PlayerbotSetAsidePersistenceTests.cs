using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotSetAsidePersistenceTests
{
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
