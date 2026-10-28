namespace ArcaneCore.Game.Rules;

/// <summary>How one evaluation of one rule ended (the order is the evaluator's check order).</summary>
public enum RuleOutcome : byte
{
    /// <summary>The checks passed and the actions ran.</summary>
    Ran = 0,

    /// <summary>The event primitive did not accept this firing's arguments.</summary>
    EventMismatch,

    /// <summary>The subject's phase is not in the rule's phase mask.</summary>
    Phase,

    /// <summary>The combat-only or out-of-combat-only flag did not match.</summary>
    Combat,

    /// <summary>A non-repeatable rule that already fired.</summary>
    AlreadyFired,

    /// <summary>The rule is cooling down.</summary>
    Cooldown,

    /// <summary>The subject filter's columns did not match.</summary>
    Filter,

    /// <summary>The subject is outside the filter's (or the stable-share rule's) share.</summary>
    Share,

    /// <summary>The per-firing chance roll failed.</summary>
    Chance,

    /// <summary>The condition did not hold (or could not be decided, which fails closed).</summary>
    Condition,
}

/// <summary>One traced rule evaluation. <see cref="Roll"/> is the 0..99 roll or share bucket, -1 when none was drawn.</summary>
public readonly record struct RuleTraceEntry(
    long NowMs, ulong SubjectGuid, int RuleId, short EventType, RuleOutcome Outcome, int Roll, bool? ConditionResult, int ActionsRun, int ActionsFailed);

/// <summary>Receives traced evaluations (<see cref="RuleFlags.DebugTrace"/> rules and <c>.rules trace</c> subjects). Off the hot path: only traced firings reach it.</summary>
public interface IRuleTraceSink
{
    void Write(in RuleTraceEntry entry);
}

/// <summary>
/// The subjects whose every rule evaluation is traced (<c>.rules trace &lt;guid&gt;</c>). Thread-safe; the evaluator reads
/// <see cref="Count"/> first, so with nobody traced a firing costs one volatile read.
/// </summary>
public sealed class RuleTracer
{
    private readonly Lock _lock = new();
    private volatile HashSet<ulong> _guids = [];

    public int Count => _guids.Count;

    public IReadOnlyCollection<ulong> Guids => _guids;

    public bool IsTraced(ulong guid) => _guids.Contains(guid);

    /// <summary>Start tracing <paramref name="guid"/>, or stop when it is traced already. True when it is traced afterwards.</summary>
    public bool Toggle(ulong guid)
    {
        lock (_lock)
        {
            var next = new HashSet<ulong>(_guids);
            bool traced = next.Add(guid) || !next.Remove(guid);
            _guids = next;
            return traced;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _guids = [];
        }
    }
}

/// <summary>What the evaluator counted since it was created (diagnostics; <c>.rules check</c> prints them for the world's evaluators).</summary>
public sealed class RuleEvaluatorCounters
{
    /// <summary>Rule firings whose actions ran.</summary>
    public long Fired;

    /// <summary>Actions that ran and returned <see cref="RuleActionResult.Done"/>.</summary>
    public long ActionsRun;

    /// <summary>Actions that found no target or a subject without the capability.</summary>
    public long ActionsFailed;

    /// <summary>Delayed runs refused because the subject already had <see cref="RuleSubjectState.PendingCapacity"/> pending.</summary>
    public long DroppedRuns;

    /// <summary>List starts refused at the nesting limit.</summary>
    public long DepthLimited;
}
