using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The outcome checks of the group coordinator (<see cref="PlayerbotGroupAnomalies"/>): each fires when its outcome is missing and stays
/// silent when it is there. The clocks are pure bookkeeping (tested directly); the coordinator's wiring is tested on the manual-clock
/// <see cref="GroupTestWorld"/> by handing the observer a controlled clock.
/// </summary>
public sealed class PlayerbotGroupAnomalyTests
{
    private const uint LesserHeal = 2050, Smite = 585;
    private const byte Human = 1, Warrior = 1, Priest = 5;
    private const uint StepLimit = 20_000;
    private const uint GoalTimeout = 600_000;
    private const uint TravelLimit = 600_000;

    private static string Key(string code) => PlayerbotGroupAnomalies.Key(code);

    private static PlayerbotRunMachine Machine() => new(Build().Find(RagefireChasm, 0)!, 3);

    private static PlayerbotGroupFinding Look(PlayerbotGroupWatch watch, PlayerbotGroupState state, uint entered, PlayerbotRunMachine? run, uint now)
        => watch.Observe(state, entered, StepLimit, run is null ? GoalTimeout : uint.MaxValue, TravelLimit, run, now);

    // --- state-stuck (pure) --------------------------------------------------------------------------------------------

    [Fact]
    public void AGroupInOneState_PastItsBudget_IsFoundOnce_UntilItEntersAnotherState()
    {
        var watch = new PlayerbotGroupWatch();
        uint budget = PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Travelling, StepLimit, GoalTimeout);

        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Travelling, 1_000, null, 1_000 + budget)); // at the budget: not past it
        Assert.Equal(PlayerbotGroupFinding.StateStuck, Look(watch, PlayerbotGroupState.Travelling, 1_000, null, 1_001 + budget));
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Travelling, 1_000, null, 9_000 + budget)); // once per episode
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Gathering, 500_000, null, 500_100));
        Assert.Equal(PlayerbotGroupFinding.StateStuck, Look(watch, PlayerbotGroupState.Gathering, 500_000, null, 500_001 + budget)); // a new entry reports again
    }

    [Fact]
    public void AGroupThatLeavesItsState_InTime_IsNeverFound_AndTheClockSurvivesAWrap()
    {
        var watch = new PlayerbotGroupWatch();
        uint entered = uint.MaxValue - 5_000;
        for (uint step = 0; step < 90_000; step += 1_000)
            Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Travelling, entered, null, unchecked(entered + step)));
    }

    [Fact]
    public void TheBudgetOfEachState_FollowsTheBoundTheCoordinatorEnforces()
    {
        Assert.Equal(PlayerbotGroupWatch.FormingBudgetMs, PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Forming, StepLimit, GoalTimeout));
        Assert.Equal(GoalTimeout + PlayerbotGroupWatch.GraceMs, PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Engaging, StepLimit, GoalTimeout));
        Assert.Equal(2 * StepLimit + PlayerbotGroupWatch.GraceMs, PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Gathering, StepLimit, GoalTimeout));
        Assert.Equal(PlayerbotGroupWatch.WipedBudgetMs, PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Wiped, StepLimit, GoalTimeout));
        Assert.Equal(uint.MaxValue, PlayerbotGroupWatch.StateBudgetMs(PlayerbotGroupState.Travelling, uint.MaxValue, uint.MaxValue));
    }

    // --- run-step-stuck and route-step-incomplete (pure) ---------------------------------------------------------------

    [Fact]
    public void ARunStep_PastItsBudget_IsFoundOnce_AndAStepThatAdvancesIsNot()
    {
        var watch = new PlayerbotGroupWatch();
        PlayerbotRunMachine run = Machine();
        uint budget = PlayerbotGroupWatch.RunStepBudgetMs(PlayerbotRunStep.Travel, TravelLimit, PlayerbotGroupCoordinator.StepTimeoutMs);

        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, 0));
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, budget - 1));
        Assert.True((Look(watch, PlayerbotGroupState.Engaging, 0, run, budget + 1) & PlayerbotGroupFinding.RunStepStuck) != 0);
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, budget + 5_000));

        run.Apply(PlayerbotRunSignal.Arrived); // the step moved on: a fresh clock
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, budget + 6_000));
    }

    [Fact]
    public void ARoute_WithoutABossDying_IsFoundOncePerBoss_AndABossDeathStartsTheClockOver()
    {
        var watch = new PlayerbotGroupWatch();
        PlayerbotRunMachine run = Machine();
        run.Apply(PlayerbotRunSignal.Arrived);
        run.Apply(PlayerbotRunSignal.AllInside);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);

        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, 0));
        run.Apply(PlayerbotRunSignal.BossEngaged);
        run.Apply(PlayerbotRunSignal.BossDead(run.CurrentBoss!.Entry));
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, 1_700_000)); // a kill: the clock starts over
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, 3_400_000 - 1_000));
        Assert.True((Look(watch, PlayerbotGroupState.Engaging, 0, run, 3_501_000) & PlayerbotGroupFinding.RouteStepIncomplete) != 0);
        Assert.Equal(PlayerbotGroupFinding.None, Look(watch, PlayerbotGroupState.Engaging, 0, run, 3_600_000));
    }

    // --- member clocks (pure) ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData((int)PlayerbotGroupState.Travelling, true, true, false, 10f, true)] // other map instance
    [InlineData((int)PlayerbotGroupState.Engaging, true, true, true, 151f, true)] // too far
    [InlineData((int)PlayerbotGroupState.Engaging, true, true, true, 149f, false)] // close enough
    [InlineData((int)PlayerbotGroupState.Travelling, false, true, true, 400f, false)] // dead: another check
    [InlineData((int)PlayerbotGroupState.Travelling, true, false, false, 0f, false)] // between maps: no observation
    [InlineData((int)PlayerbotGroupState.Gathering, true, true, false, 0f, false)] // gathering has its own bound
    [InlineData((int)PlayerbotGroupState.Wiped, true, true, true, 900f, false)]
    public void AMemberIsLeftBehind_OnlyWhenAliveOnKnownMaps_WhileTheGroupTravelsOrFights(int state, bool alive, bool known, bool same, float yards, bool expected)
        => Assert.Equal(expected, PlayerbotMemberWatch.IsLeftBehind((PlayerbotGroupState)state, alive, known, same, yards));

    [Fact]
    public void ADeadMember_IsFoundOnce_AtTheRecoveryBudget_AndARevivedOneIsNot()
    {
        var watch = new PlayerbotMemberWatch();
        long t = 0;
        bool fired = false;
        for (; t <= PlayerbotGroupWatch.MemberRecoveryBudgetMs - 1_000; t += 30_000) fired |= watch.ObserveDead(true, t, out _);
        Assert.False(fired);
        for (; t <= PlayerbotGroupWatch.MemberRecoveryBudgetMs + 60_000; t += 30_000) fired |= watch.ObserveDead(true, t, out _);
        Assert.True(fired);
        Assert.False(watch.ObserveDead(true, t + 30_000, out _)); // once per death

        var revived = new PlayerbotMemberWatch();
        bool revivedFired = false;
        for (long u = 0; u < 20 * 60_000L; u += 30_000) revivedFired |= revived.ObserveDead(u % (10 * 60_000L) < 5 * 60_000L, u, out _); // dead five minutes, then alive, over and over
        Assert.False(revivedFired);
    }

    // --- the coordinator's wiring --------------------------------------------------------------------------------------

    private static void Deafen(WorldSession session)
        => ((CancellationTokenSource)typeof(WorldSession).GetField("_kick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(session)!).Cancel();

    private static async Task<(GroupTestWorld World, PlayerbotGroupCoordinator.BotGroup Group, (Guid Id, string Name) Leader, (Guid Id, string Name) Other)> FormAsync(string prefix)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        var leader = group.LeaderBotId == tank.Id ? tank : healer;
        return (world, group, leader, leader.Id == tank.Id ? healer : tank);
    }

    [Fact]
    public async Task AGroupStuckInAState_IsReportedOnce_AndAGroupWithinItsBudgetIsNot()
    {
        var (world, group, leaderBot, _) = await FormAsync("Gsta");
        await using GroupTestWorld _ = world;
        Player leader = world.Player(leaderBot.Id);
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(leader);

        await world.OnWorldAsync(() =>
        {
            uint entered = group.StateEnteredMs;
            world.Coordinator.ObserveOutcomes(group, leader, unchecked(entered + 5_000), StepLimit);
            return true;
        });
        Assert.False(cap.Any(Key(PlayerbotGroupAnomalies.StateStuck)), cap.Describe());

        await world.OnWorldAsync(() =>
        {
            uint late = unchecked(group.StateEnteredMs + 4_000_000_000u);
            world.Coordinator.ObserveOutcomes(group, leader, late, StepLimit);
            world.Coordinator.ObserveOutcomes(group, leader, unchecked(late + 1_000), StepLimit);
            return true;
        });
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.StateStuck)));
        Assert.Contains("past its", cap.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABotReleasedFromAGroup_ThatStaysInTheServersGroup_IsReported_AndOneThatLeftIsNot()
    {
        var (world, group, _, otherBot) = await FormAsync("Gsti");
        await using GroupTestWorld _ = world;
        Player other = world.Player(otherBot.Id);
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(other);

        await world.OnWorldAsync(() =>
        {
            Assert.True(world.GroupManager.GetGroup(other.Guid)?.IsMember(other.Guid));
            Deafen(world.Session(otherBot.Id)); // its leave is never dispatched, so the server still has it
            world.Coordinator.Forget(otherBot.Id);
            return true;
        });
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.MemberStillGrouped)));
        Assert.Contains("still in the server's group", cap.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABotReleasedFromAGroup_WhoseLeaveWorks_IsNotReported()
    {
        var (world, _, _, otherBot) = await FormAsync("Gstj");
        await using GroupTestWorld _ = world;
        Player other = world.Player(otherBot.Id);
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(other);

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(otherBot.Id);
            Assert.Null(world.GroupManager.GetGroup(other.Guid));
            return true;
        });
        Assert.False(cap.Any(Key(PlayerbotGroupAnomalies.MemberStillGrouped)), cap.Describe());
    }

    [Fact]
    public async Task ALeaderThatIsLost_IsReported_AndTheGroupEnds()
    {
        var (world, group, leaderBot, _) = await FormAsync("Gldr");
        await using GroupTestWorld _ = world;
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(world.Player(leaderBot.Id));

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(leaderBot.Id);
            return true;
        });
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.LeaderGone)));
        Assert.DoesNotContain(group, world.Coordinator.Groups);
    }

    [Fact]
    public async Task BotsStoppedByTheHostShutdown_ReportNoLeaderGoneOrStillGrouped_ButTheGroupStillEnds()
    {
        var (world, group, leaderBot, otherBot) = await FormAsync("Gshd");
        await using GroupTestWorld _ = world;
        Player leader = world.Player(leaderBot.Id);
        Player other = world.Player(otherBot.Id);
        using AnomalyCapture leaderCap = PlayerbotAnomalies.CaptureFor(leader);
        using AnomalyCapture otherCap = PlayerbotAnomalies.CaptureFor(other);
        await world.OnWorldAsync(() =>
        {
            Deafen(world.Session(otherBot.Id)); // its leave is never dispatched: outside a shutdown this is the still-grouped finding
            return true;
        });

        // The real path: the managed feature marks the coordinator, stops every bot, and posts one Forget per bot to the world thread.
        await world.Bots.ShutdownBeforeWorldStopAsync();
        await world.OnWorldAsync(() => true); // drain the posted Forget calls

        Assert.False(leaderCap.Any(Key(PlayerbotGroupAnomalies.LeaderGone)), leaderCap.Describe());
        Assert.False(leaderCap.Any(Key(PlayerbotGroupAnomalies.MemberStillGrouped)), leaderCap.Describe());
        Assert.False(otherCap.Any(Key(PlayerbotGroupAnomalies.LeaderGone)), otherCap.Describe());
        Assert.False(otherCap.Any(Key(PlayerbotGroupAnomalies.MemberStillGrouped)), otherCap.Describe());
        Assert.DoesNotContain(group, world.Coordinator.Groups);
    }

    [Fact]
    public async Task AMemberThatIsLost_IsNotAGroupLeaderGone()
    {
        var (world, _, leaderBot, otherBot) = await FormAsync("Gmem");
        await using GroupTestWorld _ = world;
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(world.Player(leaderBot.Id));
        using AnomalyCapture otherCap = PlayerbotAnomalies.CaptureFor(world.Player(otherBot.Id));

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(otherBot.Id);
            return true;
        });
        Assert.False(cap.Any(Key(PlayerbotGroupAnomalies.LeaderGone)), cap.Describe());
        Assert.False(otherCap.Any(Key(PlayerbotGroupAnomalies.LeaderGone)), otherCap.Describe());
    }

    [Fact]
    public async Task AMemberDeadPastTheRecoveryBudget_IsReported_AndOneDeadForLessIsNot()
    {
        var (world, group, leaderBot, otherBot) = await FormAsync("Gdea");
        await using GroupTestWorld _ = world;
        Player leader = world.Player(leaderBot.Id);
        Player other = world.Player(otherBot.Id);
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(other);

        await world.OnWorldAsync(() =>
        {
            other.Health = 0;
            other.Map!.Combat.KillPlayer(other);
            Assert.False(other.IsAlive);
            uint t = 1_000_000;
            for (; t < 1_000_000 + 600_000; t += 30_000) world.Coordinator.WatchMembers(group, leader, t); // ten minutes
            return t;
        });
        Assert.False(cap.Any(Key(PlayerbotGroupAnomalies.MemberUnrecovered)), cap.Describe());

        await world.OnWorldAsync(() =>
        {
            for (uint t = 1_600_000; t <= 1_000_000 + 960_000; t += 30_000) world.Coordinator.WatchMembers(group, leader, t); // sixteen
            return true;
        });
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.MemberUnrecovered)));
    }

    [Fact]
    public async Task AMemberFarFromItsLeader_WhileTheGroupTravels_IsReported_AndOneBesideItIsNot()
    {
        var (world, group, leaderBot, otherBot) = await FormAsync("Glft");
        await using GroupTestWorld _ = world;
        Player leader = world.Player(leaderBot.Id);
        Player other = world.Player(otherBot.Id);
        using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(other);

        await world.OnWorldAsync(() =>
        {
            group.State = PlayerbotGroupState.Travelling;
            other.Relocate(leader.X, leader.Y, leader.Z, 0, world.World.Host.World.NowMs);
            for (uint t = 1_000_000; t < 1_000_000 + 600_000; t += 30_000) world.Coordinator.WatchMembers(group, leader, t);
            return true;
        });
        Assert.False(cap.Any(Key(PlayerbotGroupAnomalies.MemberLeftBehind)), cap.Describe());

        await world.OnWorldAsync(() =>
        {
            other.Relocate(leader.X + 400f, leader.Y, leader.Z, 0, world.World.Host.World.NowMs);
            for (uint t = 2_000_000; t < 2_000_000 + 360_000; t += 30_000) world.Coordinator.WatchMembers(group, leader, t);
            return true;
        });
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.MemberLeftBehind)));
        Assert.Contains("yd from", cap.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInviteCheckKey_IsStable_PerBot_WhetherOrNotThePlayerIsOnline()
    {
        using AnomalyCapture cap = AnomalyLedger.Shared.Capture(n => n.Subject == "bot Gnamed (guid ABC)");
        PlayerbotGroupAnomalies.ReportNamed("Gnamed", new ArcaneCore.Game.ObjectGuid(0xABC), PlayerbotGroupAnomalies.InviteUnanswered, "detail");
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.InviteUnanswered)));
    }
}
