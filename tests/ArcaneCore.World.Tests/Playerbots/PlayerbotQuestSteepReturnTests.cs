using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A completed quest whose ender the ordinary mesh cannot approach from the bot's spot (Ashenvale #199): it is set aside only once the
/// failure is confirmed (the same spot, <see cref="PlayerbotWorldDestinations.ConfirmFailures"/> failed plans, a measured climb on
/// steep legs), diagnosed once per spot with a capped budget, and abandoned after repeated set-asides. Every case runs through the
/// brain's Update with the world clock advancing between thinks.
/// </summary>
public sealed partial class PlayerbotQuestReturnTests
{
    private const uint QuestId = QuestInteractionFixture.QuestId;

    [Fact]
    public async Task CompletedQuestWithOneEnder_ConfirmedUnclimbableAscentIsSetAside_AfterRepeatedFailuresAndDiagnosedOnce()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        var route = new ScriptedRoute(climb: 23.5f);
        PlayerbotBrain brain = await StartAsync(fixture, route);
        try
        {
            // Failures happen on every other think (a failed plan blocks the spawn, the next think backs off).
            await ThinkAsync(fixture, brain, 4);
            Assert.False(await IsSetAsideAsync(fixture, brain), "one or two failed plans from one spot confirm nothing");
            Assert.Equal(0, route.DiagnosisCalls);

            await ThinkAsync(fixture, brain, 8);
            Assert.True(await IsSetAsideAsync(fixture, brain), "the third failed plan from the same spot, with a measured steep climb");
            Assert.Equal(2, route.DiagnosisCalls); // one ordinary and one steep-allowed query, once
            Assert.True(route.MaxDiagnosisNodes <= PlayerbotNavigation.DiagnosisSearchNodes);
            Assert.Equal(QuestStatus.Complete, await StatusAsync(fixture));
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task CompletedQuestWithOneEnder_GentleUphillFromASlope_IsNeverSetAside_AndDiagnosedOnce()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        // The steep-allowed way rises 6 yards over 24: more than the 1.5-yard escape bound, but an ordinary climb to an uphill turn-in.
        var route = new ScriptedRoute(climb: 6f);
        PlayerbotBrain brain = await StartAsync(fixture, route);
        try
        {
            await ThinkAsync(fixture, brain, 24);
            Assert.False(await IsSetAsideAsync(fixture, brain));
            Assert.Equal(2, route.DiagnosisCalls); // not repeated every backoff cycle
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task CompletedQuestWithOneEnder_TransientFailureThenARoute_IsNeverSetAside()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        var route = new ScriptedRoute(climb: 23.5f);
        PlayerbotBrain brain = await StartAsync(fixture, route);
        try
        {
            await ThinkAsync(fixture, brain, 4); // two failed plans
            route.Blocked = false;
            await ThinkAsync(fixture, brain, 12);
            Assert.False(await IsSetAsideAsync(fixture, brain));
            Assert.Equal(0, route.DiagnosisCalls);
            Assert.True(route.Planned > 0, "the plan succeeded after the transient failures");
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task CompletedQuestWithTwoEnders_IsNeverSetAside()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, secondEnder: true, manualClock: true);
        var route = new ScriptedRoute(climb: 23.5f);
        PlayerbotBrain brain = await StartAsync(fixture, route);
        try
        {
            await ThinkAsync(fixture, brain, 24);
            Assert.False(await IsSetAsideAsync(fixture, brain));
            Assert.Equal(0, route.DiagnosisCalls);
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task CompletedQuestWithOneEnder_RepeatedSetAsides_AbandonTheQuest()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        var route = new ScriptedRoute(climb: 23.5f);
        PlayerbotBrain brain = await StartAsync(fixture, route);
        try
        {
            for (int round = 1; round < PlayerbotWorldDestinations.AbandonAfterSetAsides; round++)
            {
                await ThinkAsync(fixture, brain, 10);
                Assert.True(await IsSetAsideAsync(fixture, brain), $"set-aside {round}");
                Assert.Equal(QuestStatus.Complete, await StatusAsync(fixture));
                await fixture.Host.World.AdvanceClockAsync(PlayerbotSuspensions.SuspendMs + 100);
            }

            await ThinkAsync(fixture, brain, 10);
            Assert.NotEqual(QuestStatus.Complete, await StatusAsync(fixture));
            Assert.Equal(0u, await fixture.Host.World.InvokeAsync(() => new PlayerbotQuestGoals(fixture.Session,
                new PlayerbotOptions { Enabled = true }).CompletedReturnQuest(fixture.Session.Player!)));
            await fixture.Host.World.AdvanceClockAsync(30_000);
            Assert.True(await fixture.Host.World.InvokeAsync(() => brain.QuestGoals.IsRefused(QuestId)),
                "a quest abandoned for an unreachable ender stays refused for the minute its mark lasts (then it expires: PlayerbotDeferLedger)");
            Assert.True(await fixture.Host.World.InvokeAsync(() =>
            {
                uint now = fixture.Host.World.NowMs;
                var restored = new PlayerbotSuspensions();
                restored.Restore(brain.Suspensions.Export(Guid.NewGuid(), 1, now, 1_000_000), now, 1_000_000);
                return restored.IsQuestSuspended(QuestId, now + 10_000);
            }), "the per-bot refusal must survive an export and restore");
        }
        finally { await StopAsync(fixture, brain); }
    }

    private static async Task<PlayerbotBrain> StartAsync(ReturnFixture fixture, ScriptedRoute route)
    {
        await fixture.AcceptAndCompleteAsync();
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new FlatFloor(player.Z), pathfinder: route);
            return true;
        });
        await fixture.Host.WaitForWorldAsync(() =>
            !fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen ender");
        return new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
    }

