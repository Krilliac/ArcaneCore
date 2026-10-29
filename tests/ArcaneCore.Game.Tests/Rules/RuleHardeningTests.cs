using System.Runtime.CompilerServices;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using Xunit;
using static ArcaneCore.Game.Tests.Rules.RuleTestKit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// The core hardening the first host phase owes the engine (design "Notes from P0"): a delayed run keeps its invoker's guid and resolves
/// it again on resume, <see cref="RuleEvaluator.TryFire"/> and <see cref="RuleEvaluator.TryUpdate"/> are the fault-ledger idiom, an action
/// that fires its own event again is refused, and a chain of more than <see cref="RuleEvaluator.MaxListDepth"/> zero-delay lists is
/// rejected at load.
/// </summary>
public sealed class RuleHardeningTests
{
    private const short HookType = 110;
    private const short ThrowType = 111;
    private const short MarkType = 112;

    private static readonly RuleVocabulary Vocab = RuleVocabulary.Discover(
        [.. RuleVocabulary.TypesOf(typeof(RuleVocabulary).Assembly), .. HostTypes, typeof(HookAction), typeof(ThrowAction), typeof(MarkAction)]);

    private static RuleCatalog BuildCatalog(RuleContent content) => RuleCatalog.Build(content, Vocab, new RuleBuildOptions { Conditions = RuleTestKit.Conditions });

    private static RuleEvaluator NewEvaluator(IRuleSubjectResolver? subjects = null)
        => new(new Random(1)) { Subjects = subjects, FaultSite = "rules.test" };

    private static RuleContent InvokerCast(int delayMs) => new()
    {
        Rules = [Rule(1)],
        Actions = [Action(1, Cast, 133, delayMs: delayMs, target: Invoker)],
    };

    [Fact]
    public void ADelayedRun_ResolvesItsInvokerAgain_WhenItResumes()
    {
        RuleCatalog catalog = BuildCatalog(InvokerCast(100));
        var subject = new TestSubject(1);
        var invoker = new TestSubject(2);
        RuleEvaluator evaluator = NewEvaluator(new MapResolver(invoker));
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        evaluator.Fire(state, subject, Ping, default, 0, invoker);
        evaluator.Update(state, subject, 100);

        Assert.Equal(133, subject.LastSpell);
        Assert.Same(invoker, subject.LastTarget);
        Assert.Equal(0, evaluator.Counters.InvokerLost);
    }

    [Fact]
    public void ADelayedRun_ContinuesWithoutItsInvoker_WhenTheInvokerIsGone()
    {
        RuleCatalog catalog = BuildCatalog(new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [Action(1, Cast, 133, delayMs: 100, target: Invoker), RecordOf(1, 5, seq: 1)],
        });
        var subject = new TestSubject(1);
        var invoker = new TestSubject(2);
        RuleEvaluator evaluator = NewEvaluator(new MapResolver());
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        evaluator.Fire(state, subject, Ping, default, 0, invoker);
        evaluator.Update(state, subject, 100);

