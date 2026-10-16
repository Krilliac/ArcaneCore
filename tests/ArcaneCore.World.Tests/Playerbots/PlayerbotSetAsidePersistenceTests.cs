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
    public void LegacyPermanentRow_MigratesToStaggeredExpiry()
    {
        Guid bot = Guid.NewGuid();
        PlayerbotSetAside[] saved = [new(bot, 7, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue)];
        var after = new PlayerbotSuspensions();
        int before = after.Version;
        after.Restore(saved, 10, 1_000_000);
        Assert.NotEqual(before, after.Version); // the next save rewrites the row finite
        uint stagger = PlayerbotSuspensions.LegacyStagger(bot, 1361);
        Assert.InRange(stagger, 0u, 239_999u);
        Assert.True(after.IsQuestSuspended(1361, 10 + 59_999));
        Assert.False(after.IsQuestSuspended(1361, 10 + 60_000 + stagger));
        Assert.False(after.IsQuestSuspended(1361, 10 + 300_000));

        var exported = new PlayerbotSuspensions();
        exported.Restore(saved, 10, 1_000_000);
        PlayerbotSetAside row = Assert.Single(exported.Export(bot, 7, 10, 1_000_000));
        Assert.Equal(1_000_000 + 60_000 + stagger, row.ExpiresUnixMs);
        Assert.Equal(2, row.RepeatCount);

        // Idempotent: restoring the same row again, or the rewritten one, changes nothing.
        exported.Restore(saved, 10, 1_000_000);
        Assert.Equal(row, Assert.Single(exported.Export(bot, 7, 10, 1_000_000)));
        var reloaded = new PlayerbotSuspensions();
        reloaded.Restore([row], 10, 1_000_000);
        Assert.Equal(row, Assert.Single(reloaded.Export(bot, 7, 10, 1_000_000)));

        // The share is stable per bot and quest, and differs between bots.
        Assert.Equal(stagger, PlayerbotSuspensions.LegacyStagger(bot, 1361));
        Assert.True(Enumerable.Range(1, 8).Select(i => PlayerbotSuspensions.LegacyStagger(new Guid(i, 0, 0, new byte[8]), 1361)).Distinct().Count() > 1);

        // A shifted wall clock cannot overflow the remaining-time conversion: the legacy row still becomes a finite one.
        var late = new PlayerbotSuspensions();
        late.Restore(saved, 0, long.MaxValue - 1);
        Assert.True(late.IsQuestSuspended(1361, 0));
        Assert.False(late.IsQuestSuspended(1361, 300_000));

        // A finite row longer than the kind allows (a clock set back) is shortened, not dropped.
        var clamped = new PlayerbotSuspensions();
        clamped.Restore([new(bot, 7, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue - 5)], 0, 1_000_000);
        Assert.True(clamped.IsQuestSuspended(1361, 86_399_999));
        Assert.False(clamped.IsQuestSuspended(1361, 86_400_000)); // the longest an unreachable quest is set aside
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
