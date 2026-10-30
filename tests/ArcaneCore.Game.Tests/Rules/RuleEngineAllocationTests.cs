using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using Xunit;
using static ArcaneCore.Game.Tests.Rules.RuleTestKit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// The evaluation path allocates nothing (design 5.1 and 8): after a warm-up, thousands of firings and updates that exercise every check
/// (filter, share, chance, cooldown range, condition, phase, combat) and every shipped action (record, say, cast, set_phase, a started list
/// with a delayed tail) leave this thread's allocated-bytes counter where it was. A benchmark-style test: it counts bytes, not time.
/// </summary>
public sealed class RuleEngineAllocationTests
{
    [Fact]
    public void FireAndUpdate_AllocateNothingPerEvaluation()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules =
            [
                Rule(1) with { FilterId = 1, ChancePct = 60, CooldownMinMs = 5, CooldownMaxMs = 15, ConditionId = Condition },
                Rule(2) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.StableShare), ChancePct = 70, PhaseMask = 0b11 },
                Rule(3) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.OutOfCombatOnly), Priority = 4 },
                Rule(4) with { EventType = ValueBelow, EventParam1 = 50 },
            ],
            Actions =
            [
                RecordOf(1, 1), Action(1, Say, 1, seq: 1, target: Invoker), Action(1, Cast, 133, seq: 2),
                RecordOf(2, 2), Action(2, StartList, 9, seq: 1),
                Action(3, SetPhase, 1), RecordOf(3, 3, seq: 1, delayMs: 3),
                RecordOf(4, 4),
            ],
            Lists = [new RuleListRecord { Id = 9 }],
            ListActions = [RecordOf(9, 90), RecordOf(9, 91, seq: 1, delayMs: 7), Action(9, SetPhase, 0, seq: 2)],
            Filters = [new RuleSubjectFilterRecord { Id = 1, ClassMask = 1, SharePct = 90, ShareSeed = 3 }],
            Texts = [new RuleTextRecord { Id = 1, ContentDefault = "Hello" }],
        });
        Assert.Empty(catalog.Rejected);
        RuleBinding binding = catalog.Bind(Kind);
        var subjects = new (RuleSubjectState State, TestSubject Subject)[16];
        for (int i = 0; i < subjects.Length; i++)
        {
            subjects[i] = (binding.CreateState(), new TestSubject((ulong)(i + 1), new RuleSubjectFacts(1, 2, 30, 2, 1, 14, 0, RuleSubjectFlags.Bot)));
        }

        var invoker = new TestSubject(1000);
        RuleEvaluator evaluator = Evaluator(seed: 17, gate: new FixedGate(true));
        long now = 0;

        void Run(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                now++;
                (RuleSubjectState state, TestSubject subject) = subjects[i % subjects.Length];
                subject.IsInCombat = (i & 8) != 0;
                evaluator.Fire(state, subject, Ping, default, now, invoker);
                evaluator.Fire(state, subject, ValueBelow, new RuleEventArgs(i % 100), now);
                evaluator.Update(state, subject, now);
                subject.Clear();
            }
        }

        Run(5_000);
        int windows = 0;
        long firedBefore = evaluator.Counters.Fired, actionsBefore = evaluator.Counters.ActionsRun;
        void Window() { windows++; Run(20_000); }
        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "20000 rule evaluator iterations");

        // Per window: the probe ran Run(20_000) `windows` times, so scale the original one-window thresholds by that count.
        long fired = evaluator.Counters.Fired - firedBefore, actions = evaluator.Counters.ActionsRun - actionsBefore;
        Assert.True(fired > 20_000L * windows, $"only {fired} rules ran in {windows} windows: the measurement would not cover the path");
        Assert.True(actions > 40_000L * windows, $"only {actions} actions ran in {windows} windows");
    }
}
