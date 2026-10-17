using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The life of a dungeon or raid run (<see cref="PlayerbotRunMachine"/>), events in and state out, over the plans of the real
/// classic-db fixtures (<see cref="RunFixtures"/>): travel, enter, clear, boss, wipe, retry, give up and complete.
/// </summary>
public sealed class PlayerbotRunMachineTests
{
    private static PlayerbotRunPlan PlanOf(uint map) => Build().Find(map, 0) ?? throw new Xunit.Sdk.XunitException($"no plan for {map}");

    private static PlayerbotRunMachine Machine(uint map = RagefireChasm, int maxWipes = 3) => new(PlanOf(map), maxWipes);

    private static void Apply(PlayerbotRunMachine run, params PlayerbotRunSignal[] signals)
    {
        foreach (PlayerbotRunSignal signal in signals) run.Apply(signal);
    }

    [Fact]
    public void ARun_StartsTravelling_AtTheFirstBoss()
    {
        PlayerbotRunMachine run = Machine();

        Assert.Equal(PlayerbotRunStep.Travel, run.Step);
        Assert.Equal(4, run.BossCount);
        Assert.Equal(0, run.Killed);
        Assert.Equal("Oggleflint", run.CurrentBoss!.Name);
        Assert.Equal(0, run.BossIndex);
        Assert.False(run.AllBossesDead);
        Assert.False(run.Finished);
        Assert.Equal("travel", run.StepName);
    }

    [Fact]
    public void TheWholeRun_Travel_Enter_Clear_Boss_ForEveryBoss_Leave_Complete()
    {
        PlayerbotRunMachine run = Machine();

        run.Apply(PlayerbotRunSignal.Arrived);
        Assert.Equal(PlayerbotRunStep.Enter, run.Step);
        run.Apply(PlayerbotRunSignal.AllInside);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);

        string[] order = [.. run.Plan.Bosses.Select(b => b.Name)];
        Assert.Equal(["Oggleflint", "Jergosh the Invoker", "Bazzalan", "Taragaman the Hungerer"], order);
        for (int index = 0; index < order.Length; index++)
        {
            Assert.Equal(order[index], run.CurrentBoss!.Name);
            Assert.Equal(index, run.BossIndex);
            run.Apply(PlayerbotRunSignal.PackDead);
            Assert.Equal(PlayerbotRunStep.Clear, run.Step); // a dead pack changes nothing
            run.Apply(PlayerbotRunSignal.BossEngaged);
            Assert.Equal(PlayerbotRunStep.Boss, run.Step);
            run.Apply(PlayerbotRunSignal.BossDead(run.CurrentBoss.Entry));
            Assert.Equal(index + 1, run.Killed);
            Assert.Equal(index == order.Length - 1 ? PlayerbotRunStep.Leave : PlayerbotRunStep.Clear, run.Step);
        }