    /// <summary>Think <paramref name="thinks"/> times, two seconds of world time apart (the destination backoff).</summary>
    private static async Task ThinkAsync(ReturnFixture fixture, PlayerbotBrain brain, int thinks)
    {
        for (int think = 0; think < thinks; think++)
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                return true;
            });
            await fixture.Host.World.AdvanceClockAsync(2_100);
        }
    }

    private static Task<bool> IsSetAsideAsync(ReturnFixture fixture, PlayerbotBrain brain)
        => fixture.Host.World.InvokeAsync(() => brain.Suspensions.IsQuestSuspended(QuestId, fixture.Host.World.NowMs)
            && brain.Suspensions.IsEntrySuspended(QuestInteractionFixture.Entry, fixture.Host.World.NowMs));

    private static Task<QuestStatus> StatusAsync(ReturnFixture fixture)
        => fixture.Host.World.InvokeAsync(() => fixture.Feature.Services.StateOf(fixture.Session.Player!)!.Quests.Get(QuestId)?.Status
            ?? QuestStatus.None);

    private static async Task StopAsync(ReturnFixture fixture, PlayerbotBrain brain)
        => await fixture.Host.World.InvokeAsync(() => { brain.Stop(); return true; });

    /// <summary>
    /// The ordinary query (steep polygons excluded) ends at the bot's feet; the steep-allowed one climbs <c>climb</c> yards over 24 flat
    /// yards, then reaches the ender. while <c>Blocked</c> the ordinary query fails, else it succeeds.
    /// </summary>
    private sealed class ScriptedRoute(float climb) : IPathfinder
    {


        public bool Blocked { get; set; } = true;
        public int DiagnosisCalls { get; private set; }
        public int MaxDiagnosisNodes { get; private set; }
        public int Planned { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            bool steep = ((options?.ExcludeFlags ?? NavTerrain.Empty) & NavTerrain.SteepSlopes) == 0;
            if (options?.MaxSearchNodes == PlayerbotNavigation.DiagnosisSearchNodes)
            {
                DiagnosisCalls++;
                MaxDiagnosisNodes = Math.Max(MaxDiagnosisNodes, options.MaxSearchNodes);
            }

            if (steep) return new PathResult(PathType.Normal, [start, start + new Vector3(24, 0, climb), end]);
            if (Blocked) return new PathResult(PathType.Incomplete, [start, start]);
            Planned++;
            return new PathResult(PathType.Normal, [start, end]);
        }
    }
}
