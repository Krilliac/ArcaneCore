using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Rules;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// The test host of the rule engine's P0 tests (no real host is wired in P0): host <see cref="Kind"/> with two events, one host action
/// that records into the subject, and subjects that can speak and cast. The vocabulary is the Game assembly's shared primitives plus
/// these types, listed explicitly, so the deliberately broken primitive classes below are never discovered by accident.
/// </summary>
internal static class RuleTestKit
{
    public const RuleHostKind Kind = (RuleHostKind)200;
    public const short Ping = 1;
    public const short ValueBelow = 2;
    public const short Record = 100;
    public const short SetPhase = 1;
    public const short StartList = 2;
    public const short Say = 3;
    public const short Cast = 4;
    public const short Self = 1;
    public const short Invoker = 2;
    public const int Condition = 50;

    public static readonly Type[] HostTypes = [typeof(TestRuleHost), typeof(PingEvent), typeof(ValueBelowEvent), typeof(RecordAction)];

    public static RuleVocabulary Vocabulary { get; } =
        RuleVocabulary.Discover([.. RuleVocabulary.TypesOf(typeof(RuleVocabulary).Assembly), .. HostTypes]);

    public static ConditionTable Conditions { get; } =
        ConditionTable.Build([new ConditionRecord(Condition, (int)ConditionType.ActiveGameEvent, 1, 0, 0, 0, 0)]);

    /// <summary>A repeatable global ping rule of the test host whose one action records <paramref name="id"/>.</summary>
    public static RuleRecord Rule(int id, short eventType = Ping) => new()
    {
        Id = id, Host = (byte)Kind, OwnerKind = (byte)RuleOwnerKind.Global, EventType = eventType, Flags = (int)RuleFlags.Repeatable,
    };

    public static RuleActionRecord RecordOf(int owner, int tag, byte seq = 0, int delayMs = 0)
        => new() { OwnerId = owner, Seq = seq, ActionType = Record, Param1 = tag, TargetType = Self, DelayMs = delayMs };

    public static RuleActionRecord Action(int owner, short type, int param1 = 0, byte seq = 0, int delayMs = 0, short target = Self)
        => new() { OwnerId = owner, Seq = seq, ActionType = type, Param1 = param1, TargetType = target, DelayMs = delayMs };

    /// <summary><paramref name="rules"/>, each recording its own id unless <paramref name="actions"/> are given.</summary>
    public static RuleContent Content(IEnumerable<RuleRecord> rules, IEnumerable<RuleActionRecord>? actions = null) => new()
    {
        Rules = [.. rules],
        Actions = actions is null ? [] : [.. actions],
    };

    public static RuleContent Simple(params RuleRecord[] rules) => new()
    {
        Rules = rules,
        Actions = [.. rules.Select(r => RecordOf(r.Id, r.Id))],
    };

    public static RuleCatalog Build(RuleContent content, RuleBuildOptions? options = null)
        => RuleCatalog.Build(content, Vocabulary, options ?? new RuleBuildOptions { Conditions = Conditions });

    public static RuleEvaluator Evaluator(int seed = 1, IRuleConditionGate? gate = null, IRuleTraceSink? sink = null, RuleTracer? tracer = null)
        => new(new Random(seed)) { Conditions = gate, TraceSink = sink, Tracer = tracer };
}

/// <summary>The test host: global, entry and guid owners; subjects speak and cast.</summary>
public sealed class TestRuleHost : RuleHostDefinition
{
    public override RuleHostKind Kind => RuleTestKit.Kind;

    public override string Name => "test";

    public override string Description => "The rule engine's unit-test host.";

    public override IReadOnlyList<RuleOwnerKind> Owners { get; } = [RuleOwnerKind.Global, RuleOwnerKind.Entry, RuleOwnerKind.Guid];

    public override RuleCapabilities Capabilities => RuleCapabilities.Speak | RuleCapabilities.Cast;
}

[RulePrimitive(RuleTestKit.Kind, RuleTestKit.Ping, "test.ping", Description = "Fires whenever the test fires it.")]
public sealed class PingEvent : RuleEvent;

