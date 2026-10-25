using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Policy;

public sealed class PlayerbotPolicyEngineTests
{
    private static readonly PlayerbotPolicyGoalMask TownOrExplore =
        default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Explore);

    [Theory]
    [InlineData("missing")]
    [InlineData("not json")]
    [InlineData("wrong features")]
    [InlineData("nan weight")]
    public void ABadModel_IsReportedOnce_AndTheLadderScores(string kind)
    {
        string directory = Directory.CreateTempSubdirectory("policy-model-").FullName;
        try
        {
            string path = Path.Combine(directory, "model.json");
            switch (kind)
            {
                case "not json": File.WriteAllText(path, "{ this is not json"); break;
                case "wrong features": File.WriteAllText(path, LinearModelJson(features: ["level", "money"])); break;
                case "nan weight": File.WriteAllText(path, LinearModelJson().Replace("0.5", "\"NaN\"", StringComparison.Ordinal)); break;
            }

            string key = PlayerbotAnomalies.Key(PlayerbotPolicyEngine.AnomalyArea, "model-load-failed");
            using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Detail.Contains(path, StringComparison.Ordinal));
            var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, ModelPath = path }, contentRoot: directory);

            Assert.Same(RuleBasedPolicy.Instance, engine.Policy);
            Assert.False(engine.ModelLoaded);
            Assert.NotNull(engine.ModelError);
            Assert.Equal(1, capture.Count(key));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AGoodModel_Loads_AndScoresWithItsWeights()
    {
        string directory = Directory.CreateTempSubdirectory("policy-model-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "model.json"), LinearModelJson());
            var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, ModelPath = "model.json" }, contentRoot: directory);
            Assert.True(engine.ModelLoaded, engine.ModelError);

            // The linear model scores goal g as 0.5 * level + g: the last goal (Explore) wins for any level.
            float[] features = new float[PlayerbotPolicySchema.FeatureCount];
            features[0] = 2f;
            float[] scores = new float[PlayerbotPolicySchema.GoalCount];
            engine.Policy.Score(features, 1, scores);
            for (int g = 0; g < scores.Length; g++) Assert.Equal(1f + g, scores[g], 5);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ATwoLayerModel_AppliesReluBetweenLayers()
    {
        // Hidden unit = relu(level - 1); goal g = hidden * (g == Explore ? 1 : 0). Level 0.5 gives a hidden 0; level 3 gives 2.
        int f = PlayerbotPolicySchema.FeatureCount, g = PlayerbotPolicySchema.GoalCount;
        float[] w1 = new float[f];
        w1[0] = 1f;
        float[] w2 = new float[g];
        w2[(int)PlayerbotPolicyGoal.Explore] = 1f;
        string json = ModelJson(PlayerbotPolicySchema.FeatureNames,
            Layer(f, 1, "relu", w1, [-1f]), Layer(1, g, "linear", w2, new float[g]));
        PlayerbotModelPolicy model = PlayerbotModelPolicy.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        float[] features = new float[2 * f];
        features[0] = 0.5f;
        features[f] = 3f;
        float[] scores = new float[2 * g];
        model.Score(features, 2, scores);
        Assert.Equal(0f, scores[(int)PlayerbotPolicyGoal.Explore]);
        Assert.Equal(2f, scores[g + (int)PlayerbotPolicyGoal.Explore], 5);
        Assert.Equal(f + g, model.MultiplyAddsPerBot);
    }

    [Fact]
    public async Task Scoring_RunsOffTheThreadThatDispatches()
    {
        using var gate = new ManualResetEventSlim(false);
        var policy = new StubPolicy(PlayerbotPolicyGoal.Explore) { Gate = gate };
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true }, policy: policy);
        PlayerbotPolicyAgent agent = engine.CreateAgent(1);
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 1000);

        int dispatcher = Environment.CurrentManagedThreadId;
        engine.Dispatch(); // returns while the policy is still blocked: the scoring is not on this thread
        Assert.Equal(PlayerbotPolicySource.Missing, agent.Decide(1000).Source);
        gate.Set();
        await engine.WhenIdleAsync();

        Assert.NotEqual(0, policy.Thread);
        Assert.NotEqual(dispatcher, policy.Thread);
        Assert.Equal(new PlayerbotPolicyVerdict(PlayerbotPolicyGoal.Explore, PlayerbotPolicySource.Applied, PlayerbotPolicyGoal.Explore, agent.Window),
            agent.Decide(1100));
    }

    [Fact]
    public async Task StaleScores_FallBackToTheLadder()
    {
        var options = new PlayerbotPolicyOptions { Enabled = true, StaleAfterMs = 1000 };
        var engine = new PlayerbotPolicyEngine(options, policy: new StubPolicy(PlayerbotPolicyGoal.Explore));
        PlayerbotPolicyAgent agent = engine.CreateAgent(2);
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 10_000);
        engine.Dispatch();
        await engine.WhenIdleAsync();

        Assert.Equal(PlayerbotGoalOrNull(PlayerbotPolicyGoal.Explore), agent.Decide(10_000 + 1000).Promote); // at the bound: still fresh
        PlayerbotPolicyVerdict late = agent.Decide(10_000 + 1001);
        Assert.Null(late.Promote);
        Assert.Equal(PlayerbotPolicySource.Stale, late.Source);
        Assert.Equal(1, engine.Stats.Stale);
    }

    [Fact]
    public async Task ScoresOfTheBotsPreviousThink_AreFresh_PastStaleAfterMs()
    {
        // Live 2026-10-16: with ~1000 bots a bot's out-of-combat thinks were 8-17 s apart (p95), so every think found the snapshot its
        // previous think took older than StaleAfterMs (4.5 s): "agreed" froze and every verdict was stale, and no goal record was written.
        var options = new PlayerbotPolicyOptions { Enabled = true, StaleAfterMs = 4500 };
        var engine = new PlayerbotPolicyEngine(options, policy: new StubPolicy(PlayerbotPolicyGoal.Explore));
        PlayerbotPolicyAgent agent = engine.CreateAgent(4);
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 10_000, think: 7);
        engine.Dispatch();
        await engine.WhenIdleAsync();

        // The very next think, 13 s later: nothing happened to the bot between the two, so the snapshot is the freshest there can be.
        PlayerbotPolicyVerdict next = agent.Decide(10_000 + 13_000, think: 8);
        Assert.Equal(PlayerbotPolicySource.Applied, next.Source);
        Assert.Equal(PlayerbotGoalOrNull(PlayerbotPolicyGoal.Explore), next.Promote);

        // Another think came between (a fight, a rest): the state moved on, and the time bound applies again.
        Assert.Equal(PlayerbotPolicySource.Stale, agent.Decide(10_000 + 13_000, think: 9).Source);
        // Within StaleAfterMs a later think still uses it.
        Assert.Equal(PlayerbotPolicySource.Applied, agent.Decide(10_000 + 4500, think: 9).Source);
        // And the next think past the cap is stale: a bot that did not think for that long is somewhere else.
        Assert.Equal(PlayerbotPolicySource.Stale,
            agent.Decide(10_000 + PlayerbotPolicyAgent.NextThinkStaleAfterMs + 1, think: 8).Source);
        // A caller that does not count thinks keeps the time bound alone.
        Assert.Equal(PlayerbotPolicySource.Stale, agent.Decide(10_000 + 13_000).Source);
    }

    [Fact]
    public async Task MissingAndNonFiniteScores_FallBackToTheLadder()
    {
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true }, policy: new StubPolicy(PlayerbotPolicyGoal.Explore) { NaN = true });
        PlayerbotPolicyAgent agent = engine.CreateAgent(3);
        Assert.Equal(PlayerbotPolicySource.Missing, agent.Decide(0).Source); // no snapshot yet
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 0);
        Assert.Equal(PlayerbotPolicySource.Missing, agent.Decide(0).Source); // not scored yet
        engine.Dispatch();
        await engine.WhenIdleAsync();

        PlayerbotPolicyVerdict verdict = agent.Decide(100);
        Assert.Null(verdict.Promote);
        Assert.Equal(PlayerbotPolicySource.NonFinite, verdict.Source);
        Assert.Equal((2L, 1L), (engine.Stats.Missing, engine.Stats.NonFinite));
    }

    [Fact]
    public async Task TheRuleLadderPolicy_AgreesWithTheLadderForEveryOpenSet()
    {
        // A frozen clock: the scorer's real-time budget must not decide this test on a loaded runner.
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true }, time: new FakeClock());
        Assert.Same(RuleBasedPolicy.Instance, engine.Policy);
        var agents = new List<(PlayerbotPolicyAgent Agent, PlayerbotPolicyGoalMask Open)>();
        for (int bits = 1; bits < 1 << PlayerbotPolicySchema.GoalCount; bits++)
        {
            PlayerbotPolicyAgent agent = engine.CreateAgent((ulong)bits);
            var open = new PlayerbotPolicyGoalMask((byte)bits);
            agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], open, nowMs: 0);
            agents.Add((agent, open));
        }
        engine.Dispatch();
        await engine.WhenIdleAsync();

        foreach ((PlayerbotPolicyAgent agent, PlayerbotPolicyGoalMask open) in agents)
        {
            PlayerbotPolicyVerdict verdict = agent.Decide(10);
            Assert.Null(verdict.Promote);
            Assert.Equal(PlayerbotPolicySource.Agreed, verdict.Source);
            Assert.Equal(open.LadderPick, verdict.Pick);
        }
        Assert.Equal(0, engine.Stats.Applied);
        Assert.Equal(agents.Count, engine.Stats.Agreed);
    }

    [Fact]
    public async Task AModelThatPrefersGrind_LeavesTheLadderInCharge()
    {
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true }, policy: new StubPolicy(PlayerbotPolicyGoal.Grind));
        PlayerbotPolicyAgent agent = engine.CreateAgent(4);
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore.With(PlayerbotPolicyGoal.Grind), nowMs: 0);
        engine.Dispatch();
        await engine.WhenIdleAsync();
        PlayerbotPolicyVerdict verdict = agent.Decide(10);
        Assert.Null(verdict.Promote);
        Assert.Equal(PlayerbotPolicySource.Floor, verdict.Source);
        Assert.Equal((1L, 0L), (engine.Stats.Floor, engine.Stats.Agreed));
    }

    [Fact]
    public async Task ARefusedPromotion_LeavesTheLadderInCharge_ForTheRestOfItsSnapshot()
    {
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true }, policy: new StubPolicy(PlayerbotPolicyGoal.Explore));
        PlayerbotPolicyAgent agent = engine.CreateAgent(5);
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 0);
        engine.Dispatch();
        await engine.WhenIdleAsync();

        Assert.Equal(PlayerbotPolicyGoal.Explore, agent.Decide(10).Promote);
        agent.Refuse();
        Assert.Equal(PlayerbotPolicySource.Refused, agent.LastVerdict?.Source);
        Assert.Null(agent.LastVerdict?.Promote);
        // Later thinks on the same snapshot do not try it again.
        PlayerbotPolicyVerdict next = agent.Decide(20);
        Assert.Null(next.Promote);
        Assert.Equal(PlayerbotPolicySource.Refused, next.Source);

        // A new snapshot may promote again.
        agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 3000);
        engine.Dispatch();
        await engine.WhenIdleAsync();
        Assert.Equal(PlayerbotPolicyGoal.Explore, agent.Decide(3010).Promote);
        Assert.Equal(2, engine.Stats.Refused); // the refusal, and the think after it
    }

    [Fact]
    public async Task ABatchOverItsTickBudget_LeavesTheRestToTheLadder_AndCountsTheOverrun()
    {
        var options = new PlayerbotPolicyOptions { Enabled = true, TickBudgetMs = 0.1 };
        var clock = new FakeClock();
        var engine = new PlayerbotPolicyEngine(options, policy: new StubPolicy(PlayerbotPolicyGoal.Explore) { Clock = clock, AdvanceMs = 5 }, time: clock);
        var agents = Enumerable.Range(1, 100).Select(i => engine.CreateAgent((ulong)i)).ToList();
        foreach (PlayerbotPolicyAgent agent in agents) agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, nowMs: 0);
        engine.Dispatch();
        await engine.WhenIdleAsync();

        PlayerbotPolicyStats stats = engine.Stats;
        // Each 32-row chunk costs 5 ms of fake time against a 0.1 ms budget, so the batch stops after the first chunk.
        Assert.Equal((1L, 32L, 68L), (stats.Overruns, stats.Scored, stats.Unscored));
        int missing = agents.Count(agent => agent.Decide(10).Source == PlayerbotPolicySource.Missing);
        Assert.Equal(stats.Unscored, (long)missing);
    }

    [Fact]
    public void TheAbArms_SplitTheBotsByTheirGuid_AtTheShare()
    {
        Assert.All(Enumerable.Range(1, 1000), i => Assert.Equal(PlayerbotPolicyArm.Policy, PlayerbotPolicyAgent.ArmOf((ulong)i, 1.0)));
        Assert.All(Enumerable.Range(1, 1000), i => Assert.Equal(PlayerbotPolicyArm.Ladder, PlayerbotPolicyAgent.ArmOf((ulong)i, 0.0)));
        // Consecutive guids (as the server hands them out) split near the share, and a bot's arm never changes.
        int policy = Enumerable.Range(1, 10_000).Count(i => PlayerbotPolicyAgent.ArmOf((ulong)i, 0.5) == PlayerbotPolicyArm.Policy);
        Assert.InRange(policy, 4_700, 5_300);
        Assert.Equal(PlayerbotPolicyAgent.ArmOf(42, 0.5), PlayerbotPolicyAgent.ArmOf(42, 0.5));
        Assert.Throws<InvalidOperationException>(() => new PlayerbotPolicyOptions { Enabled = true, AbPolicyShare = 1.5 }.Validate());
    }

    [Fact]
    public async Task TheLadderArm_IsScoredButNeverPromotesOrExplores()
    {
        // Share 0: every bot is in the ladder arm.
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, AbPolicyShare = 0, ExplorationRate = 0.2 },
            policy: new StubPolicy(PlayerbotPolicyGoal.Explore));
        PlayerbotPolicyAgent agent = engine.CreateAgent(7);
        Assert.Equal(PlayerbotPolicyArm.Ladder, agent.Arm);
        for (uint i = 0; i < 50; i++)
        {
            PlayerbotPolicyWindow window = agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], TownOrExplore, i * 10_000);
            Assert.Null(window.Explore);
            engine.Dispatch();
            await engine.WhenIdleAsync();
            PlayerbotPolicyVerdict verdict = agent.Decide(i * 10_000 + 10);
            Assert.Null(verdict.Promote);
            // What the policy would have done is kept for the comparison.
            Assert.Equal((PlayerbotPolicySource.Applied, PlayerbotPolicyGoal.Explore), (verdict.Source, verdict.Pick));
        }
    }

    [Fact]
    public void Exploration_PicksOnlyOpenRunnersUp_AndIsOffByDefault()
    {
        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Quest).With(PlayerbotPolicyGoal.Town)
            .With(PlayerbotPolicyGoal.Grind).With(PlayerbotPolicyGoal.Explore);
        var off = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true });
        PlayerbotPolicyAgent quiet = off.CreateAgent(9);
        for (uint i = 0; i < 500; i++) Assert.Null(quiet.Request(0, new float[PlayerbotPolicySchema.FeatureCount], open, i * 10_000).Explore);

        var on = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, ExplorationRate = 0.1 });
        PlayerbotPolicyAgent agent = on.CreateAgent(9);
        int explored = 0;
        for (uint i = 0; i < 2000; i++)
        {
            PlayerbotPolicyWindow window = agent.Request(0, new float[PlayerbotPolicySchema.FeatureCount], open, i * 10_000);
            if (window.Explore is not { } goal) continue;
            explored++;
            Assert.True(goal is PlayerbotPolicyGoal.Town or PlayerbotPolicyGoal.Explore, goal.ToString()); // never the ladder's Quest, never Grind
            Assert.Equal(PlayerbotPolicySource.Explored, agent.Decide(i * 10_000).Source);
        }
        Assert.InRange(explored, 120, 280); // 10% of 2000, with room for the seeded draw
    }

    [Fact]
    public void Options_AreOffByDefault_AndRefuseTelemetryWithoutTheEngine()
    {
        var options = new PlayerbotOptions();
        Assert.False(options.Policy.Enabled);
        Assert.False(options.Policy.Telemetry.Enabled);
        Assert.Equal(0, options.Policy.ExplorationRate);
        Assert.Equal(string.Empty, options.Policy.ModelPath);
        options.Validate();

        Assert.Throws<InvalidOperationException>(() => new PlayerbotPolicyOptions { Telemetry = { Enabled = true } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotPolicyOptions { ExplorationRate = 0.05 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotPolicyOptions { Enabled = true, ExplorationRate = 0.5 }.Validate());
        new PlayerbotPolicyOptions { Enabled = true, ExplorationRate = 0.1, Telemetry = { Enabled = true } }.Validate();
        // Scores that go stale before the next snapshot would leave a ladder-only gap in every cycle.
        Assert.Throws<InvalidOperationException>(() => new PlayerbotPolicyOptions { Enabled = true, IntervalMs = 5000, StaleAfterMs = 4000 }.Validate());
    }

    private static PlayerbotPolicyGoal? PlayerbotGoalOrNull(PlayerbotPolicyGoal goal) => goal;

    internal static string LinearModelJson(IReadOnlyList<string>? features = null)
    {
        int f = PlayerbotPolicySchema.FeatureCount, g = PlayerbotPolicySchema.GoalCount;
        float[] weights = new float[g * f];
        for (int goal = 0; goal < g; goal++) weights[goal * f] = 0.5f; // 0.5 * level
        return ModelJson(features ?? PlayerbotPolicySchema.FeatureNames, Layer(f, g, "linear", weights, Enumerable.Range(0, g).Select(i => (float)i).ToArray()));
    }

    private static object Layer(int inputs, int outputs, string activation, float[] weights, float[] bias)
        => new { inputs, outputs, activation, weights, bias };

    private static string ModelJson(IReadOnlyList<string> features, params object[] layers) => JsonSerializer.Serialize(new
    {
        format = PlayerbotModelPolicy.Format,
        version = PlayerbotModelPolicy.Version,
        features,
        goals = PlayerbotPolicySchema.GoalNames,
        layers,
    });

    /// <summary>A clock that only moves when told to (1 tick = 1 ms), so the scorer's budget is deterministic.</summary>
    internal sealed class FakeClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void AdvanceMs(long ms) => Interlocked.Add(ref _ms, ms);
    }

    /// <summary>Scores one goal 10 and the others 0 (or NaN), noting the thread it ran on.</summary>
    internal sealed class StubPolicy(PlayerbotPolicyGoal best) : IPlayerbotPolicy
    {
        public ManualResetEventSlim? Gate { get; init; }
        public bool NaN { get; init; }
        public FakeClock? Clock { get; init; }
        public int AdvanceMs { get; init; }
        public int Thread { get; private set; }
        public string Name => "stub";

        public void Score(ReadOnlySpan<float> features, int count, Span<float> scores)
        {
            Thread = Environment.CurrentManagedThreadId;
            if (Gate is { } gate && !gate.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("the test never opened the gate");
            if (Clock is not null && AdvanceMs > 0) Clock.AdvanceMs(AdvanceMs);
            for (int row = 0; row < count; row++)
                for (int goal = 0; goal < PlayerbotPolicySchema.GoalCount; goal++)
                    scores[row * PlayerbotPolicySchema.GoalCount + goal] = NaN ? float.NaN : goal == (int)best ? 10f : 0f;
        }
    }
}
