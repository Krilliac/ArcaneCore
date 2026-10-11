using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
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
    public void RepeatedSharedSuspensions_KeepPersistedRepeatCountWithinStoreLimit()
    {
        var shared = new PlayerbotSharedSetAsides();
        uint now = 1_000;
        ulong bot = 1;
        for (int repeat = 0; repeat < 257; repeat++)
        {
            for (int i = 0; i < PlayerbotSharedSetAsides.BotsToShare; i++)
                shared.Report(PlayerbotGoalKind.Quest, 0, 1361, bot++, now);
            Assert.True(shared.IsQuestSetAside(1361, now));
            if (repeat != 256) now += PlayerbotSharedSetAsides.MaxSetAsideMs;
        }

        Assert.InRange(Assert.Single(shared.Export(now, 1_000_000)).RepeatCount, 1, 256);
    }

    [Fact]
    public void AQuestOnWhichThreeBotsStalled_IsSetAsideForEveryBot()
    {
        var shared = new PlayerbotSharedSetAsides();
        var other = new PlayerbotSuspensions { Shared = shared };
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 1, nowMs: 1_000));
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 1, nowMs: 130_000)); // the same bot again counts once
        Assert.Null(shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 2, nowMs: 200_000));
        Assert.False(other.IsQuestSuspended(1656, 200_000));

        Assert.Equal("quest 1656 for 30 min", shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot: 3, nowMs: 300_000)?.Goal);
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
        PlayerbotSharedSetAside? second = null;
        for (ulong bot = 4; bot <= 6; bot++) second = shared.Report(PlayerbotGoalKind.Train, 3059, 0, bot, now += 1_000);
        Assert.Equal("entry 3059 for 60 min", second?.Goal);

        for (ulong bot = 1; bot <= 5; bot++)
            Assert.Null(shared.Report(PlayerbotGoalKind.Explore, 0, 2161, bot, now += 1_000));
    }

    [Fact]
    public void ASetAside_NamesWhereTheBotsThatSetItAsideStalled()
    {
        var shared = new PlayerbotSharedSetAsides();
        shared.Report(PlayerbotGoalKind.Quest, 0, 109, 1, 1_000, 0, new Vector3(-9471.9f, -1792.0f, 132.1f));
        shared.Report(PlayerbotGoalKind.Quest, 0, 109, 2, 2_000, 0, new Vector3(-9427.0f, -1908.0f, 94.0f));
        PlayerbotSharedSetAside? everyone = shared.Report(PlayerbotGoalKind.Quest, 234, 109, 3, 3_000, 1, new Vector3(1f, 2.5f, 3f));

        Assert.Equal("quest 109 for 30 min", everyone?.Goal);
        Assert.Equal("-9471.9,-1792.0,132.1 map 0; -9427.0,-1908.0,94.0 map 0; 1.0,2.5,3.0 map 1", everyone?.Stalls);
    }

    /// <summary>
    /// A homed bot that stalls outside its home does not count towards a goal set aside for every bot; inside its home it does. Live
    /// 2026-10-11 (200 bots homed at Sentinel Hill): 46 of the 49 stalls behind the shared set-asides were made outside the home, and
    /// quest 109, whose ender stands at Sentinel Hill, was set aside for every bot for up to two hours by bots stalled in Redridge and at
    /// Raven Hill. The bot still sets the goal aside for itself.
    /// </summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("inside")]
    [InlineData("none")] // an unhomed bot counts as before
    [InlineData("other-zone")] // inside the circle of a zone-clipped home, but in another zone: outside
    public async Task AStallOutsideTheHome_IsTheBotsOwn_InsideItCountsForEveryBot(string home)
    {
        bool inside = home is "inside" or "none";
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var shared = new PlayerbotSharedSetAsides();
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }, shared: shared);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                uint now = host.World.NowMs;
                // Two other bots stalled on quest 109 a moment ago: this bot's stall would be the third.
                shared.Report(PlayerbotGoalKind.Quest, 0, 109, ulong.MaxValue - 1, now);
                shared.Report(PlayerbotGoalKind.Quest, 0, 109, ulong.MaxValue - 2, now);
                var centre = new Vector3(player.X + 10f, player.Y, player.Z);
                brain.Home = home switch
                {
                    "none" => null,
                    "outside" => new PlayerbotHomeArea(player.MapId, centre + new Vector3(5000f, 0, 0), 300f, "Test"),
                    "other-zone" => new PlayerbotHomeArea(player.MapId, centre, 300f, "Test", zoneClipped: true,
                        new PlayerbotZoneLookup((x, _, _) => x == centre.X ? 40u : 12u)),
                    _ => new PlayerbotHomeArea(player.MapId, centre, 300f, "Test"),
                };
                brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 234, 109);

                brain.GiveUpStalledGoal(player);

                Assert.Equal(!inside, brain.StallOutsideHome);
                Assert.Equal(inside, shared.IsQuestSetAside(109, now));
                Assert.Equal(inside, brain.SharedSetAside is not null);
                if (inside)
                    Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{player.X:F1},{player.Y:F1},{player.Z:F1} map {player.MapId}"), brain.SharedSetAside!.Value.Stalls);
                Assert.True(brain.Suspensions.IsQuestSuspended(109, now)); // its own set-aside either way
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
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
