using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using Xunit;
using static ArcaneCore.Game.Tests.Rules.RuleTestKit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// The evaluator (design 5.3-5.4): priority order, per-firing chance, stable share, cooldown, phase mask, once-only rules, combat flags,
/// subject filters, conditions, delayed actions and rule lists, and the trace.
/// </summary>
public sealed class RuleEvaluatorTests
{
    private static (RuleSubjectState State, TestSubject Subject) Bound(RuleCatalog catalog, ulong guid = 1, RuleSubjectFacts facts = default)
        => (catalog.Bind(Kind).CreateState(), new TestSubject(guid, facts));

    [Fact]
    public void Rules_RunInPriorityOrder_LowerFirst_TiesById()
    {
        RuleCatalog catalog = Build(Simple(Rule(5) with { Priority = 2 }, Rule(3) with { Priority = 2 }, Rule(9) with { Priority = -1 }, Rule(1) with { Priority = 10 }));
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);

        int ran = Evaluator().Fire(state, subject, Ping, default, 0);

        Assert.Equal(4, ran);
        Assert.Equal([9, 3, 5, 1], subject.Tags);
    }

    [Fact]
    public void Chance_IsAFreshRollPerFiring_OneDrawEach_AndAFullChanceDrawsNothing()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { ChancePct = 30 }, Rule(2)));
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator(seed: 1234);
        var replay = new Random(1234);
        int expected = 0;
        for (int i = 0; i < 200; i++)
        {
            evaluator.Fire(state, subject, Ping, default, i);
            if (replay.Next(100) < 30)
            {
                expected++;
            }
        }

        Assert.Equal(expected + 200, subject.Count);
        Assert.Equal(expected * 1 + 200 * 2, subject.Total);
        Assert.InRange(expected, 40, 80);
    }

    [Fact]
    public void StableShare_SelectsTheSameSubjects_AcrossEvaluatorsAndCatalogs_AndANewSeedReshuffles()
    {
        RuleContent content = new()
        {
            Rules = [Rule(1) with { FilterId = 1 }, Rule(2) with { FilterId = 2 }, Rule(3) with { ChancePct = 30, Flags = (int)(RuleFlags.Repeatable | RuleFlags.StableShare) }],
            Actions = [RecordOf(1, 1), RecordOf(2, 2), RecordOf(3, 3)],
            Filters = [new RuleSubjectFilterRecord { Id = 1, SharePct = 30, ShareSeed = 11 }, new RuleSubjectFilterRecord { Id = 2, SharePct = 30, ShareSeed = 12 }],
        };

        HashSet<ulong> Selected(RuleCatalog catalog, int tag, int seed)
        {
            var selected = new HashSet<ulong>();
            RuleEvaluator evaluator = Evaluator(seed);
            for (ulong guid = 1; guid <= 5_000; guid++)
            {
                (RuleSubjectState state, TestSubject subject) = Bound(catalog, guid);
                evaluator.Fire(state, subject, Ping, default, 0);
                evaluator.Fire(state, subject, Ping, default, 1);
                if (subject.Tags.Contains(tag))
                {
                    Assert.Equal(2, subject.Tags.Count(t => t == tag));
                    selected.Add(guid);
                }
            }

            return selected;
        }

        HashSet<ulong> first = Selected(Build(content), 1, seed: 1);
        HashSet<ulong> restarted = Selected(Build(content), 1, seed: 99);
        HashSet<ulong> reseeded = Selected(Build(content), 2, seed: 1);
        HashSet<ulong> stableChance = Selected(Build(content), 3, seed: 7);

        Assert.InRange(first.Count, 1_350, 1_650);
        Assert.True(first.SetEquals(restarted));
        Assert.InRange(reseeded.Count, 1_350, 1_650);
        Assert.InRange(first.Intersect(reseeded).Count(), 300, 600);
        Assert.InRange(stableChance.Count, 1_350, 1_650);
        Assert.True(stableChance.SetEquals(Selected(Build(content), 3, seed: 8)));
        Assert.All(first, g => Assert.True(RuleShare.Bucket(11, 1, g) < 30));
    }

    [Fact]
    public void Cooldown_BlocksUntilItEnds_AndARangeIsDrawnOnlyWhenTheRuleRuns()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { CooldownMinMs = 1000, CooldownMaxMs = 1000 }, Rule(2) with { CooldownMinMs = 100, CooldownMaxMs = 200 }));
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator(seed: 5);
        var replay = new Random(5);

        evaluator.Fire(state, subject, Ping, default, 0);
        long until = replay.Next(100, 201);
        Assert.Equal(1000, state.CooldownOf(catalog.Bind(Kind).ForEvent(Ping)[0].Slot));
        Assert.Equal(until, state.CooldownOf(1));

        evaluator.Fire(state, subject, Ping, default, until - 1);
        evaluator.Fire(state, subject, Ping, default, until);
        evaluator.Fire(state, subject, Ping, default, 999);
        evaluator.Fire(state, subject, Ping, default, 1000);

        // until-1: both cool down; until: rule 2 again; 999: rule 2 (its second cooldown ended), rule 1 still cools; 1000: rule 1.
        Assert.Equal([1, 2, 2, 2, 1], subject.Tags);
        Assert.InRange(until, 100, 200);
    }

    [Fact]
    public void ARuleWithoutRepeatable_FiresOnce_UntilTheStateIsReset()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { Flags = 0 }));
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator();

        evaluator.Fire(state, subject, Ping, default, 0);
        evaluator.Fire(state, subject, Ping, default, 1);
        state.Reset();
        evaluator.Fire(state, subject, Ping, default, 2);

        Assert.Equal([1, 1], subject.Tags);
    }

    [Fact]
    public void PhaseMask_AndSetPhase_GateRules()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1) with { PhaseMask = 0b01 }, Rule(2) with { PhaseMask = 0b10, Priority = 5 }, Rule(3) with { Priority = 9 }],
            Actions = [RecordOf(1, 1), Action(1, SetPhase, 1, seq: 1), RecordOf(2, 2), RecordOf(3, 3)],
        });
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator();

        evaluator.Fire(state, subject, Ping, default, 0);
        evaluator.Fire(state, subject, Ping, default, 1);

        // Phase 0: rule 1 runs and moves to phase 1, so rule 2 (phase 1 only) runs in the same firing; then only 2 and 3.
        Assert.Equal([1, 2, 3, 2, 3], subject.Tags);
        Assert.Equal(1, state.Phase);
    }

    [Fact]
    public void CombatFlags_Filters_AndEventParameters_GateRules()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules =
            [
                Rule(1) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.CombatOnly) },
                Rule(2) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.OutOfCombatOnly) },
                Rule(3) with { FilterId = 1 },
                Rule(4) with { EventType = ValueBelow, EventParam1 = 50 },
            ],
            Actions = [RecordOf(1, 1), RecordOf(2, 2), RecordOf(3, 3), RecordOf(4, 4)],
            Filters = [new RuleSubjectFilterRecord { Id = 1, ClassMask = 1 << 0, LevelMin = 20, LevelMax = 40, Team = 2, SubjectFlags = (int)RuleSubjectFlags.Bot }],
        });
        RuleEvaluator evaluator = Evaluator();
        var horde = new RuleSubjectFacts(1, 2, 30, 2, 1, 14, 0, RuleSubjectFlags.Bot | RuleSubjectFlags.Player);
        (RuleSubjectState state, TestSubject subject) = Bound(catalog, 1, horde);

        evaluator.Fire(state, subject, Ping, default, 0);
        subject.IsInCombat = true;
        subject.Facts = horde with { Level = 41 };
        evaluator.Fire(state, subject, Ping, default, 0);
        evaluator.Fire(state, subject, ValueBelow, new RuleEventArgs(50), 0);
        evaluator.Fire(state, subject, ValueBelow, new RuleEventArgs(49), 0);

        Assert.Equal([2, 3, 1, 4], subject.Tags);
    }

    [Fact]
    public void Conditions_GateRules_AndAnUndecidableConditionFailsClosed()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { ConditionId = Condition }, Rule(2)));
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        var gate = new FixedGate(true);
        RuleEvaluator evaluator = Evaluator(gate: gate);

        evaluator.Fire(state, subject, Ping, default, 0);
        gate.Answer = false;
        evaluator.Fire(state, subject, Ping, default, 0);
        gate.Answer = null;
        evaluator.Fire(state, subject, Ping, default, 0);
        Evaluator().Fire(state, subject, Ping, default, 0);

        Assert.Equal([1, 2, 2, 2, 2], subject.Tags);
        Assert.Equal(3, gate.Calls);
    }

    [Fact]
    public void DelayedActions_AndRuleLists_RunLater_AndAListRestarts()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [RecordOf(1, 1), RecordOf(1, 2, seq: 1, delayMs: 100), Action(1, StartList, 7, seq: 2)],
            Lists = [new RuleListRecord { Id = 7 }],
            ListActions = [RecordOf(7, 70), RecordOf(7, 71, seq: 1, delayMs: 500), RecordOf(7, 72, seq: 2)],
        });
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator();

        evaluator.Fire(state, subject, Ping, default, 0);
        Assert.Equal([1], subject.Tags);
        evaluator.Update(state, subject, 99);
        Assert.Equal([1], subject.Tags);
        evaluator.Update(state, subject, 100);
        Assert.Equal([1, 2, 70], subject.Tags);
        Assert.Equal(1, state.PendingCount);

        // The rule fires again and its tail restarts list 7 before the list's own tail (due at 600) ran: that tail is dropped.
        evaluator.Fire(state, subject, Ping, default, 120);
        evaluator.Update(state, subject, 220);
        Assert.Equal([1, 2, 70, 1, 2, 70], subject.Tags);
        Assert.Equal(1, state.PendingCount);
        evaluator.Update(state, subject, 719);
        evaluator.Update(state, subject, 720);
        Assert.Equal([1, 2, 70, 1, 2, 70, 71, 72], subject.Tags);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void ARepeatingList_KeepsRepeating_AndAFullPendingTableDropsAndCounts()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1), Rule(2) with { EventType = ValueBelow, EventParam1 = 100 }],
            Actions = [Action(1, StartList, 7), RecordOf(2, 2), RecordOf(2, 3, seq: 1, delayMs: 10)],
            Lists = [new RuleListRecord { Id = 7 }],
            ListActions = [RecordOf(7, 7), Action(7, StartList, 7, seq: 1, delayMs: 10)],
        });
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        RuleEvaluator evaluator = Evaluator();

        evaluator.Fire(state, subject, Ping, default, 0);
        for (int t = 10; t <= 200; t += 10)
        {
            evaluator.Update(state, subject, t);
        }

        Assert.Equal(21, subject.Tags.Count(t => t == 7));
        Assert.Equal(0, evaluator.Counters.DepthLimited);

        for (int i = 0; i < RuleSubjectState.PendingCapacity; i++)
        {
            evaluator.Fire(state, subject, ValueBelow, default, 300);
        }

        Assert.Equal(1, evaluator.Counters.DroppedRuns);
    }

    [Fact]
    public void Say_AndCast_ReachTheSubject_AndTheInvokerTargetIsTheInvoker()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [Action(1, Say, 4, target: Invoker), Action(1, Cast, 133, seq: 1)],
            Texts = [new RuleTextRecord { Id = 4, ContentDefault = "For the Horde!" }],
        });
        (RuleSubjectState state, TestSubject subject) = Bound(catalog);
        var invoker = new TestSubject(2);
        RuleEvaluator evaluator = Evaluator();

        evaluator.Fire(state, subject, Ping, default, 0, invoker);
        Assert.Equal("For the Horde!", subject.LastText!.Content);
        Assert.Equal(133, subject.LastSpell);
        Assert.Same(subject, subject.LastTarget);

        evaluator.Fire(state, subject, Ping, default, 0);
        Assert.Equal(1, evaluator.Counters.ActionsFailed);
    }

    [Fact]
    public void Trace_ReportsTracedSubjects_AndDebugTraceRules_Only()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { ChancePct = 50 }, Rule(2) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.DebugTrace), CooldownMinMs = 10, CooldownMaxMs = 10 }));
        var sink = new ListSink();
        var tracer = new RuleTracer();
        RuleEvaluator evaluator = Evaluator(seed: 3, sink: sink, tracer: tracer);
        (RuleSubjectState state, TestSubject subject) = Bound(catalog, guid: 42);

        evaluator.Fire(state, subject, Ping, default, 0);
        Assert.Equal([2], sink.Entries.Select(e => e.RuleId));

        Assert.True(tracer.Toggle(42));
        evaluator.Fire(state, subject, Ping, default, 5);
        Assert.Equal([2, 1, 2], sink.Entries.Select(e => e.RuleId));
        RuleTraceEntry roll = sink.Entries[1];
        Assert.Equal(42UL, roll.SubjectGuid);
        Assert.InRange(roll.Roll, 0, 99);
        Assert.Equal(roll.Roll < 50 ? RuleOutcome.Ran : RuleOutcome.Chance, roll.Outcome);
        Assert.Equal(RuleOutcome.Cooldown, sink.Entries[2].Outcome);

        Assert.False(tracer.Toggle(42));
        Assert.Equal(0, tracer.Count);
    }
}
