using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bots' think cadence at 750 bots (live logs 2026-10-15, Debug build: a think every 9.5 to 16 s per bot, every tick ended on the 8 ms
/// budget with about four thinks): the two think lanes, the adaptive budget, the options, the metrics and the starvation anomaly.
/// The rotation tests run a simulated clock: a think costs a fixed number of milliseconds, a tick is 50 ms.
/// </summary>
public sealed class PlayerbotThinkLanesTests
{
    private const int TickMs = 50;

    /// <summary>Result of a simulated run: the longest wait (ms) between two thinks per bot, and the thinks per tick.</summary>
    private sealed record Run(double[] LongestGapMs, int[] Thinks);

    /// <summary>
    /// <paramref name="bots"/> bots, the ones <paramref name="inCombat"/> names always in the first lane (due every
    /// <paramref name="combatIntervalMs"/>), a think costing <paramref name="thinkCostMs"/> of a <paramref name="budgetMs"/> budget,
    /// the first lane holding <paramref name="share"/> of it, over <paramref name="ticks"/> ticks.
    /// </summary>
    private static Run Simulate(int bots, Func<int, bool> inCombat, double thinkCostMs, double budgetMs, double share, int combatIntervalMs, int ticks, bool lanes = true)
    {
        var rotation = new PlayerbotThinkRotation();
        var last = new double[bots];
        Array.Fill(last, -1);
        var longest = new double[bots];
        var thinks = new int[bots];
        for (int tick = 0; tick < ticks; tick++)
        {
            double now = tick * (double)TickMs;
            double spent = 0;
            Func<int, bool> visit = index =>
            {
                spent += thinkCostMs;
                if (last[index] >= 0) longest[index] = Math.Max(longest[index], now - last[index]);
                last[index] = now;
                thinks[index]++;
                return true;
            };
            if (lanes)
                rotation.TickLanes(bots, budgetMs, share,
                    index => inCombat(index) && (last[index] < 0 || now - last[index] >= combatIntervalMs)
                        ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                    visit, () => spent);
            else
                rotation.TickWithin(bots, budgetMs, visit, () => spent);
        }

        return new Run(longest, thinks);
    }

    [Fact]
    public void BothLanes_KeepTheirFairnessBound_UnderLoad()
    {
        // 300 bots, every third one fighting; a 20 ms budget at 1 ms per think: 14 thinks for the fights, 6 kept for everyone.
        const int bots = 300;
        Run run = Simulate(bots, i => i % 3 == 0, thinkCostMs: 1, budgetMs: 20, share: 0.7, combatIntervalMs: 50, ticks: 400);

        int fighters = bots / 3;
        int fightBound = (fighters + 13) / 14;              // ceil(P / Kp) ticks for a due bot of the first lane
        int mainBound = (bots + 5) / 6;                      // ceil(N / Kmin) ticks for every bot, Kmin = the reserved 6 thinks
        double fightWorst = Enumerable.Range(0, bots).Where(i => i % 3 == 0).Max(i => run.LongestGapMs[i]);
        double quietWorst = Enumerable.Range(0, bots).Where(i => i % 3 != 0).Max(i => run.LongestGapMs[i]);
        Assert.True(fightWorst <= fightBound * TickMs, $"a fighter waited {fightWorst} ms (bound {fightBound * TickMs} ms)");
        Assert.True(quietWorst <= mainBound * TickMs, $"a quiet bot waited {quietWorst} ms (bound {mainBound * TickMs} ms)");
        Assert.All(run.Thinks, count => Assert.True(count > 0, "a bot never thought"));
    }

    [Fact]
    public void CombatBots_ThinkAtTheirInterval_UnderLoad_WhereOneLaneLeavesThemTheWholeRotationWait()
    {
        // 750 bots, 50 of them fighting, 0.5 ms a think and a 12 ms budget: 24 thinks a tick. One lane: a bot waits 750 / 24 = 32 ticks (1.6 s).
        const int bots = 750;
        bool Fighting(int i) => i % 15 == 0;
        Run lanes = Simulate(bots, Fighting, thinkCostMs: 0.5, budgetMs: 12, share: 0.7, combatIntervalMs: 300, ticks: 500);
        Run single = Simulate(bots, Fighting, thinkCostMs: 0.5, budgetMs: 12, share: 0.7, combatIntervalMs: 300, ticks: 500, lanes: false);

        double worstFight = Enumerable.Range(0, bots).Where(Fighting).Max(i => lanes.LongestGapMs[i]);
        double singleFight = Enumerable.Range(0, bots).Where(Fighting).Max(i => single.LongestGapMs[i]);
        Assert.True(worstFight <= 400, $"a fighting bot waited {worstFight} ms (interval 300 ms plus a tick or two)");
        Assert.True(singleFight >= 1500, $"the single lane should leave a fighter waiting a whole rotation, got {singleFight} ms");
    }