        Assert.Equal(0, subject.LastSpell);
        Assert.Equal([5], subject.Tags);
        Assert.Equal(1, evaluator.Counters.InvokerLost);
        Assert.Equal(1, evaluator.Counters.ActionsFailed);
    }

    [Fact]
    public void ADelayedRun_WithoutAResolver_TreatsTheInvokerAsGone()
    {
        RuleCatalog catalog = BuildCatalog(InvokerCast(100));
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator();
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        evaluator.Fire(state, subject, Ping, default, 0, new TestSubject(2));
        evaluator.Update(state, subject, 100);

        Assert.Equal(1, evaluator.Counters.InvokerLost);
        Assert.Equal(0, subject.LastSpell);
    }

    [Fact]
    public void ADelayedRun_WhoseInvokerIsTheSubject_NeedsNoResolver()
    {
        RuleCatalog catalog = BuildCatalog(InvokerCast(100));
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator();
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        evaluator.Fire(state, subject, Ping, default, 0, subject);
        evaluator.Update(state, subject, 100);

        Assert.Equal(133, subject.LastSpell);
        Assert.Equal(0, evaluator.Counters.InvokerLost);
    }

    [Fact]
    public void ADelayedRun_DoesNotKeepItsInvokerAlive()
    {
        RuleCatalog catalog = BuildCatalog(InvokerCast(100));
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator(new MapResolver());
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        WeakReference reference = FireWithTransientInvoker(evaluator, state, subject);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(1, state.PendingCount);
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FireWithTransientInvoker(RuleEvaluator evaluator, RuleSubjectState state, TestSubject subject)
    {
        var invoker = new TestSubject(2);
        evaluator.Fire(state, subject, Ping, default, 0, invoker);
        return new WeakReference(invoker);
    }

    [Fact]
    public void AnActionThatFiresItsOwnEventAgain_IsRefusedAndCounted_ButAnotherEventRuns()
    {
        RuleCatalog catalog = BuildCatalog(new RuleContent
        {
            Rules = [Rule(1), Rule(2, ValueBelow) with { EventParam1 = 100 }],
            Actions =
            [
                Action(1, HookType, Ping), Action(1, HookType, ValueBelow, seq: 1), Action(1, MarkType, 1, seq: 2),
                Action(2, MarkType, 2),
            ],
        });
        var subject = new HookSubject(1);
        RuleEvaluator evaluator = NewEvaluator();
        RuleSubjectState state = catalog.Bind(Kind).CreateState();
        var inner = new List<int>();
        subject.OnFire = eventType => inner.Add(evaluator.Fire(state, subject, eventType, default, 0));

        int ran = evaluator.Fire(state, subject, Ping, default, 0);

        Assert.Equal(1, ran);
        Assert.Equal([0, 1], inner);
        Assert.Equal(1, evaluator.Counters.Reentrant);
        Assert.Equal([2, 1], subject.Tags);

        // The guard unwinds: the same event fires normally afterwards.
        subject.Clear();
        subject.OnFire = null;
        Assert.Equal(1, evaluator.Fire(state, subject, Ping, default, 1));
        Assert.Equal(1, evaluator.Counters.Reentrant);
    }

    [Fact]
    public void FiringTheSameEventForAnotherSubject_FromAnAction_IsAllowed()
    {
        RuleCatalog catalog = BuildCatalog(new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [Action(1, HookType, Ping), Action(1, MarkType, 1, seq: 1)],
        });
        var first = new HookSubject(1);
        var second = new HookSubject(2);
        RuleEvaluator evaluator = NewEvaluator();
        RuleBinding binding = catalog.Bind(Kind);
        RuleSubjectState firstState = binding.CreateState();
        RuleSubjectState secondState = binding.CreateState();
        first.OnFire = eventType => evaluator.Fire(secondState, second, eventType, default, 0);

        evaluator.Fire(firstState, first, Ping, default, 0);

        Assert.Equal([1], first.Tags);
        Assert.Equal([1], second.Tags);
        Assert.Equal(0, evaluator.Counters.Reentrant);
    }

    [Fact]
    public void TryFire_CatchesAFault_CountsIt_AndLeavesTheEvaluatorUsable()
    {
        RuleCatalog catalog = BuildCatalog(new RuleContent
        {
            Rules = [Rule(1), Rule(2, ValueBelow) with { EventParam1 = 100 }],
            Actions = [Action(1, ThrowType), RecordOf(2, 2)],
        });
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator();
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        Assert.Equal(0, evaluator.TryFire(state, subject, Ping, default, 0));
        Assert.Equal(0, evaluator.TryFire(state, subject, Ping, default, 1));
        Assert.Equal(2, evaluator.Counters.Faults);
        Assert.Equal(0, evaluator.Counters.Reentrant);
        Assert.Equal(1, evaluator.TryFire(state, subject, ValueBelow, default, 2));
        Assert.Equal([2], subject.Tags);
        Assert.Throws<InvalidOperationException>(() => evaluator.Fire(state, subject, Ping, default, 3));
    }

    [Fact]
    public void TryUpdate_CatchesAFaultInADelayedAction()
    {
        RuleCatalog catalog = BuildCatalog(new RuleContent { Rules = [Rule(1)], Actions = [Action(1, ThrowType, delayMs: 10)] });
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator();
        RuleSubjectState state = catalog.Bind(Kind).CreateState();

        evaluator.Fire(state, subject, Ping, default, 0);
        evaluator.TryUpdate(state, subject, 10);

        Assert.Equal(1, evaluator.Counters.Faults);
        Assert.Equal(0, state.PendingCount);
    }

    private static RuleContent Chain(int lists, bool delayed)
    {
        var listRows = new List<RuleListRecord>();
        var actions = new List<RuleActionRecord>();
        for (int id = 1; id <= lists; id++)
        {
            listRows.Add(new RuleListRecord { Id = id });
            actions.Add(RecordOf(id, id));
            if (id < lists)
            {
                actions.Add(Action(id, StartList, id + 1, seq: 1, delayMs: delayed ? 5 : 0));
            }
        }

        return new RuleContent
        {
            Rules = [Rule(1)],
            Actions = [Action(1, StartList, 1)],
            Lists = listRows,
            ListActions = actions,
        };
    }

    [Fact]
    public void AZeroDelayChainOfEightLists_Loads_AndRunsToTheEnd()
    {
        RuleCatalog catalog = BuildCatalog(Chain(RuleEvaluator.MaxListDepth, delayed: false));
        Assert.Empty(catalog.Rejected);
        var subject = new TestSubject(1);
        RuleEvaluator evaluator = NewEvaluator();

        evaluator.Fire(catalog.Bind(Kind).CreateState(), subject, Ping, default, 0);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], subject.Tags);
        Assert.Equal(0, evaluator.Counters.DepthLimited);
    }

    [Fact]
    public void AZeroDelayChainOfNineLists_IsRejectedAtLoad_WithItsStartingRule()
    {
        RuleCatalog catalog = BuildCatalog(Chain(RuleEvaluator.MaxListDepth + 1, delayed: false));

        RuleRejection list = Assert.Single(catalog.Rejected, r => r.Table == RuleDataTables.List && r.Id == 1);
        Assert.Contains("9 deep", list.Reason);
        Assert.Contains(catalog.Rejected, r => r.Table == RuleDataTables.Rule && r.Id == 1);
        Assert.Equal(0, catalog.RuleCount);
    }

    [Fact]
    public void ALongChainWithADelayInIt_Loads()
    {
        RuleCatalog catalog = BuildCatalog(Chain(14, delayed: true));

        Assert.Empty(catalog.Rejected);
        Assert.Equal(1, catalog.RuleCount);
    }

    private sealed class MapResolver(params IRuleSubject[] subjects) : IRuleSubjectResolver
    {
        public IRuleSubject? Resolve(ulong guid) => subjects.FirstOrDefault(s => s.Guid == guid);
    }

    public sealed class HookSubject(ulong guid) : IRuleSubject
    {
        private readonly List<int> _tags = [];

        public ulong Guid { get; } = guid;

        public bool IsInCombat => false;

        public RuleSubjectFacts Facts => default;

        public Action<short>? OnFire { get; set; }

        public int[] Tags => [.. _tags];

        public void Record(int tag) => _tags.Add(tag);

        public void Clear() => _tags.Clear();
    }

    [RulePrimitive(Kind, HookType, "test.hook", Description = "Calls the subject's hook with the event type to fire.")]
    [RuleParam(1, "event", RuleParamKind.Int)]
    public sealed class HookAction : RuleAction
    {
        public override RuleActionResult Execute(in RuleActionContext context)
        {
            (context.Subject as HookSubject)?.OnFire?.Invoke((short)context.Action.Param(1));
            return RuleActionResult.Done;
        }
    }

    [RulePrimitive(Kind, MarkType, "test.mark", Description = "Records its tag in a hook subject.")]
    [RuleParam(1, "tag", RuleParamKind.Int)]
    public sealed class MarkAction : RuleAction
    {
        public override RuleActionResult Execute(in RuleActionContext context)
        {
            (context.Subject as HookSubject)?.Record(context.Action.Param(1));
            return RuleActionResult.Done;
        }
    }

    [RulePrimitive(Kind, ThrowType, "test.throw", Description = "Throws.")]
    public sealed class ThrowAction : RuleAction
    {
        public override RuleActionResult Execute(in RuleActionContext context) => throw new InvalidOperationException("test fault");
    }
}
