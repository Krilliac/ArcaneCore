namespace ArcaneCore.Game.Rules;

/// <summary>
/// Declares a rule vocabulary entry on a <see cref="RuleEvent"/>, <see cref="RuleAction"/> or <see cref="RuleTarget"/> class:
/// its host (<see cref="RuleHostKind.Any"/> for a shared primitive), its id within that host and kind, and its name. The class needs a
/// parameterless constructor; <see cref="RuleVocabulary"/> finds it by reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RulePrimitiveAttribute(RuleHostKind host, short id, string name) : Attribute
{
    public RuleHostKind Host { get; } = host;

    public short Id { get; } = id;

    /// <summary>Lower case with dots and underscores, host-prefixed for host primitives (<c>creature.health_below</c>, <c>set_phase</c>).</summary>
    public string Name { get; } = name;

    /// <summary>One sentence for <c>.rules vocab</c> and docs/reference/rules.md.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// One parameter of a primitive, in order: index 1..6 for actions (<c>param1..6</c>), 1..4 for events (<c>event_param1..4</c>), 1..3 for
/// targets (<c>target_param1..3</c>). Columns past the last declared parameter must be 0.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RuleParamAttribute(int index, string name, RuleParamKind kind) : Attribute
{
    public int Index { get; } = index;

    public string Name { get; } = name;

    public RuleParamKind Kind { get; } = kind;

    public string Description { get; init; } = string.Empty;
}

/// <summary>A declared parameter of a primitive.</summary>
public sealed record RuleParamInfo(int Index, string Name, RuleParamKind Kind, string Description);

/// <summary>A primitive's identity and signature, read from its attributes at discovery.</summary>
public sealed record RulePrimitiveInfo(
    RulePrimitiveKind Kind, RuleHostKind Host, short Id, string Name, string Description, IReadOnlyList<RuleParamInfo> Parameters, Type Type)
{
    /// <summary>False for a primitive a code module added (<see cref="RuleVocabulary.WithModule(IEnumerable{Type})"/>).</summary>
    public bool IsBuiltIn { get; init; } = true;
}

/// <summary>The checks a primitive can add to the generic parameter validation (load time, never during evaluation).</summary>
public readonly ref struct RuleValidationContext(RuleHostDefinition host, ReadOnlySpan<int> parameters)
{
    public RuleHostDefinition Host { get; } = host;

    /// <summary>The primitive's parameter columns, in order (all of them, declared or not).</summary>
    public ReadOnlySpan<int> Parameters { get; } = parameters;
}

/// <summary>The common base of every vocabulary entry. Instances are stateless and shared by every rule and thread.</summary>
public abstract class RulePrimitive
{
    /// <summary>Set once, by <see cref="RuleVocabulary"/> at discovery.</summary>
    public RulePrimitiveInfo Info { get; internal set; } = null!;

    /// <summary>A reason the parameters are invalid beyond what their kinds check, or null. Load time only.</summary>
    public virtual string? Validate(in RuleValidationContext context) => null;
}

/// <summary>
/// An event type. The host fires it with <see cref="RuleEventArgs"/>; <see cref="Matches"/> decides whether one rule's event parameters
/// accept that firing (for example a health threshold), before any other check. Runs on the host's thread and must not allocate.
/// </summary>
public abstract class RuleEvent : RulePrimitive
{
    public virtual bool Matches(CompiledRule rule, in RuleEventArgs args) => true;
}

/// <summary>What one action run did (the trace and the evaluator's counters record it).</summary>
public enum RuleActionResult : byte
{
    Done = 0,

    /// <summary>The target resolved to nothing; the action did nothing.</summary>
    NoTarget = 1,

    /// <summary>The subject or target lacks the capability the action needs.</summary>
    Unsupported = 2,
}

/// <summary>An action type. Runs on the host's thread and must not allocate.</summary>
public abstract class RuleAction : RulePrimitive
{
    /// <summary>The subject capabilities the action needs; a host without them cannot use it (checked at load).</summary>
    public virtual RuleCapabilities Requires => RuleCapabilities.None;

    public abstract RuleActionResult Execute(in RuleActionContext context);
}

/// <summary>A target selector. Runs on the host's thread and must not allocate.</summary>
public abstract class RuleTarget : RulePrimitive
{
    /// <summary>The single target, or null when there is none.</summary>
    public abstract IRuleSubject? Resolve(in RuleActionContext context);
}

/// <summary>
/// A host: a system that owns subjects and fires events (one class each, design 5.2). It declares which owner kinds its rules may use and
/// what its subjects can do; its events are the <see cref="RuleEvent"/> classes declared with its <see cref="Kind"/>.
/// </summary>
public abstract class RuleHostDefinition
{
    public abstract RuleHostKind Kind { get; }

    /// <summary>Lower case, as <c>.rules check</c> and <c>.rules vocab</c> take it (<c>bot</c>, <c>creature</c>, ...).</summary>
    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>The owner kinds rules of this host may use (<see cref="RuleOwnerKind.Global"/> included when it is allowed).</summary>
    public abstract IReadOnlyList<RuleOwnerKind> Owners { get; }

    public abstract RuleCapabilities Capabilities { get; }

    /// <summary>Whether an action may have <c>delay_ms</c> above 0. A host that never calls <see cref="RuleEvaluator.Update"/> (the bot host: its passes are synchronous) returns false, and such an action, or a list with one, is rejected at load.</summary>
    public virtual bool AllowsDelays => true;
}

/// <summary>Everything an action and its target see while running (a stack value; nothing is allocated per action).</summary>
public readonly ref struct RuleActionContext
{
    internal RuleActionContext(RuleEvaluator evaluator, RuleSubjectState state, IRuleSubject subject, IRuleSubject? invoker,
        CompiledAction action, IRuleSubject? target, long nowMs, int depth)
    {
        Evaluator = evaluator;
        State = state;
        Subject = subject;
        Invoker = invoker;
        Action = action;
        Target = target;
        NowMs = nowMs;
        Depth = depth;
    }

    public RuleEvaluator Evaluator { get; }

    public RuleSubjectState State { get; }

    public IRuleSubject Subject { get; }

    /// <summary>Who caused the event (the attacker, the player who used an object, ...), when the event has one.</summary>
    public IRuleSubject? Invoker { get; }

    public CompiledAction Action { get; }

    /// <summary>The resolved target (null while the target itself is resolving).</summary>
    public IRuleSubject? Target { get; }

    /// <summary>The host clock, in milliseconds.</summary>
    public long NowMs { get; }

    /// <summary>How many rule lists deep this run is (a start inside a started list is 2).</summary>
    public int Depth { get; }

    internal RuleActionContext WithTarget(IRuleSubject? target) => new(Evaluator, State, Subject, Invoker, Action, target, NowMs, Depth);
}