        Assert.True(run.AllBossesDead);
        Assert.Null(run.CurrentBoss);
        Assert.False(run.Complete);
        run.Apply(PlayerbotRunSignal.Left);
        Assert.Equal(PlayerbotRunStep.Complete, run.Step);
        Assert.True(run.Complete);
        Assert.True(run.Finished);
        Assert.Null(run.Reason);
    }

    [Fact]
    public void AWipe_SendsTheGroupBack_TheKilledBossesStayKilled_AndTheRunResumesAtTheNextBoss()
    {
        PlayerbotRunMachine run = Machine();
        uint first = run.CurrentBoss!.Entry;
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.BossEngaged, PlayerbotRunSignal.BossDead(first),
            PlayerbotRunSignal.BossEngaged);
        Assert.Equal(PlayerbotRunStep.Boss, run.Step);
        string second = run.CurrentBoss!.Name;

        run.Apply(PlayerbotRunSignal.Wipe);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
        Assert.Equal(1, run.Wipes);

        // Everyone is alive and rested: back to the entrance, in again, and on from the boss that is still alive.
        run.Apply(PlayerbotRunSignal.Regrouped);
        Assert.Equal(PlayerbotRunStep.Retry, run.Step);
        run.Apply(PlayerbotRunSignal.Arrived);
        Assert.Equal(PlayerbotRunStep.Enter, run.Step);
        run.Apply(PlayerbotRunSignal.AllInside);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);
        Assert.True(run.IsKilled(first));
        Assert.Equal(1, run.Killed);
        Assert.Equal(second, run.CurrentBoss!.Name);
        Assert.Equal(1, run.BossIndex);
    }

    [Fact]
    public void AWipe_WhileAlreadyWiped_IsNotCountedTwice()
    {
        PlayerbotRunMachine run = Machine();
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.Wipe, PlayerbotRunSignal.Wipe, PlayerbotRunSignal.Wipe);

        Assert.Equal(1, run.Wipes);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
    }

    [Fact]
    public void TheRun_IsGivenUp_AtMaxWipes_AndIgnoresEverythingAfter()
    {
        PlayerbotRunMachine run = Machine(maxWipes: 2);
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.Wipe, PlayerbotRunSignal.Regrouped,
            PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);
        Assert.False(run.Finished);

        run.Apply(PlayerbotRunSignal.Wipe);

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal("wiped", run.Reason);
        Assert.Equal(2, run.Wipes);
        Assert.True(run.Finished);
        Assert.False(run.Complete);

        Apply(run, PlayerbotRunSignal.Regrouped, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.Left,
            PlayerbotRunSignal.BossDead(run.CurrentBoss!.Entry));
        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(0, run.Killed);
    }

    [Fact]
    public void AWipeWhileStillTravelling_Counts_AndTheRunIsRetriedFromTheWipe()
    {
        PlayerbotRunMachine run = Machine(maxWipes: 3);
        Assert.Equal(PlayerbotRunStep.Travel, run.Step);

        run.Apply(PlayerbotRunSignal.Wipe); // the group fell to what stood in the way, before the entrance

        Assert.Equal(1, run.Wipes);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
        Apply(run, PlayerbotRunSignal.Regrouped, PlayerbotRunSignal.Arrived);
        Assert.Equal(PlayerbotRunStep.Enter, run.Step);
    }

    [Fact]
    public void AWipe_WhoseBudgetExpires_TakesTheRecoveryTransition_WithoutAnotherWipe()
    {
        PlayerbotRunMachine run = Machine();
        uint first = run.CurrentBoss!.Entry;
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.BossDead(first));
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);

        const uint start = 5_000;
        run.Apply(PlayerbotRunSignal.Wipe);
        run.NoteTime(start, PlayerbotGroupWatch.WipedBudgetMs); // the clock starts when the step is first seen
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
        Assert.Equal(1, run.Wipes);

        // One millisecond inside the budget the group is still recovering; the step does not move.
        run.NoteTime(start + PlayerbotGroupWatch.WipedBudgetMs - 1, PlayerbotGroupWatch.WipedBudgetMs);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);

        // The budget expired and nobody sent Regrouped (the group can sit in Regrouping for the travel budget). The recovery
        // transition is taken anyway: retry, the same wipe, the killed boss stays killed.
        run.NoteTime(start + PlayerbotGroupWatch.WipedBudgetMs, PlayerbotGroupWatch.WipedBudgetMs);
        Assert.Equal(PlayerbotRunStep.Retry, run.Step);
        Assert.Equal(1, run.Wipes);
        Assert.True(run.IsKilled(first));
        Assert.False(run.Finished);

        // A regroup that arrives late is not a second recovery, and the retry still enters.
        run.Apply(PlayerbotRunSignal.Regrouped);
        Assert.Equal(PlayerbotRunStep.Retry, run.Step);
        run.Apply(PlayerbotRunSignal.Arrived);
        Assert.Equal(PlayerbotRunStep.Enter, run.Step);

        // The next wipe gets its own budget: seeing it starts the clock, and the old deadline does not expire it at once.
        run.Apply(PlayerbotRunSignal.AllInside);
        run.Apply(PlayerbotRunSignal.Wipe);
        uint again = start + PlayerbotGroupWatch.WipedBudgetMs;
        run.NoteTime(again, PlayerbotGroupWatch.WipedBudgetMs);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
        Assert.Equal(2, run.Wipes);
        run.NoteTime(again + PlayerbotGroupWatch.WipedBudgetMs - 1, PlayerbotGroupWatch.WipedBudgetMs);
        Assert.Equal(PlayerbotRunStep.Wiped, run.Step);
        run.NoteTime(again + PlayerbotGroupWatch.WipedBudgetMs, PlayerbotGroupWatch.WipedBudgetMs);
        Assert.Equal(PlayerbotRunStep.Retry, run.Step);
        Assert.Equal(2, run.Wipes);
    }

    [Fact]
    public void AWipeThatAlreadyGaveTheRunUp_IsNotRevivedWhenTimePasses()
    {
        PlayerbotRunMachine run = Machine(maxWipes: 1);
        run.Apply(PlayerbotRunSignal.Wipe);
        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);

        run.NoteTime(1_000, PlayerbotGroupWatch.WipedBudgetMs);
        run.NoteTime(1_000 + PlayerbotGroupWatch.WipedBudgetMs, PlayerbotGroupWatch.WipedBudgetMs);

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal("wiped", run.Reason);
        Assert.Equal(1, run.Wipes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ARun_GetsMaxWipesMinusOneRetries_TheMaxWipesthWipeGivesItUp(int maxWipes)
    {
        PlayerbotRunMachine run = Machine(maxWipes: maxWipes);
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);

        int retries = 0;
        while (!run.Finished)
        {
            run.Apply(PlayerbotRunSignal.Wipe);
            if (run.Finished) break;
            run.Apply(PlayerbotRunSignal.Regrouped);
            Assert.Equal(PlayerbotRunStep.Retry, run.Step);
            retries++;
            Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);
        }

        Assert.Equal(maxWipes - 1, retries);
        Assert.Equal(maxWipes, run.Wipes);
        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal("wiped", run.Reason);
    }

    [Fact]
    public void MaxWipes_IsLive_LoweringItUnderTheWipesSoFarGivesTheRunUpAtTheNextWipe()
    {
        PlayerbotRunMachine run = Machine(maxWipes: 5);
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.Wipe, PlayerbotRunSignal.Regrouped,
            PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);

        run.MaxWipes = 2;
        run.Apply(PlayerbotRunSignal.Wipe);

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(2, run.Wipes);
    }

    [Fact]
    public void ABossFoundDeadAtItsSpawn_WhileEntering_IsCountedKilled_AndTheLastOneSendsTheGroupOut()
    {
        PlayerbotRunMachine run = Machine();
        run.Apply(PlayerbotRunSignal.Arrived);
        foreach (PlayerbotRunBoss boss in run.Plan.Bosses) run.Apply(PlayerbotRunSignal.BossDead(boss.Entry));

        Assert.True(run.AllBossesDead);
        Assert.Equal(PlayerbotRunStep.Leave, run.Step);
    }

    [Fact]
    public void AllBossesDead_AfterARetry_GoesStraightToLeave_NotToClear()
    {
        PlayerbotRunMachine run = Machine();
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);
        foreach (PlayerbotRunBoss boss in run.Plan.Bosses) run.Apply(PlayerbotRunSignal.BossDead(boss.Entry));
        Assert.Equal(PlayerbotRunStep.Leave, run.Step);

        // The group wipes on the way out: it regroups, walks in again and has nothing left to kill.
        Apply(run, PlayerbotRunSignal.Wipe, PlayerbotRunSignal.Regrouped, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);

        Assert.Equal(PlayerbotRunStep.Leave, run.Step);
        Assert.Equal(1, run.Wipes);
    }

    [Fact]
    public void EventsThatMakeNoSenseInTheCurrentStep_AreIgnored()
    {
        PlayerbotRunMachine run = Machine();

        // Travelling: nothing but arriving, a wipe or an end changes the step.
        Apply(run, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.BossEngaged, PlayerbotRunSignal.Regrouped, PlayerbotRunSignal.Left,
            PlayerbotRunSignal.BossDead(run.CurrentBoss!.Entry));
        Assert.Equal(PlayerbotRunStep.Travel, run.Step);
        Assert.Equal(0, run.Killed);

        // Inside: a creature the plan does not route (Ragefire's Taragaman is routed; entry 1 is not) kills nothing.
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.BossDead(1), PlayerbotRunSignal.Left, PlayerbotRunSignal.Arrived);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);
        Assert.Equal(0, run.Killed);
    }

    [Fact]
    public void AScriptedEncounter_IsNeverARoutedBoss_SoItsDeathChangesNothing()
    {
        // Deadmines: Sneed (643) is credited by a creature with no static spawn, so the plan carries it as scripted.
        PlayerbotRunMachine run = Machine(Deadmines);
        Assert.Contains("Sneed", run.Plan.Scripted);
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);

        run.Apply(PlayerbotRunSignal.BossDead(643));

        Assert.Equal(0, run.Killed);
        Assert.Equal(PlayerbotRunStep.Clear, run.Step);
    }

    [Theory]
    [InlineData("no path to the boss", "no path to the boss")]
    [InlineData("", "no-path")]
    [InlineData(null, "no-path")]
    public void NoPath_GivesTheRunUp_WithItsReason(string? text, string reason)
    {
        PlayerbotRunMachine run = Machine();

        run.Apply(text is null ? new PlayerbotRunSignal(PlayerbotRunEvent.NoPath) : PlayerbotRunSignal.NoPath(text));

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(reason, run.Reason);
    }

    [Theory]
    [InlineData("travel", "travel-timeout")]
    [InlineData("enter", "enter-timeout")]
    [InlineData(null, "run-timeout")]
    public void ATimeout_GivesTheRunUp_NamingTheStepThatTookTooLong(string? kind, string reason)
    {
        PlayerbotRunMachine run = Machine();

        run.Apply(kind is null ? new PlayerbotRunSignal(PlayerbotRunEvent.Timeout) : PlayerbotRunSignal.Timeout(kind));

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(reason, run.Reason);
    }

    [Theory]
    [InlineData("leader-stopped", "leader-stopped")]
    [InlineData(null, "aborted")]
    public void AnAbort_GivesTheRunUp_WithTheGroupsReason(string? text, string reason)
    {
        PlayerbotRunMachine run = Machine();
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);

        run.Apply(text is null ? new PlayerbotRunSignal(PlayerbotRunEvent.Abort) : PlayerbotRunSignal.Abort(text));

        Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
        Assert.Equal(reason, run.Reason);
        Assert.False(run.Complete);
    }

    [Fact]
    public void ACompletedRun_IgnoresALateAbort_AndStaysComplete()
    {
        PlayerbotRunMachine run = Machine();
        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside);
        foreach (PlayerbotRunBoss boss in run.Plan.Bosses) run.Apply(PlayerbotRunSignal.BossDead(boss.Entry));
        Apply(run, PlayerbotRunSignal.Left);
        Assert.True(run.Complete);

        Apply(run, PlayerbotRunSignal.Abort("late"), PlayerbotRunSignal.Wipe, PlayerbotRunSignal.NoPath("late"));

        Assert.True(run.Complete);
        Assert.Null(run.Reason);
        Assert.Equal(0, run.Wipes);
    }

    [Fact]
    public void ARaidRun_WithOneRoutedBoss_RunsTheSameStates()
    {
        PlayerbotRunPlan onyxia = PlanOf(OnyxiasLair);
        Assert.True(onyxia.Raid);
        var run = new PlayerbotRunMachine(onyxia, 5);

        Apply(run, PlayerbotRunSignal.Arrived, PlayerbotRunSignal.AllInside, PlayerbotRunSignal.BossEngaged);
        Assert.Equal(PlayerbotRunStep.Boss, run.Step);
        Apply(run, PlayerbotRunSignal.BossDead(10184));
        Assert.Equal(PlayerbotRunStep.Leave, run.Step);
        Apply(run, PlayerbotRunSignal.Left);
        Assert.True(run.Complete);
    }
}