    [Fact]
    public void QuietBots_AreNotStarved_ByACrowdOfFights()
    {
        // 600 of 750 bots fighting and always due, 1 ms a think, 10 ms budget with 7 ms for the fights: the quiet 150 still get 3 thinks a tick.
        const int bots = 750;
        Run run = Simulate(bots, i => i % 5 != 0, thinkCostMs: 1, budgetMs: 10, share: 0.7, combatIntervalMs: 50, ticks: 600);

        int mainBound = (bots + 2) / 3; // the reserved 3 thinks a tick walk the main cursor over all 750 in at most 250 ticks
        double quietWorst = Enumerable.Range(0, bots).Where(i => i % 5 == 0).Max(i => run.LongestGapMs[i]);
        Assert.True(quietWorst <= mainBound * TickMs, $"a quiet bot waited {quietWorst} ms (bound {mainBound * TickMs} ms)");
        Assert.All(Enumerable.Range(0, bots).Where(i => i % 5 == 0), i => Assert.True(run.Thinks[i] > 2, $"quiet bot {i} thought {run.Thinks[i]} times"));
    }

    [Fact]
    public void WithRoomForEveryone_EveryBotThinksEveryTick_AsBefore()
    {
        Run run = Simulate(40, i => i % 2 == 0, thinkCostMs: 0.1, budgetMs: 20, share: 0.7, combatIntervalMs: 300, ticks: 50);
        Assert.All(run.Thinks, count => Assert.Equal(50, count));
        Assert.All(run.LongestGapMs, gap => Assert.Equal(TickMs, gap));
    }

    [Fact]
    public void ABotOfTheFirstLane_IsThoughtForOncePerTick_AndAtLeastOneBotAlwaysThinks()
    {
        var rotation = new PlayerbotThinkRotation();
        var seen = new List<int>();
        // Everyone is first lane and the budget is already spent: still exactly one visit per lane pass, never the same bot twice.
        rotation.TickLanes(5, 0, 0.7, _ => PlayerbotThinkRotation.ThinkLane.Priority, i => { seen.Add(i); return true; }, () => 100);
        Assert.Equal([0, 1], seen);
        Assert.True(rotation.LastTickTimeLimited);
        Assert.Equal(1, rotation.LastPriorityVisits);
    }

