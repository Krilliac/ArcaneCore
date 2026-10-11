using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>The tick-budget control loop (a pure function) against chosen p95 sequences: budget 60 ms, adding resumes below 48 ms.</summary>
public sealed class PopulationBudgetControllerTests
{
    private static PlayerbotPopulationOptions Options() => new() { Enabled = true, TargetOnline = 10, TickP95BudgetMs = 60, ResumeBelowPct = 80, WindowTicks = 200, ConfirmWindows = 1 };

    private static PopulationDecision Decide(double p95, int running, int owned, int target, PlayerbotPopulationOptions? options = null, int samples = 200,
        int windows = 1)
        => PopulationBudgetController.Decide(samples, p95, running, owned, target, options ?? Options(), windows);

    [Fact]
    public void UnderBudgetAndBelowTarget_AddsUpToTheStartCap()
    {
        PopulationDecision far = Decide(20, 2, 2, 10);
        Assert.Equal((PopulationAction.Add, 4, "under-budget"), (far.Action, far.Count, far.State)); // 8 missing, MaxStartsPerInterval 4
        PopulationDecision near = Decide(20, 8, 8, 10);
        Assert.Equal((PopulationAction.Add, 2, "under-budget"), (near.Action, near.Count, near.State));
        Assert.Equal(PopulationAction.Hold, Decide(20, 10, 10, 10).Action);
        Assert.Equal("at-target", Decide(20, 10, 10, 10).State);
    }

