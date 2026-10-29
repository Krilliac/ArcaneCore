namespace ArcaneCore.Game.Rules;

/// <summary>
/// One validated, precompiled action (of a rule or a rule list): the primitive objects are resolved, so running it does no lookup.
/// <see cref="Reference"/> holds the object a reference parameter names (the <see cref="CompiledRuleList"/> of <c>start_rule_list</c>,
/// the <see cref="RuleText"/> of <c>say</c>), resolved at load. Immutable.
/// </summary>
public sealed class CompiledAction
{
    private readonly int[] _params;
    private readonly int[] _targetParams;

    internal CompiledAction(byte seq, RuleAction action, int[] parameters, RuleTarget target, int[] targetParams, int delayMs, int flags)
    {
        Seq = seq;
        Action = action;
        _params = parameters;
        Target = target;
        _targetParams = targetParams;
        DelayMs = delayMs;
        Flags = flags;
    }

    public byte Seq { get; }

    public RuleAction Action { get; }

    public RuleTarget Target { get; }

    /// <summary>Milliseconds after the previous action of the same sequence (or after the start, for the first one).</summary>
    public int DelayMs { get; }

    public int Flags { get; }

    /// <summary>The object the first reference parameter names, resolved at load (null when the action has none).</summary>
    public object? Reference { get; internal set; }

    /// <summary>Action parameter <paramref name="index"/> (1..6).</summary>
    public int Param(int index) => _params[index - 1];

    /// <summary>Target parameter <paramref name="index"/> (1..3).</summary>
    public int TargetParam(int index) => _targetParams[index - 1];
}

/// <summary>A validated <c>rule_list</c> with its actions in <c>seq</c> order. Immutable once the catalog is built.</summary>
public sealed class CompiledRuleList
{
    internal CompiledRuleList(int id, string? comment)
    {
        Id = id;
        Comment = comment;
    }

    public int Id { get; }

    public string? Comment { get; }

    public CompiledAction[] Actions { get; internal set; } = [];
}

/// <summary>A validated <c>rule_subject_filter</c>.</summary>
public sealed record CompiledFilter(
    int Id, uint ClassMask, uint RaceMask, byte LevelMin, byte LevelMax, byte Team, uint MapId, uint ZoneId, uint AreaId,
    RuleSubjectFlags SubjectFlags, byte SharePct, int ShareSeed)
{
    /// <summary>Whether <paramref name="facts"/> pass every column (0 columns match anything). The share is tested separately.</summary>
    public bool Matches(in RuleSubjectFacts facts)
    {
        if (ClassMask != 0 && (facts.Class == 0 || facts.Class > 32 || (ClassMask & (1u << (facts.Class - 1))) == 0))
        {
            return false;
        }

        if (RaceMask != 0 && (facts.Race == 0 || facts.Race > 32 || (RaceMask & (1u << (facts.Race - 1))) == 0))
        {
            return false;
        }

        if ((LevelMin != 0 && facts.Level < LevelMin) || (LevelMax != 0 && facts.Level > LevelMax))
        {
            return false;
        }

        if ((Team != 0 && facts.Team != Team) || (MapId != 0 && facts.MapId != MapId) || (ZoneId != 0 && facts.ZoneId != ZoneId)
            || (AreaId != 0 && facts.AreaId != AreaId))
        {
            return false;
        }

        return (facts.Flags & SubjectFlags) == SubjectFlags;
    }
}

/// <summary>A validated, enabled <c>rule</c> with its actions, ready to evaluate. Immutable.</summary>
public sealed class CompiledRule
{
    private readonly int[] _eventParams;

    internal CompiledRule(int id, RuleHostKind host, RuleOwner owner, RuleEvent eventPrimitive, int[] eventParams, uint phaseMask,
        CompiledFilter? filter, byte chancePct, uint conditionId, int cooldownMinMs, int cooldownMaxMs, short priority, RuleFlags flags,
        CompiledAction[] actions, string? comment)
    {
        Id = id;
        Host = host;
        Owner = owner;
        Event = eventPrimitive;
        _eventParams = eventParams;
        PhaseMask = phaseMask;
        Filter = filter;
        ChancePct = chancePct;
        ConditionId = conditionId;
        CooldownMinMs = cooldownMinMs;
        CooldownMaxMs = cooldownMaxMs;
        Priority = priority;
        Flags = flags;
        Actions = actions;
        Comment = comment;
    }

    public int Id { get; }

    public RuleHostKind Host { get; }

    public RuleOwner Owner { get; }

    public RuleEvent Event { get; }

    /// <summary>The event type id.</summary>
    public short EventType => Event.Info.Id;

    /// <summary>Bit n = phase n; 0 = every phase.</summary>
    public uint PhaseMask { get; }

    public CompiledFilter? Filter { get; }

    public byte ChancePct { get; }

    public uint ConditionId { get; }

    public int CooldownMinMs { get; }

    public int CooldownMaxMs { get; }

    public short Priority { get; }

    public RuleFlags Flags { get; }

    public CompiledAction[] Actions { get; }

    public string? Comment { get; }

    /// <summary>Event parameter <paramref name="index"/> (1..4).</summary>
    public int EventParam(int index) => _eventParams[index - 1];

    internal bool Has(RuleFlags flag) => (Flags & flag) != 0;
}