    [Fact]
    public void AdaptiveBudget_RespectsItsFloorAndCeiling_AndFollowsTheSlack()
    {
        // Slack 50 - 10 = 40 ms, 60%: 24 ms.
        Assert.Equal(24, PlayerbotThinkBudget.Compute(50, 10, 0, 8, 30, 0.6), 6);
        // An idle world is held to the ceiling.
        Assert.Equal(30, PlayerbotThinkBudget.Compute(50, 0, 0, 8, 30, 0.95));
        // A tick the world already filled gets the floor, never zero or negative.
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 60, 0, 8, 30, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 10, 40, 8, 30, 0.6)); // the bots already used the share in motion and intake
        // The bots' own earlier work this tick comes off the budget.
        Assert.Equal(14, PlayerbotThinkBudget.Compute(50, 10, 10, 8, 30, 0.6), 6);
        // Equal floor and ceiling is a fixed budget; a ceiling under the floor is the floor; garbage is the floor.
        Assert.Equal(12, PlayerbotThinkBudget.Compute(50, 0, 0, 12, 12, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 0, 0, 8, 3, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(double.NaN, 0, 0, 8, 30, 0.6));
    }

    [Fact]
    public void TheNonBotTime_IsSmoothed_SoOneSpikeDoesNotSwingTheBudget()
    {
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 20, botMs: 10); // 10 ms that are not the bots'
        Assert.Equal(10, budget.NonBotMs, 6);
        budget.Observe(tickMs: 110, botMs: 10); // an autosave spike: 100 ms
        Assert.Equal(10 + PlayerbotThinkBudget.SmoothingAlpha * 90, budget.NonBotMs, 6);
        double afterSpike = budget.Budget(50, 0, 8, 30, 0.6);
        Assert.InRange(afterSpike, 8, 30);
        budget.Observe(tickMs: 5, botMs: 10); // a tick that reports less than the bots' own: zero, not negative
        Assert.True(budget.NonBotMs >= 0);
    }

    [Fact]
    public void TheDefaults_AreValid_AndLeaveTheOldBudgetAsTheFloor()
    {
        var options = new PlayerbotOptions();
        options.Validate();
        Assert.Equal(PlayerbotThinkRotation.TickBudgetMs, options.ThinkBudgetMinMs);
        Assert.True(options.ThinkBudgetMaxMs > options.ThinkBudgetMinMs);
        Assert.Equal(300, options.CombatThinkIntervalMs);
        Assert.True(options.CombatThinkIntervalMs < 500, "a fight is thought for more often than the 500 ms decision interval");
        Assert.Equal(PlayerbotRecovery.StuckMs, (uint)options.ThinkStarvedGapMs);
    }

    [Theory]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), 0.5)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), 201)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), double.NaN)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), 7)]   // under the default floor of 8
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), 201)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), double.PositiveInfinity)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetTickShare), 0.01)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetTickShare), 1.0)]
    [InlineData(nameof(PlayerbotOptions.CombatThinkIntervalMs), 49)]
    [InlineData(nameof(PlayerbotOptions.CombatThinkIntervalMs), 60_001)]
    [InlineData(nameof(PlayerbotOptions.CombatLaneShare), 0.05)]
    [InlineData(nameof(PlayerbotOptions.CombatLaneShare), 0.95)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), 999)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), -1)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), 3_600_001)]
    public void OutOfRangeThinkOptions_AreRefused(string name, double value)
    {
        var options = new PlayerbotOptions();
        typeof(PlayerbotOptions).GetProperty(name)!.SetValue(options, Convert.ChangeType(value, typeof(PlayerbotOptions).GetProperty(name)!.PropertyType));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThinkOptions_BindFromConfiguration_AndAFixedBudgetAndAnOffSwitchAreValid()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlayerbotOptions.SectionName + ":ThinkBudgetMinMs"] = "12",
            [PlayerbotOptions.SectionName + ":ThinkBudgetMaxMs"] = "12",
            [PlayerbotOptions.SectionName + ":ThinkBudgetTickShare"] = "0.5",
            [PlayerbotOptions.SectionName + ":CombatThinkIntervalMs"] = "400",
            [PlayerbotOptions.SectionName + ":CombatLaneShare"] = "0.5",
            [PlayerbotOptions.SectionName + ":ThinkStarvedGapMs"] = "0",
        }).Build();
        PlayerbotOptions options = PlayerbotOptions.Bind(configuration);
        options.Validate();
        Assert.Equal(12, options.ThinkBudgetMinMs);
        Assert.Equal(12, options.ThinkBudgetMaxMs);
        Assert.Equal(0.5, options.ThinkBudgetTickShare);
        Assert.Equal(400, options.CombatThinkIntervalMs);
        Assert.Equal(0.5, options.CombatLaneShare);
        Assert.Equal(0, options.ThinkStarvedGapMs);
    }

    [Fact]
    public void TheStarvationCheck_FiresOncePerEpisode_AndStaysSilentWhenHealthy()
    {
        bool reported = false;
        // Healthy: every check within the bound.
        for (uint now = 1000; now < 9000; now += 50)
            Assert.False(PlayerbotThinkStarvation.ShouldReport(now, lastThinkMs: now - 400, boundMs: 10_000, ref reported));
        Assert.False(reported);
        // Exactly at the bound is not over it.
        Assert.False(PlayerbotThinkStarvation.ShouldReport(11_000, 1000, 10_000, ref reported));
        // Over it: once, however many ticks follow.
        Assert.True(PlayerbotThinkStarvation.ShouldReport(11_050, 1000, 10_000, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(11_100, 1000, 10_000, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(20_000, 1000, 10_000, ref reported));
        // The next think clears the flag (the feature does it); a new long gap is a new episode.
        reported = false;
        Assert.True(PlayerbotThinkStarvation.ShouldReport(40_000, 25_000, 10_000, ref reported));
        // Off, and a clock that went backwards.
        reported = false;
        Assert.False(PlayerbotThinkStarvation.ShouldReport(40_000, 1, boundMs: 0, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(100, 200, 1000, ref reported));
    }

    [Fact]
    public void TheCadenceReport_CarriesTheLaneSplitTheBudgetAndTheP99_AndTheCommandPrintsThem()
    {
        var stats = new PlayerbotCadenceStats();
        for (int i = 0; i < 90; i++) stats.RecordGap(500);             // quiet bots every 0.5 s
        for (int i = 0; i < 10; i++) stats.RecordGap(300, combat: true); // fighters every 0.3 s
        stats.RecordGap(9_000);
        stats.EndTick(thinks: 30, timeLimited: true, actionLimited: false, budgetMs: 20);
        stats.EndTick(thinks: 30, timeLimited: false, actionLimited: false, budgetMs: 10);
        Assert.Null(stats.Last);
        Assert.True(stats.TryReport(PlayerbotCadenceStats.ReportIntervalMs, bots: 750, out PlayerbotCadenceReport report));

        Assert.Equal(10, report.CombatThinks);
        Assert.Equal(400u, report.CombatGapP95Ms);   // bucket edge of 300 ms
        Assert.Equal(15.0, report.AvgBudgetMs);
        Assert.Equal(50.0, report.TimeLimitedPct);
        Assert.True(report.GapP99Ms >= report.GapP95Ms);
        Assert.Equal(report, stats.Last);

        string[] lines = PlayerbotExtraCommands.CadenceLines(report, 18.5).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains("750 bots", lines[0], StringComparison.Ordinal);
        Assert.Contains("p99", lines[0], StringComparison.Ordinal);
        Assert.Contains("Combat/dead lane: 10 thinks", lines[1], StringComparison.Ordinal);
        Assert.Contains("time budget 50.0%", lines[2], StringComparison.Ordinal);
        Assert.Contains("no full minute", PlayerbotExtraCommands.CadenceLines(null, 8).Single(), StringComparison.Ordinal);
    }

    private static readonly string Starved = PlayerbotAnomalies.Key("cadence", "think-starved");

    /// <summary>A scripted controller whose think burns <paramref name="ms"/> of wall time.</summary>
    private sealed class Burn(double ms) : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        }

        public void Detached(Guid botId) { }
    }

    /// <summary>Three scripted bots named <c>prefix</c> + a/b/c, a <paramref name="boundMs"/> starvation bound and <paramref name="budgetMs"/> as the fixed think budget.</summary>
    private static async Task<(WorldTestHost Host, ManagedPlayerbotFeature Feature, AnomalyCapture Capture, Dictionary<string, int> Thinks)> StartAsync(
        double budgetMs, int boundMs, double burnMs, string prefix)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: o => o.ThinkStarvedGapMs = boundMs);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        feature.ThinkBudgetMs = budgetMs;
        var thinks = new Dictionary<string, int>();
        var names = new Dictionary<Guid, string>();
        feature.ThinkObserver = (id, _) =>
        {
            if (names.TryGetValue(id, out string? name)) thinks[name] = thinks.GetValueOrDefault(name) + 1; // world thread only
        };
        await feature.StartupAsync(default);
        AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Starved && n.Subject.Contains($"bot {prefix}", StringComparison.Ordinal));
        for (int i = 0; i < 3; i++)
        {
            string name = $"{prefix}{(char)('a' + i)}";
            Guid id = Assert.IsType<Guid>((await feature.CreateAsync(name, 1, 1)).BotId);
            names[id] = name;
            Assert.True((await feature.StartScriptedAsync(id.ToString(), new Burn(burnMs))).Success);
        }

        return (host, feature, capture, thinks);
    }

    [Fact]
    public async Task AHealthyServer_ReportsNoStarvedBot()
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 1000, burnMs: 0, "Okbot");
        try
        {
            await Task.Delay(3000); // three bots thought for every tick: no gap near the 1 s bound
            Assert.False(capture.Any(Starved), capture.Describe());
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AStarvedBot_IsReportedOncePerEpisode_NotOncePerTick()
    {
        // A zero budget thinks one bot per tick and each think takes 1.2 s, so each of the three bots waits about 3.6 s between thinks:
        // over the 1 s bound every cycle. The two that sit out a tick are silent for the next tick's whole run: one report each, not one per tick.
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 1000, burnMs: 1200, "Hitbot");
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && capture.Count(Starved) < 6) await Task.Delay(100);
            Assert.True(capture.Count(Starved) >= 6, "no starved bot was reported: " + capture.Describe());
            Dictionary<string, int> thinksNow = await host.World.InvokeAsync(() => new Dictionary<string, int>(thinks));
            Dictionary<string, int> reports = capture.Notices
                .GroupBy(n => n.Subject.Split(' ')[1])
                .ToDictionary(g => g.Key, g => g.Count());
            foreach ((string name, int count) in reports)
                Assert.True(count <= thinksNow.GetValueOrDefault(name) + 1,
                    $"{name}: {count} reports for {thinksNow.GetValueOrDefault(name)} thinks (more than one per episode)");
            Assert.Contains("bound 1000 ms", capture.Notices.First().Detail, StringComparison.Ordinal);
            Assert.Equal(AnomalySeverity.Warning, capture.Notices.First().Severity);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }
}