    [Fact]
    public void OverBudget_ParksOwnedBots_AndAddsNothing()
    {
        PopulationDecision many = Decide(75, 6, 4, 10);
        Assert.Equal((PopulationAction.Park, 2, "over-budget"), (many.Action, many.Count, many.State)); // MaxParksPerInterval 2
        PopulationDecision one = Decide(75, 6, 1, 10);
        Assert.Equal((PopulationAction.Park, 1, "over-budget"), (one.Action, one.Count, one.State)); // only one owned bot to give
        Assert.Contains("75.0 ms is over the 60 ms budget", many.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InsideTheHysteresisBand_Holds_NoFlappingOverASequence()
    {
        // The explicit sequence: below the resume line adds, between the resume line and the budget holds, above the budget parks.
        double[] p95 = [20, 47.9, 48, 55, 59.9, 60, 60.1, 55, 48, 47];
        string[] expected = ["Add", "Add", "Hold", "Hold", "Hold", "Hold", "Park", "Hold", "Hold", "Add"];
        Assert.Equal(expected, p95.Select(p => Decide(p, 5, 5, 10).Action.ToString()));

        // A model where every bot costs 2 ms (p95 = 10 + 2 * running): the loop adds until the resume line and then holds for good.
        int running = 0;
        var actions = new List<PopulationAction>();
        PlayerbotPopulationOptions options = Options();
        options.TargetOnline = 100;
        for (int pass = 0; pass < 60; pass++)
        {
            PopulationDecision d = Decide(10 + 2 * running, running, running, 100, options);
            actions.Add(d.Action);
            if (d.Action == PopulationAction.Add) running += d.Count;
            else if (d.Action == PopulationAction.Park) running -= d.Count;
        }

        Assert.DoesNotContain(PopulationAction.Park, actions);
        Assert.Equal(PopulationAction.Hold, actions[^1]);
        Assert.InRange(running, 19, 24); // 10 + 2 * running reached 48 ms
        Assert.Single(actions.SkipWhile(a => a == PopulationAction.Add).Distinct()); // after the last add: holds only
    }

    [Fact]
    public void AfterParking_AddsResumeOnlyBelowTheResumeThreshold()
    {
        Assert.Equal("over-budget", Decide(70, 8, 8, 10).State);
        Assert.Equal("hysteresis", Decide(55, 6, 6, 10).State); // better, but not below 48 ms
        Assert.Equal("hysteresis", Decide(48, 6, 6, 10).State); // the threshold itself does not resume
        Assert.Equal(PopulationAction.Add, Decide(47.9, 6, 6, 10).Action);
    }

    [Fact]
    public void TooFewSamples_HoldsUntilTheWindowRefills()
    {
        PopulationDecision early = Decide(500, 8, 8, 10, samples: 199);
        Assert.Equal((PopulationAction.Hold, "settling"), (early.Action, early.State));
        Assert.Equal(PopulationAction.Hold, Decide(1, 0, 0, 10, samples: 0).Action);
        Assert.Equal(PopulationAction.Park, Decide(500, 8, 8, 10, samples: 200).Action);
    }

    [Fact]
    public void ParkWhenOverBudgetOff_HoldsInsteadOfParking()
    {
        PlayerbotPopulationOptions options = Options();
        options.ParkWhenOverBudget = false;
        PopulationDecision d = Decide(90, 8, 8, 10, options);
        Assert.Equal((PopulationAction.Hold, "over-budget-hold"), (d.Action, d.State));
        Assert.Contains("parking is off", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AboveTarget_ParksTheSurplusEvenUnderBudget()
    {
        Assert.Equal((PopulationAction.Park, 2), Pair(Decide(5, 8, 5, 6))); // surplus 2
        Assert.Equal((PopulationAction.Park, 1), Pair(Decide(5, 7, 5, 6))); // surplus 1
        Assert.Equal((PopulationAction.Park, 2), Pair(Decide(5, 12, 5, 6))); // surplus 6, MaxParksPerInterval 2
        Assert.Equal((PopulationAction.Park, 1), Pair(Decide(5, 12, 1, 6))); // one owned bot to give
        Assert.Equal("over-target", Decide(5, 8, 5, 6).State);
        // No owned bot to give: operator bots are never parked, so the loop only holds.
        Assert.Equal(PopulationAction.Hold, Decide(5, 8, 0, 6).Action);
        Assert.Equal((PopulationAction.Park, 2), Pair(Decide(5, 4, 4, 0))); // a target of 0 parks the owned bots

        static (PopulationAction, int) Pair(PopulationDecision d) => (d.Action, d.Count);
    }

    [Fact]
    public void TargetIsCappedByMaxBots()
    {
        PlayerbotPopulationOptions options = Options();
        options.TargetOnline = 50;
        Assert.Equal(8, PopulationBudgetController.EffectiveTarget(options, 8));
        Assert.Equal(50, PopulationBudgetController.EffectiveTarget(options, 1000));
        Assert.Equal(0, PopulationBudgetController.EffectiveTarget(options, 0));
        PopulationDecision d = Decide(10, 8, 8, PopulationBudgetController.EffectiveTarget(options, 8), options);
        Assert.Equal("at-target", d.State); // 8 running of an effective 8: nothing to add although TargetOnline is 50
    }

    [Fact]
    public void NoOwnedBotsRunning_OverBudgetHoldsAndSaysWhy()
    {
        PopulationDecision d = Decide(90, 8, 0, 10);
        Assert.Equal((PopulationAction.Hold, "over-budget-no-owned"), (d.Action, d.State));
        Assert.Contains("owns no running bot", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmWindows_AParkForTheBudgetNeedsThatManyOverWindows_AndAnAddAsManyUnderTheResumeLine()
    {
        PlayerbotPopulationOptions options = Options();
        options.ConfirmWindows = 2;
        PopulationDecision first = Decide(75, 6, 4, 10, options, windows: 1);
        Assert.Equal((PopulationAction.Hold, "over-budget-confirming"), (first.Action, first.State));
        Assert.Contains("in 1 of the 2 windows a park needs", first.Reason, StringComparison.Ordinal);
        Assert.Equal((PopulationAction.Park, "over-budget"), (Decide(75, 6, 4, 10, options, windows: 2).Action, Decide(75, 6, 4, 10, options, windows: 2).State));

        PopulationDecision quiet = Decide(20, 2, 2, 10, options, windows: 1);
        Assert.Equal((PopulationAction.Hold, "under-budget-confirming"), (quiet.Action, quiet.State));
        Assert.Equal(PopulationAction.Add, Decide(20, 2, 2, 10, options, windows: 2).Action);

        // A lowered target is the operator's limit: honoured at once, without confirmation.
        Assert.Equal((PopulationAction.Park, "over-target"), (Decide(5, 8, 5, 6, options, windows: 1).Action, Decide(5, 8, 5, 6, options, windows: 1).State));
    }

    [Fact]
    public void TheWindowCount_GrowsOnTheSameSide_RestartsOnTheOtherOrBetween_IgnoresSettling_AndResets()
    {
        PlayerbotPopulationOptions options = Options(); // budget 60, resume line 48, 200 ticks
        var windows = new PopulationBudgetWindows();
        Assert.Equal(1, windows.Observe(200, 200, 75, options));
        Assert.Equal(1, windows.Observe(150, 350, 10, options)); // settling: no evidence either way
        Assert.Equal(2, windows.Observe(200, 400, 61, options));
        Assert.Equal(1, windows.Observe(200, 600, 55, options)); // between the resume line and the budget
        Assert.Equal(1, windows.Observe(200, 800, 75, options));
        Assert.Equal(1, windows.Observe(200, 1000, 20, options)); // the other side
        Assert.Equal(2, windows.Observe(200, 1200, 47.9, options));
        windows.Reset();
        Assert.Equal(0, windows.Count);
        Assert.Equal(1, windows.Observe(200, 1400, 47.9, options));
    }

    [Fact]
    public void AWindowSharingTicksWithTheLastCountedOne_IsNotCounted_AndNeverConfirmsTheOtherSide()
    {
        PlayerbotPopulationOptions options = Options(); // budget 60, resume line 48, 200 ticks
        var windows = new PopulationBudgetWindows();
        Assert.Equal(1, windows.Observe(200, 200, 75, options));
        Assert.Equal(1, windows.Observe(300, 300, 75, options)); // 100 new ticks: the same burst, not a second window
        Assert.Equal(1, windows.Observe(399, 399, 75, options)); // 199 new
        Assert.Equal(0, windows.Observe(399, 399, 20, options)); // overlapped and on the other side: no count carried over
        Assert.Equal(1, windows.Observe(400, 400, 75, options)); // 200 new ticks since the counted window; the other-side reading restarted the count
        Assert.Equal(2, windows.Observe(4096, 4600, 75, options)); // a saturated ring still counts by the running total
    }

    [Fact]
    public void ANonFreshReadingOnTheOtherSide_StartsTheCountAgain_SoOverUnderOverDoesNotConfirm()
    {
        PlayerbotPopulationOptions options = Options(); // budget 60, resume line 48, 200 ticks
        var windows = new PopulationBudgetWindows();
        Assert.Equal(1, windows.Observe(200, 200, 75, options));
        Assert.Equal(0, windows.Observe(200, 250, 20, options)); // non-fresh, under the resume line
        Assert.Equal(1, windows.Observe(200, 400, 75, options)); // the next counted window is the first again, not the second
    }

    [Fact]
    public void TheLive20261011Flapping_AddTenParkTwo_IsDampedByTwoWindows()
    {
        // Live w25-r2 (budget 95 ms, resume 76 ms, 10 starts and 2 parks a pass): batches of ten starts every one to four minutes
        // while the running count stayed near 84, i.e. ten added and then taken back two at a time. A single window over the budget
        // after a quiet one (a save burst, a GC pause) no longer parks, and a single quiet window no longer adds ten.
        PlayerbotPopulationOptions options = new() { Enabled = true, TargetOnline = 1000, TickP95BudgetMs = 95, ResumeBelowPct = 80, WindowTicks = 200,
            MaxStartsPerInterval = 10, ConfirmWindows = 2 };
        var windows = new PopulationBudgetWindows();
        double[] p95 = [70, 99, 70, 99, 70, 99];
        string[] states = [.. p95.Select((p, i) => PopulationBudgetController.Decide(200, p, 360, 84, 1000, options, windows.Observe(200, 200L * (i + 1), p, options)).State)];
        Assert.Equal(["under-budget-confirming", "over-budget-confirming", "under-budget-confirming", "over-budget-confirming",
            "under-budget-confirming", "over-budget-confirming"], states);
    }
}
