using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.WorldEvent;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules.WorldEvent;

/// <summary>The <c>world_event</c> rule host (P4): vocabulary, the runner that ties it to game-event starts, stops and ticks, and its fault and re-entrancy boundary.</summary>
public sealed class WorldEventHostTests
{
    private const short SayAction = 3;
    private const short SelfTarget = 1;

    private sealed class Control : IWorldEventControl
    {
        public List<string> Log { get; } = [];

        public Func<ushort, bool>? OnStart { get; set; }

        public Func<ushort, bool>? OnStop { get; set; }

        public bool StartEvent(ushort eventId)
        {
            Log.Add($"start {eventId}");
            return OnStart?.Invoke(eventId) ?? true;
        }

        public bool StopEvent(ushort eventId)
        {
            Log.Add($"stop {eventId}");
            return OnStop?.Invoke(eventId) ?? true;
        }

        public void Announce(string text) => Log.Add($"say {text}");
    }

    private static RuleRecord Rule(int id, short eventType, int ownerId = 7, int p1 = 0, RuleFlags flags = RuleFlags.None, int cooldownMs = 0, bool global = false) => new()
    {
        Id = id,
        Host = (byte)RuleHostKind.WorldEvent,
        OwnerKind = (byte)(global ? RuleOwnerKind.Global : RuleOwnerKind.GameEvent),
        OwnerId = global ? 0 : ownerId,
        EventType = eventType,
        EventParam1 = p1,
        Flags = (int)flags,
        CooldownMinMs = cooldownMs,
        CooldownMaxMs = cooldownMs,
    };

    private static RuleActionRecord Act(int rule, short type, int p1, byte seq = 0, int delayMs = 0)
        => new() { OwnerId = rule, Seq = seq, ActionType = type, Param1 = p1, TargetType = SelfTarget, DelayMs = delayMs };

    private static RuleContent Content(RuleRecord[] rules, RuleActionRecord[] actions) => new()
    {
        Rules = rules,
        Actions = actions,
        Texts = [new RuleTextRecord { Id = 1, ContentDefault = "hello" }, new RuleTextRecord { Id = 2, ContentDefault = "bye" }],
    };

    private sealed class Harness
    {
        public long Now;

        public Control Control { get; } = new();

        public RuleCatalog Catalog { get; set; }

        public WorldEventRuleRunner Runner { get; }

        public Harness(RuleContent content)
        {
            Catalog = RuleCatalog.Build(content, RuleVocabulary.Default);
            Assert.Empty(Catalog.Rejected);
            Runner = new WorldEventRuleRunner(() => Catalog, Control, new RuleEvaluator(new Random(1)), () => Now);
        }
    }

    [Fact]
    public void TheHost_IsRegistered_WithItsEventsAndActions()
    {
        RuleVocabulary vocabulary = RuleVocabulary.Default;

        Assert.Equal("world_event", vocabulary.FindHost(RuleHostKind.WorldEvent)!.Name);
        Assert.Equal(["world_event.started", "world_event.stopped", "world_event.update"],
            vocabulary.For(RuleHostKind.WorldEvent).Where(p => p.Kind == RulePrimitiveKind.Event).Select(p => p.Name));
        Assert.Equal(["set_phase", "start_rule_list", "say", "cast", "world_event.start_event", "world_event.stop_event"],
            vocabulary.For(RuleHostKind.WorldEvent).Where(p => p.Kind == RulePrimitiveKind.Action).Select(p => p.Name));
    }

