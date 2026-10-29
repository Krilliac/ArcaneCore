using System.Collections.Frozen;

namespace ArcaneCore.Game.Rules;

/// <summary>A rule as one binding sees it: the rule and its slot in the subject state's cooldown and fired arrays.</summary>
public readonly record struct BoundRule(CompiledRule Rule, int Slot);

/// <summary>
/// The rules that apply to one kind of subject: the host's global rules plus the rules of up to two owners (an entry and a spawn guid,
/// say), merged per event type and sorted by priority, then id. Built at load or bind time by <see cref="RuleCatalog.Bind"/>, cached by
/// the catalog and shared by every subject with the same owners; never built during evaluation. Immutable.
/// </summary>
public sealed class RuleBinding
{
    private readonly FrozenDictionary<short, BoundRule[]> _byEvent;

    internal RuleBinding(RuleCatalog catalog, RuleHostKind host, RuleOwner primary, RuleOwner secondary, IEnumerable<CompiledRule> rules)
    {
        Catalog = catalog;
        Host = host;
        Primary = primary;
        Secondary = secondary;
        CompiledRule[] ordered = [.. rules.Distinct().OrderBy(r => r.Priority).ThenBy(r => r.Id)];
        SlotCount = ordered.Length;
        _byEvent = ordered.Select((rule, slot) => new BoundRule(rule, slot))
            .GroupBy(b => b.Rule.EventType)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray());
    }

    public RuleCatalog Catalog { get; }

    public RuleHostKind Host { get; }

    public RuleOwner Primary { get; }

    public RuleOwner Secondary { get; }

    /// <summary>How many rules the binding holds (the length of a subject state's arrays).</summary>
    public int SlotCount { get; }

    /// <summary>The event types any bound rule listens to.</summary>
    public IEnumerable<short> EventTypes => _byEvent.Keys;

    /// <summary>The rules of <paramref name="eventType"/>, in run order (empty when none listens). Allocation free.</summary>
    public ReadOnlySpan<BoundRule> ForEvent(short eventType)
    {
        if (_byEvent.TryGetValue(eventType, out BoundRule[]? rules))
        {
            return rules;
        }

        return ReadOnlySpan<BoundRule>.Empty;
    }

    /// <summary>A fresh per-subject state for this binding (spawn time).</summary>
    public RuleSubjectState CreateState() => new(this);
}

/// <summary>
/// The per-subject runtime state of the rules of one <see cref="RuleBinding"/>: the current phase, each rule's cooldown and whether a
/// non-repeatable rule has fired, and the delayed action runs still pending. Owned by the host and touched only on its thread. A catalog
/// reload gives the host new bindings, and a subject rebound to one starts from a fresh state.
/// </summary>
public sealed class RuleSubjectState
{
    /// <summary>How many delayed runs (rule action tails and started lists) one subject can have pending at once.</summary>
    public const int PendingCapacity = 4;

    internal readonly long[] CooldownUntil;
    internal readonly bool[] Fired;
    internal readonly PendingRun[] Pending = new PendingRun[PendingCapacity];

    internal RuleSubjectState(RuleBinding binding)
    {
        Binding = binding;
        CooldownUntil = new long[binding.SlotCount];
        Fired = new bool[binding.SlotCount];
    }

    public RuleBinding Binding { get; }

    /// <summary>The current phase, 0..31 (<c>set_phase</c> changes it).</summary>
    public int Phase { get; set; }

    /// <summary>How many delayed runs are pending.</summary>
    public int PendingCount
    {
        get
        {
            int count = 0;
            foreach (ref readonly PendingRun run in Pending.AsSpan())
            {
                if (run.Actions is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>When the rule in <paramref name="slot"/> may fire again (host clock, ms; 0 = now).</summary>
    public long CooldownOf(int slot) => CooldownUntil[slot];

    /// <summary>Back to the spawn state: phase 0, no cooldowns, nothing fired, nothing pending (the host calls it on evade, respawn and similar resets).</summary>
    public void Reset()
    {
        Phase = 0;
        Array.Clear(CooldownUntil);
        Array.Clear(Fired);
        Array.Clear(Pending);
    }
}

/// <summary>A delayed remainder of an action sequence: run <see cref="Actions"/> from <see cref="Next"/> once the clock reaches <see cref="DueMs"/>. It holds the invoker's guid, never the invoker (0 = none); the evaluator resolves it again on resume.</summary>
internal struct PendingRun
{
    public CompiledAction[]? Actions;
    public CompiledRuleList? List;
    public int Next;
    public long DueMs;
    public ulong InvokerGuid;
    public int RuleId;
}
