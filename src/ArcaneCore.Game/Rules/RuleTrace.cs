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

/// <summary>
/// What the evaluator counted since it was created (diagnostics for its host). Written on the host thread only; a host registers them with
/// <see cref="RuleCountersRegistry"/> and <c>.rules stats</c> prints them (a plain 64-bit read from another thread is a snapshot, not a lock).
/// </summary>
public sealed class RuleEvaluatorCounters
{
    /// <summary>Faults <see cref="RuleEvaluator.TryFire"/> and <see cref="RuleEvaluator.TryUpdate"/> caught (the ledger holds the details).</summary>
    public long Faults;

    /// <summary>Firings refused because the same event was already running for the same subject (an action fired it again), or because the nesting cap was hit.</summary>
    public long Reentrant;

    /// <summary>Delayed runs resumed after their invoker was gone (despawned, logged out): they continued without an invoker.</summary>
    public long InvokerLost;

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

    /// <summary>One line with every counter (<c>.rules stats</c>).</summary>
    public string Describe() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"fired={Fired} actions={ActionsRun} failed={ActionsFailed} dropped_runs={DroppedRuns} depth_limited={DepthLimited} reentrant={Reentrant} invoker_lost={InvokerLost} faults={Faults}");
}

/// <summary>
/// The evaluator counters of every running host, by label, for <c>.rules stats</c>. A host with one evaluator per map thread registers each
/// under its own label and unregisters it when the thread goes away. Thread-safe.
/// </summary>
public sealed class RuleCountersRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (RuleHostKind Host, RuleEvaluatorCounters Counters)> _entries = new(StringComparer.Ordinal);

    /// <summary>Register (or replace) the counters of <paramref name="label"/>.</summary>
    public void Register(RuleHostKind host, string label, RuleEvaluatorCounters counters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(counters);
        _entries[label] = (host, counters);
    }

    /// <summary>Remove <paramref name="label"/>; true when it was registered.</summary>
    public bool Unregister(string label) => _entries.TryRemove(label, out _);

    /// <summary>One line per registered evaluator (narrowed to <paramref name="host"/>), ordered by label.</summary>
    public IReadOnlyList<string> Lines(RuleHostKind? host = null)
        => [.. _entries.Where(e => host is null || e.Value.Host == host).OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{RuleHostNames.Of(e.Value.Host)} {e.Key}: {e.Value.Counters.Describe()}")];
}