    [Fact]
    public void AStartRule_RunsOnStart_ForItsEventOnly()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Started)], [Act(1, SayAction, 1)]));

        h.Runner.OnEventChanged(8, active: true, resume: false);
        Assert.Empty(h.Control.Log);
        h.Runner.OnEventChanged(7, active: true, resume: false);

        Assert.Equal(["say hello"], h.Control.Log);
    }

    [Fact]
    public void AGlobalRule_RunsForEveryEvent()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Started, global: true)], [Act(1, SayAction, 1)]));

        h.Runner.OnEventChanged(8, active: true, resume: false);
        h.Runner.OnEventChanged(7, active: true, resume: false);

        Assert.Equal(["say hello", "say hello"], h.Control.Log);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    public void TheStartMode_SeparatesAFreshStartFromAResume(int mode, bool resume, bool fires)
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Started, p1: mode)], [Act(1, SayAction, 1)]));

        h.Runner.OnEventChanged(7, active: true, resume: resume);

        Assert.Equal(fires ? 1 : 0, h.Control.Log.Count);
    }

    [Fact]
    public void AStopRule_RunsOnStop_AndItsDelayedActionsStillComplete()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Stopped)], [Act(1, SayAction, 1), Act(1, SayAction, 2, seq: 1, delayMs: 5000)]));

        h.Runner.OnEventChanged(7, active: true, resume: false);
        h.Runner.OnEventChanged(7, active: false, resume: false);
        Assert.Equal(["say hello"], h.Control.Log);
        Assert.Equal(1, h.Runner.EntryCount);

        h.Now = 4000;
        h.Runner.Update();
        Assert.Equal(["say hello"], h.Control.Log);
        h.Now = 5000;
        h.Runner.Update();

        Assert.Equal(["say hello", "say bye"], h.Control.Log);
        Assert.Equal(0, h.Runner.EntryCount);
    }

    [Fact]
    public void AnUpdateRule_WithACooldown_IsATimer_AndStopsWithTheEvent()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Update, flags: RuleFlags.Repeatable, cooldownMs: 1000)], [Act(1, SayAction, 1)]));

        h.Runner.OnEventChanged(7, active: true, resume: false);
        for (int t = 0; t <= 3000; t += 250)
        {
            h.Now = t;
            h.Runner.Update();
        }

        Assert.Equal(4, h.Control.Log.Count); // t = 0, 1000, 2000, 3000

        h.Runner.OnEventChanged(7, active: false, resume: false);
        h.Now = 9000;
        h.Runner.Update();

        Assert.Equal(4, h.Control.Log.Count);
        Assert.Equal(0, h.Runner.EntryCount);
    }

    [Fact]
    public void AReload_RebindsRunningEvents_ToTheNewRules()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Update, flags: RuleFlags.Repeatable)], [Act(1, SayAction, 1)]));
        h.Runner.OnEventChanged(7, active: true, resume: false);
        h.Runner.Update();
        Assert.Equal(["say hello"], h.Control.Log);

        h.Catalog = RuleCatalog.Build(Content([Rule(1, WorldEventRuleIds.Update, flags: RuleFlags.Repeatable)], [Act(1, SayAction, 2)]), RuleVocabulary.Default);
        h.Runner.Update();

        Assert.Equal(["say hello", "say bye"], h.Control.Log);
    }

    [Fact]
    public void AnActionThatStartsAnotherEvent_ReEntersTheRunner_AndItsRulesRun()
    {
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Started, ownerId: 7), Rule(2, WorldEventRuleIds.Started, ownerId: 9)],
            [Act(1, WorldEventRuleIds.StartEvent, 9), Act(2, SayAction, 1)]));
        h.Control.OnStart = id =>
        {
            h.Runner.OnEventChanged(id, active: true, resume: false);
            return true;
        };

        h.Runner.OnEventChanged(7, active: true, resume: false);

        Assert.Equal(["start 9", "say hello"], h.Control.Log);
    }

    [Fact]
    public void AnEntryReplacedDuringATick_GetsNoExtraUpdate_AndItsDelayedRunIsDropped()
    {
        // Rule 1 (event 7, on its first tick) restarts event 9; rule 2 (event 9, on start) says hello at once and bye after 100 ms.
        // The tick visits the entries in the order they were created (event 7, then event 9: the dictionary has had no removal), so rule 1 runs before event 9's entry is reached; the test relies on that order.
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Update, ownerId: 7), Rule(2, WorldEventRuleIds.Started, ownerId: 9)],
            [Act(1, WorldEventRuleIds.StartEvent, 9), Act(2, SayAction, 1), Act(2, SayAction, 2, seq: 1, delayMs: 100)]));
        h.Control.OnStart = id =>
        {
            h.Runner.OnEventChanged(id, active: true, resume: false);
            return true;
        };
        h.Runner.OnEventChanged(7, active: true, resume: false);
        h.Runner.OnEventChanged(9, active: true, resume: false);
        Assert.Equal(["say hello"], h.Control.Log);

        h.Now = 200; // the first run's "bye" is due, but its entry is replaced by rule 1 earlier in this same tick
        h.Runner.Update();

        Assert.Equal(["say hello", "start 9", "say hello"], h.Control.Log);

        h.Now = 400; // the replacement's own "bye" runs on the next tick, once
        h.Runner.Update();

        Assert.Equal(["say hello", "start 9", "say hello", "say bye"], h.Control.Log);
        Assert.Equal(2, h.Runner.EntryCount);
    }

    [Fact]
    public void AnEntryReplacedByItsOwnUpdateRule_GetsNoDelayedRunInThatTick()
    {
        // Event 7's Update rule restarts event 7 itself. The old entry holds a delayed "bye" that is due; it is replaced before its own update step,
        // so its "bye" must not run in that tick. The replacement's own pending bye is not asserted: the fresh state re-fires rule 1 on the next tick and replaces the entry again.
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Update, ownerId: 7), Rule(2, WorldEventRuleIds.Started, ownerId: 7)],
            [Act(1, WorldEventRuleIds.StartEvent, 7), Act(2, SayAction, 1), Act(2, SayAction, 2, seq: 1, delayMs: 100)]));
        h.Control.OnStart = id =>
        {
            h.Runner.OnEventChanged(id, active: true, resume: false);
            return true;
        };
        h.Runner.OnEventChanged(7, active: true, resume: false);
        Assert.Equal(["say hello"], h.Control.Log);

        h.Now = 200; // the first entry's "bye" is due, but its own Update rule replaces it first
        h.Runner.Update();

        Assert.Equal(["say hello", "start 7", "say hello"], h.Control.Log);
        Assert.Equal(1, h.Runner.EntryCount);
    }

    [Fact]
    public void AnEntryRemovedByItsOwnUpdateRule_IsNotUpdatedOrRemovedAgain()
    {
        // Event 7's Update rule stops event 7: the stop has no delayed action left, so the entry is removed inside the rule. The tick must not
        // touch it afterwards, and later ticks find nothing. A removed entry has Running false and no due run, so this guards double update and
        // double removal only; it cannot tell the two IsCurrent checks apart.
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Update, ownerId: 7), Rule(2, WorldEventRuleIds.Stopped, ownerId: 7)],
            [Act(1, WorldEventRuleIds.StopEvent, 7), Act(2, SayAction, 1)]));
        h.Control.OnStop = id =>
        {
            h.Runner.OnEventChanged(id, active: false, resume: false);
            return true;
        };
        h.Runner.OnEventChanged(7, active: true, resume: false);

        h.Now = 100;
        h.Runner.Update();

        Assert.Equal(["stop 7", "say hello"], h.Control.Log);
        Assert.Equal(0, h.Runner.EntryCount);

        h.Now = 200;
        h.Runner.Update();

        Assert.Equal(["stop 7", "say hello"], h.Control.Log);
    }

    [Fact]
    public void AnUpdateRuleThatStopsItsEvent_StillRunsItsLaterDelayedAction()
    {
        // Event 7's Update rule stops event 7 with its first action and has a delayed second action. The stop must not remove the entry while the
        // rule is still running, or the delayed action would park into an orphaned state and never run.
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Update, ownerId: 7)],
            [Act(1, WorldEventRuleIds.StopEvent, 7), Act(1, SayAction, 1, seq: 1, delayMs: 100)]));
        h.Control.OnStop = id =>
        {
            h.Runner.OnEventChanged(id, active: false, resume: false);
            return true;
        };
        h.Runner.OnEventChanged(7, active: true, resume: false);

        h.Now = 100;
        h.Runner.Update();

        Assert.Equal(["stop 7"], h.Control.Log);
        Assert.Equal(1, h.Runner.EntryCount);

        h.Now = 250;
        h.Runner.Update();

        Assert.Equal(["stop 7", "say hello"], h.Control.Log);
        Assert.Equal(0, h.Runner.EntryCount);
    }

    [Fact]
    public void AnEventThatRestartsItself_IsCutAtTheNestingLimit()
    {
        var h = new Harness(Content([Rule(1, WorldEventRuleIds.Started, global: true, flags: RuleFlags.Repeatable)], [Act(1, WorldEventRuleIds.StartEvent, 7)]));
        h.Control.OnStart = id =>
        {
            h.Runner.OnEventChanged(id, active: true, resume: false);
            return true;
        };

        h.Runner.OnEventChanged(7, active: true, resume: false);

        Assert.Equal(WorldEventRuleRunner.MaxNesting, h.Control.Log.Count);
        Assert.Equal(1, h.Runner.NestingSkips);
    }

    [Fact]
    public void AThrowingAction_IsContained_AndTheNextEventStillRuns()
    {
        var h = new Harness(Content(
            [Rule(1, WorldEventRuleIds.Started, ownerId: 7), Rule(2, WorldEventRuleIds.Started, ownerId: 8)],
            [Act(1, WorldEventRuleIds.StartEvent, 9), Act(2, SayAction, 1)]));
        h.Control.OnStart = _ => throw new InvalidOperationException("boom");
        long before = FaultLedger.Shared.Snapshot().Where(f => f.Site == "world-event.rules").Sum(f => f.Count);

        h.Runner.OnEventChanged(7, active: true, resume: false);
        h.Runner.OnEventChanged(8, active: true, resume: false);

        Assert.Equal(["start 9", "say hello"], h.Control.Log);
        // The harness passes no logger, so the fault goes to the shared ledger (the runner's null-logger branch): exactly the one throwing action is recorded.
        Assert.Equal(before + 1, FaultLedger.Shared.Snapshot().Where(f => f.Site == "world-event.rules").Sum(f => f.Count));
    }

    /// <summary>A control that behaves like <c>GameEventService</c>: it refuses to start a running event, ignores a stop of a stopped one, and tells the runner.</summary>
    private sealed class ServiceLikeControl : IWorldEventControl
    {
        public HashSet<ushort> Active { get; } = [];

        public List<string> Log { get; } = [];

        public WorldEventRuleRunner? Runner { get; set; }

        public bool StartEvent(ushort eventId)
        {
            if (Active.Add(eventId))
            {
                Runner!.OnEventChanged(eventId, active: true, resume: false);
            }

            return true;
        }

        public bool StopEvent(ushort eventId)
        {
            if (Active.Remove(eventId))
            {
                Runner!.OnEventChanged(eventId, active: false, resume: false);
            }

            return true;
        }

        public void Announce(string text) => Log.Add(text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStartStopPingPong_CutAtTheNestingLimit_LeavesTheEntriesAgreeingWithTheGameEvents(bool startFirst)
    {
        var control = new ServiceLikeControl();
        RuleCatalog catalog = RuleCatalog.Build(Content(
            [Rule(1, WorldEventRuleIds.Started, global: true, flags: RuleFlags.Repeatable), Rule(2, WorldEventRuleIds.Stopped, global: true, flags: RuleFlags.Repeatable),
             Rule(3, WorldEventRuleIds.Update, global: true, flags: RuleFlags.Repeatable)],
            [Act(1, WorldEventRuleIds.StopEvent, 7), Act(2, WorldEventRuleIds.StartEvent, 7), Act(3, SayAction, 1)]), RuleVocabulary.Default);
        Assert.Empty(catalog.Rejected);
        long now = 0;
        var runner = new WorldEventRuleRunner(() => catalog, control, new RuleEvaluator(new Random(1)), () => now);
        control.Runner = runner;
        if (!startFirst)
        {
            control.Active.Add(7);
            runner.OnEventChanged(7, active: true, resume: true); // running before, resumed: its start rule stops it and so on
        }
        else
        {
            control.StartEvent(7);
        }

        Assert.True(runner.NestingSkips > 0);
        control.Log.Clear();
        runner.Update();

        bool running = control.Active.Contains(7);
        Assert.Equal(running ? 1 : 0, runner.EntryCount);
        Assert.Equal(running ? ["hello"] : [], control.Log);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void AStartOrStopActionWithoutAGameEventNumber_IsRejected(int eventId)
    {
        RuleCatalog catalog = RuleCatalog.Build(Content([Rule(1, WorldEventRuleIds.Started)], [Act(1, WorldEventRuleIds.StopEvent, eventId)]), RuleVocabulary.Default);

        Assert.Contains(catalog.Rejected, r => r.Reason.Contains("game event number", StringComparison.Ordinal));
    }

    [Fact]
    public void AStartRuleWithAnUnknownMode_IsRejected()
    {
        RuleCatalog catalog = RuleCatalog.Build(Content([Rule(1, WorldEventRuleIds.Started, p1: 3)], [Act(1, SayAction, 1)]), RuleVocabulary.Default);

        Assert.Contains(catalog.Rejected, r => r.Reason.Contains("mode must be", StringComparison.Ordinal));
    }

    [Fact]
    public void ARuleWithAnOwnerKindTheHostDoesNotUse_IsRejected()
    {
        RuleRecord bad = Rule(1, WorldEventRuleIds.Started) with { OwnerKind = (byte)RuleOwnerKind.Profile };
        RuleCatalog catalog = RuleCatalog.Build(Content([bad], [Act(1, SayAction, 1)]), RuleVocabulary.Default);

        Assert.Single(catalog.Rejected);
    }
}
