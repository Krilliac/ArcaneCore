using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.WorldEvent;
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

        public bool StartEvent(ushort eventId)
        {
            Log.Add($"start {eventId}");
            return OnStart?.Invoke(eventId) ?? true;
        }

        public bool StopEvent(ushort eventId)
        {
            Log.Add($"stop {eventId}");
            return true;
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

        h.Runner.OnEventChanged(7, active: true, resume: false);
        h.Runner.OnEventChanged(8, active: true, resume: false);

        Assert.Equal(["start 9", "say hello"], h.Control.Log);
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
