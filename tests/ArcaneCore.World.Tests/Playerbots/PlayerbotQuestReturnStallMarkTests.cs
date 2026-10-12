using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA reads a stall of the walk home from the " return-trip" mark the feature logs beside the brain's report. These tests drive a real
/// stall through the brain (the stall watch gives the goal up and the same think re-evaluates the quest return, which rewrites
/// <see cref="PlayerbotQuestGoals.ReturnQuest"/>), then log it exactly as <c>ManagedPlayerbotFeature</c> does after the think.
/// </summary>
public sealed partial class PlayerbotQuestReturnTests
{
    /// <summary>Every plan fails and ends at the bot's feet, so no steep climb is ever measured and the quest is never set aside by the destinations.</summary>
    private sealed class NoWay : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, start]);
    }

    /// <summary>Thinks until the stall watch gives the goal up; returns the report, or fails the test.</summary>
    private static async Task<string> StallAsync(ReturnFixture fixture, PlayerbotBrain brain)
    {
        for (int think = 0; think < 600; think++)
        {
            string? report = await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                return brain.StallReport;
            });
            if (report is not null) return report;
            await fixture.Host.World.AdvanceClockAsync(500);
        }

        throw new Xunit.Sdk.XunitException("the bot never stalled");
    }

    private static async Task<PlayerbotBrain> StartStallingAsync(ReturnFixture fixture, bool completed)
    {
        await fixture.AcceptAndCompleteAsync(complete: completed);
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new Walled(player.Z), pathfinder: new NoWay());
            return true;
        });
        await fixture.Host.WaitForWorldAsync(() => !fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen giver");
        return new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
    }

    private static string LogLine(PlayerbotBrain brain, Player player, string report)
    {
        var log = new ListLogger();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Stallbot", brain.StallCount, 0, report, brain.StallOutsideHome, brain.SharedSetAside,
            brain.QaReportContext(player, report));
        return Assert.Single(log.Entries).Message;
    }

    [Fact]
    public async Task ARealStallOfTheWalkHome_IsLoggedWithTheReturnTripMark_ForTheStallOnly()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        PlayerbotBrain brain = await StartStallingAsync(fixture, completed: true);
        try
        {
            string report = await StallAsync(fixture, brain);
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                Assert.StartsWith("stalled ", report, StringComparison.Ordinal);
                Assert.Contains("goal=Quest", report, StringComparison.Ordinal);
                Assert.Contains($"quest={QuestId}", report, StringComparison.Ordinal);
                Assert.Equal(1, brain.StallCount);
                // The give-up suspended the quest and the same think rewrote the trip: the mark must not depend on that field.
                Assert.True(brain.Suspensions.IsQuestSuspended(QuestId, fixture.Host.World.NowMs));
                Assert.Equal(0u, brain.QuestGoals.ReturnQuest);

                string line = LogLine(brain, player, report);
                Assert.Contains(" return-trip qa[", line, StringComparison.Ordinal);
                Assert.True(QaData.Parse(line).ReturnTrip, line);
                Assert.Equal(QuestId, QaData.Parse(line).Quest);
                return true;
            });
            // Recorded, not asserted: the target the report carries decides whether the classifier can name the ender.
            Assert.Matches(@"target=\d+", report);

            // The mark belongs to the stall it was captured for: the next think, and the next give-up, leave the old report unmarked.
            await StallThinksAsync(fixture, brain, 2);
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                brain.GiveUpStalledGoal(player);
                Assert.Equal(0u, brain.StallReturnQuest);
                Assert.DoesNotContain("return-trip", brain.QaReportContext(player, report), StringComparison.Ordinal);
                return true;
            });
        }
        finally { await StopAsync(fixture, brain); }
    }

    private static async Task StallThinksAsync(ReturnFixture fixture, PlayerbotBrain brain, int thinks)
    {
        for (int think = 0; think < thinks; think++)
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                return true;
            });
            await fixture.Host.World.AdvanceClockAsync(500);
        }
    }

    [Fact]
    public async Task ARealStallOnTheWayToTakeTheQuest_IsNotMarkedAsAReturnTrip()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        // The quest is not accepted. The NPC that gives it also ends it; it stands in view but out of reach, so the bot walks to it to TAKE
        // the quest, and no way is found.
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40, player.Y, player.Z, 0);
            WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new Walled(player.Z), pathfinder: new NoWay());
            return true;
        });
        await fixture.Host.WaitForWorldAsync(() => fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "visible giver");
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
        try
        {
            string report = await StallAsync(fixture, brain);
            await fixture.Host.World.InvokeAsync(() =>
            {
                Assert.StartsWith("stalled ", report, StringComparison.Ordinal);
                Assert.Contains("goal=Quest", report, StringComparison.Ordinal);
                Assert.Contains($"quest={QuestId}", report, StringComparison.Ordinal);
                Assert.Equal(QuestStatus.None, fixture.Feature.Services.StateOf(fixture.Session.Player!)!.Quests.Get(QuestId)?.Status ?? QuestStatus.None);
                string line = LogLine(brain, fixture.Session.Player!, report);
                Assert.DoesNotContain("return-trip", line, StringComparison.Ordinal);
                Assert.False(QaData.Parse(line).ReturnTrip, line);
                return true;
            });
        }
        finally { await StopAsync(fixture, brain); }
    }
}
