namespace ArcaneCore.Kernel.Rules;

/// <summary>
/// One row of the <c>rule</c> table (docs/superpowers/specs/2026-10-28-rule-engine-design.md, section 4): when event
/// <see cref="EventType"/> fires for a subject of <see cref="Host"/> owned by (<see cref="OwnerKind"/>, <see cref="OwnerId"/>), and the
/// filter, chance, condition, cooldown and phase checks pass, the rule's <c>rule_action</c> rows run in <c>seq</c> order. The numbers are
/// the rule engine's vocabulary ids (<c>docs/reference/rules.md</c>); the loader validates them, this record does not.
/// </summary>
public sealed record RuleRecord
{
    public int Id { get; init; }

    public byte Host { get; init; }

    public byte OwnerKind { get; init; }

    public int OwnerId { get; init; }

    public short EventType { get; init; }

    public int EventParam1 { get; init; }

    public int EventParam2 { get; init; }

    public int EventParam3 { get; init; }

    public int EventParam4 { get; init; }

    /// <summary>The phases (bit n = phase n) the rule fires in; 0 = every phase.</summary>
    public int PhaseMask { get; init; }

    /// <summary><c>rule_subject_filter.id</c>, 0 = none.</summary>
    public int FilterId { get; init; }

    /// <summary>1..100: a fresh roll each firing, or a stable per-subject share with the stable-share flag.</summary>
    public byte ChancePct { get; init; } = 100;

    /// <summary><c>conditions.condition_entry</c>, 0 = none.</summary>
    public int ConditionId { get; init; }

    public int CooldownMinMs { get; init; }

    public int CooldownMaxMs { get; init; }

    /// <summary>Order among the rules of one event of one subject: lower values run first, ties by id.</summary>
    public short Priority { get; init; }

    public int Flags { get; init; }

    public bool Enabled { get; init; } = true;

    public string? Comment { get; init; }
}

/// <summary>
/// One row of <c>rule_action</c> (owned by a rule) or <c>rule_list_action</c> (owned by a rule list): the action, its parameters and its
/// target, run in <see cref="Seq"/> order. <see cref="DelayMs"/> is relative to the previous action of the same sequence.
/// </summary>
public sealed record RuleActionRecord
{
    /// <summary><c>rule.id</c> for <c>rule_action</c>, <c>rule_list.id</c> for <c>rule_list_action</c>.</summary>
    public int OwnerId { get; init; }

    public byte Seq { get; init; }

    public short ActionType { get; init; }

    public int Param1 { get; init; }

    public int Param2 { get; init; }

    public int Param3 { get; init; }

    public int Param4 { get; init; }

    public int Param5 { get; init; }

    public int Param6 { get; init; }

    public short TargetType { get; init; }

    public int TargetParam1 { get; init; }

    public int TargetParam2 { get; init; }

    public int TargetParam3 { get; init; }

    public int DelayMs { get; init; }

    public int Flags { get; init; }
}

/// <summary>One row of <c>rule_list</c>: a named, reusable action list (its actions are <c>rule_list_action</c> rows).</summary>
public sealed record RuleListRecord
{
    public int Id { get; init; }

    public string? Comment { get; init; }
}

/// <summary>One row of <c>rule_subject_filter</c>: narrows which subjects a rule applies to, plus a stable share of them.</summary>
public sealed record RuleSubjectFilterRecord
{
    public int Id { get; init; }

    /// <summary>Bit (class - 1); 0 = any class.</summary>
    public int ClassMask { get; init; }

    /// <summary>Bit (race - 1); 0 = any race.</summary>
    public int RaceMask { get; init; }

    /// <summary>0 = no lower bound.</summary>
    public byte LevelMin { get; init; }

    /// <summary>0 = no upper bound.</summary>
    public byte LevelMax { get; init; }

    /// <summary>0 any, 1 Alliance, 2 Horde.</summary>
    public byte Team { get; init; }

    public int MapId { get; init; }

    public int ZoneId { get; init; }

    public int AreaId { get; init; }

    /// <summary>Subject flags that must all be set (bots only, players only, elite only, ...).</summary>
    public int SubjectFlags { get; init; }

    /// <summary>1..100: the stable share of matching subjects the rule applies to (100 = all).</summary>
    public byte SharePct { get; init; } = 100;

    public int ShareSeed { get; init; }

    public string? Comment { get; init; }
}

/// <summary>One row of <c>rule_text</c>: a text a rule can say (the unified creature text).</summary>
public sealed record RuleTextRecord
{
    public int Id { get; init; }

    public string ContentDefault { get; init; } = string.Empty;

    public int Sound { get; init; }

    public byte Type { get; init; }

    public byte Language { get; init; }

    public int Emote { get; init; }

    public int BroadcastTextId { get; init; }

    public string? Comment { get; init; }
}

/// <summary>One row of <c>playerbot_goal</c>: the bot goal catalogue, keyed by the <see cref="Characters.PlayerbotGoalKind"/> value.</summary>
public sealed record PlayerbotGoalRecord
{
    public short GoalId { get; init; }

    public string Name { get; init; } = string.Empty;

    public short BasePriority { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Which C# goal driver runs the goal (the bot host's driver vocabulary).</summary>
    public short Driver { get; init; }

    public int DriverParam1 { get; init; }

    public int DriverParam2 { get; init; }

    public int DriverParam3 { get; init; }

    public int DriverParam4 { get; init; }

    public string? Comment { get; init; }
}

/// <summary>Every rule table, read together so a reload validates one consistent set.</summary>
public sealed record RuleContent
{
    /// <summary>No rows.</summary>
    public static RuleContent Empty { get; } = new();

    public IReadOnlyList<RuleRecord> Rules { get; init; } = [];

    /// <summary><c>rule_action</c> rows (<see cref="RuleActionRecord.OwnerId"/> is the rule id).</summary>
    public IReadOnlyList<RuleActionRecord> Actions { get; init; } = [];

    public IReadOnlyList<RuleListRecord> Lists { get; init; } = [];

    /// <summary><c>rule_list_action</c> rows (<see cref="RuleActionRecord.OwnerId"/> is the list id).</summary>
    public IReadOnlyList<RuleActionRecord> ListActions { get; init; } = [];

    public IReadOnlyList<RuleSubjectFilterRecord> Filters { get; init; } = [];

    public IReadOnlyList<RuleTextRecord> Texts { get; init; } = [];

    public IReadOnlyList<PlayerbotGoalRecord> Goals { get; init; } = [];

    /// <summary>Rows across every table.</summary>
    public int RowCount => Rules.Count + Actions.Count + Lists.Count + ListActions.Count + Filters.Count + Texts.Count + Goals.Count;
}

/// <summary>Reads the rule tables of the world database (startup and <c>.reload rules</c>, off the world thread).</summary>
public interface IRuleContentStore
{
    /// <summary>Every row of every rule table, in key order.</summary>
    Task<RuleContent> LoadAsync(CancellationToken cancellationToken = default);
}
