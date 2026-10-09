using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Goals set aside for every bot (<see cref="PlayerbotSharedSetAsides"/>). In the live stress test (2026-10-08, 200 bots) each bot
/// that stalled on quest 1656 set it aside for ten minutes on its own, took it again and stalled again (99 reports); the next bot
/// did the same. Once <see cref="PlayerbotSharedSetAsides.BotsToShare"/> different bots stalled on one quest, no bot picks it for a
/// while.
/// </summary>
public sealed class PlayerbotSharedSetAsideTests
{
    [Fact]
    public void AQuestOnWhichThreeBotsStalled_IsSetAsideForEveryBot()
    {
        var shared = new PlayerbotSharedSetAsides();
        var other = new PlayerbotSuspensions { Shared = shared };
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 1, nowMs: 1_000));
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 1, nowMs: 130_000)); // the same bot again counts once
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 2, nowMs: 200_000));
        Assert.False(other.IsQuestSuspended(1656, 200_000));

        Assert.Equal("quest 1656 for 30 min", shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 3, nowMs: 300_000));
        Assert.True(other.IsQuestSuspended(1656, 300_001));
        Assert.False(other.IsEntrySuspended(6747, 300_001)); // the innkeeper still serves its other quests
        Assert.False(other.IsQuestSuspended(1656, 300_000 + PlayerbotSharedSetAsides.SetAsideMs)); // tried again after a while
    }

    [Fact]
    public void StallsOutsideTheWindow_DoNotAddUp()
    {
        var shared = new PlayerbotSharedSetAsides();
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 2991, 752, 1, 0));
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 2991, 752, 2, 10_000));
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 2991, 752, 3, 20_000 + PlayerbotSharedSetAsides.WindowMs));
        Assert.False(shared.IsQuestSetAside(752, 30_000 + PlayerbotSharedSetAsides.WindowMs));
    }

    [Fact]
    public void ARepeatedSetAside_LastsLonger_AndExplorationIsNeverShared()
    {
        var shared = new PlayerbotSharedSetAsides();
        uint now = 0;
        for (ulong bot = 1; bot <= 3; bot++) shared.Report(PlayerbotGoalKind.Train, 3059, 0, bot, now += 1_000);
        Assert.True(shared.IsEntrySetAside(3059, now));
        now += PlayerbotSharedSetAsides.SetAsideMs;
        Assert.False(shared.IsEntrySetAside(3059, now));
        string? second = null;
        for (ulong bot = 4; bot <= 6; bot++) second = shared.Report(PlayerbotGoalKind.Train, 3059, 0, bot, now += 1_000);
        Assert.Equal("entry 3059 for 60 min", second);

        for (ulong bot = 1; bot <= 5; bot++)
            Assert.Null(shared.Report(PlayerbotGoalKind.Explore, 0, 2161, bot, now += 1_000));
    }

    [Fact]
    public void TheMemoryIsBounded()
    {
        var shared = new PlayerbotSharedSetAsides();
        for (uint quest = 1; quest <= PlayerbotSharedSetAsides.MaxKeys + 50; quest++)
            shared.Report(PlayerbotGoalKind.Quest, 0, quest, 1, quest);
        Assert.Equal(PlayerbotSharedSetAsides.MaxKeys, shared.Count);
    }
}