[RulePrimitive(RuleTestKit.Kind, RuleTestKit.ValueBelow, "test.value_below", Description = "Fires when the firing's value is below the threshold.")]
[RuleParam(1, "threshold", RuleParamKind.Percent)]
public sealed class ValueBelowEvent : RuleEvent
{
    public override bool Matches(CompiledRule rule, in RuleEventArgs args) => args.Value1 < rule.EventParam(1);
}

[RulePrimitive(RuleTestKit.Kind, RuleTestKit.Record, "test.record", Description = "Records its tag in the subject.")]
[RuleParam(1, "tag", RuleParamKind.Int)]
public sealed class RecordAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context)
    {
        if (context.Target is not TestSubject subject)
        {
            return RuleActionResult.Unsupported;
        }

        subject.Record(context.Action.Param(1));
        return RuleActionResult.Done;
    }
}

/// <summary>A second class for test.ping's identity (only discovered when a test lists it).</summary>
[RulePrimitive(RuleTestKit.Kind, RuleTestKit.Ping, "test.ping_again")]
public sealed class DuplicatePingEvent : RuleEvent;

/// <summary>A host action whose id is a shared action's id (only discovered when a test lists it).</summary>
[RulePrimitive(RuleTestKit.Kind, RuleTestKit.SetPhase, "test.shadow")]
public sealed class ShadowingAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context) => RuleActionResult.Done;
}

/// <summary>A module action with a new id.</summary>
[RulePrimitive(RuleTestKit.Kind, 101, "test.module_echo", Description = "Added by a module.")]
public sealed class ModuleEchoAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context) => RuleActionResult.Done;
}

/// <summary>A module action that tries to take a built-in shared id.</summary>
[RulePrimitive(RuleHostKind.Any, RuleTestKit.SetPhase, "set_phase_override")]
public sealed class OverridingSetPhaseAction : RuleAction
{
    public override RuleActionResult Execute(in RuleActionContext context) => RuleActionResult.Done;
}

/// <summary>An event of a host that has no definition.</summary>
[RulePrimitive((RuleHostKind)201, 1, "orphan.event")]
public sealed class OrphanHostEvent : RuleEvent;

/// <summary>A test subject: records what its actions did, without allocating once its buffers are sized.</summary>
public sealed class TestSubject(ulong guid, RuleSubjectFacts facts = default) : IRuleSpeaker, IRuleCaster
{
    private readonly int[] _tags = new int[64];

    public ulong Guid { get; } = guid;

    public bool IsInCombat { get; set; }

    public RuleSubjectFacts Facts { get; set; } = facts;

    public int Count { get; private set; }

    public int Total { get; private set; }

    public int LastSpell { get; private set; }

    public RuleText? LastText { get; private set; }

    public IRuleSubject? LastTarget { get; private set; }

    /// <summary>The recorded tags, in order (the first 64).</summary>
    public int[] Tags => _tags[..Math.Min(Count, _tags.Length)];

    public void Record(int tag)
    {
        if (Count < _tags.Length)
        {
            _tags[Count] = tag;
        }

        Count++;
        Total += tag;
    }

    public void Clear()
    {
        Count = 0;
        Total = 0;
    }

    public void Say(RuleText text, IRuleSubject? target)
    {
        LastText = text;
        LastTarget = target;
    }

    public void Cast(int spellId, int flags, IRuleSubject? target)
    {
        LastSpell = spellId;
        LastTarget = target;
    }
}

/// <summary>A condition gate with a fixed answer that counts its calls.</summary>
public sealed class FixedGate(bool? answer) : IRuleConditionGate
{
    public bool? Answer { get; set; } = answer;

    public int Calls { get; private set; }

    public bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker)
    {
        Calls++;
        return Answer;
    }
}

/// <summary>Collects traced evaluations.</summary>
public sealed class ListSink : IRuleTraceSink
{
    public List<RuleTraceEntry> Entries { get; } = [];

    public void Write(in RuleTraceEntry entry) => Entries.Add(entry);
}
