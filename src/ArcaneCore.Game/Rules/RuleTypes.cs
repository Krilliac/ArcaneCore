namespace ArcaneCore.Game.Rules;

/// <summary>
/// <c>rule.host</c>: the system that owns a rule's subjects and fires its events (design section 3). <see cref="Any"/> is not a host:
/// it marks a shared primitive that every host whose subjects support it can use.
/// </summary>
public enum RuleHostKind : byte
{
    Any = 0,
    Bot = 1,
    Creature = 2,
    GameObject = 3,
    AreaTrigger = 4,
    WorldEvent = 5,
    Map = 6,
    Economy = 7,
}

/// <summary><c>rule.owner_kind</c>: what a rule is attached to; with <see cref="Global"/> the owner id is 0 and the rule applies to every subject of its host.</summary>
public enum RuleOwnerKind : byte
{
    Global = 0,

    /// <summary>A template entry (creature or game object entry).</summary>
    Entry = 1,

    /// <summary>A spawn guid (the low part).</summary>
    Guid = 2,

    /// <summary>A game event id.</summary>
    GameEvent = 3,

    /// <summary>A bot profile id.</summary>
    Profile = 4,

    /// <summary>An area trigger id.</summary>
    AreaTrigger = 5,

    /// <summary>A map id.</summary>
    Map = 6,
}

/// <summary>The three kinds of vocabulary entry.</summary>
public enum RulePrimitiveKind : byte
{
    Event = 0,
    Action = 1,
    Target = 2,
}

/// <summary><c>rule.flags</c>.</summary>
[Flags]
public enum RuleFlags
{
    None = 0,

    /// <summary>Fires every time it passes; without it a rule fires once per subject state (until the host resets it).</summary>
    Repeatable = 0x01,

    /// <summary>Only while the subject is in combat.</summary>
    CombatOnly = 0x02,

    /// <summary>Only while the subject is out of combat.</summary>
    OutOfCombatOnly = 0x04,

    /// <summary>Every firing of this rule is traced, for every subject (<c>.rules trace</c> traces one subject's rules).</summary>
    DebugTrace = 0x08,

    /// <summary><c>chance_pct</c> is a stable share of subjects (section 5.4) instead of a roll per firing.</summary>
    StableShare = 0x10,
}

/// <summary>Facts about a subject that subject filters test (<c>rule_subject_filter.subject_flags</c> requires all of its bits).</summary>
[Flags]
public enum RuleSubjectFlags
{
    None = 0,
    Player = 0x01,
    Bot = 0x02,
    Creature = 0x04,
    Elite = 0x08,
    GameObject = 0x10,
}

/// <summary>What a host's subjects can do; an action that needs a capability the host lacks is rejected at load.</summary>
[Flags]
public enum RuleCapabilities
{
    None = 0,

    /// <summary>The subject implements <see cref="IRuleSpeaker"/>.</summary>
    Speak = 0x01,

    /// <summary>The subject implements <see cref="IRuleCaster"/>.</summary>
    Cast = 0x02,
}

/// <summary>How the validator checks one primitive parameter.</summary>
public enum RuleParamKind : byte
{
    /// <summary>Any value.</summary>
    Int = 0,

    /// <summary>0 or 1.</summary>
    Bool = 1,

    /// <summary>0..100.</summary>
    Percent = 2,

    /// <summary>0 or more.</summary>
    Milliseconds = 3,

    /// <summary>A phase number, 0..31.</summary>
    Phase = 4,

    /// <summary>A spell id (above 0; checked against the spell store when the host supplies it).</summary>
    SpellId = 5,

    /// <summary>A creature entry (above 0; checked against the creature templates when the host supplies them).</summary>
    CreatureEntry = 6,

    /// <summary>A <c>rule_text.id</c> that must exist.</summary>
    TextId = 7,

    /// <summary>A <c>conditions.condition_entry</c> that must exist.</summary>
    ConditionId = 8,

    /// <summary>A <c>rule_list.id</c> that must exist and load.</summary>
    RuleListId = 9,

    /// <summary>A <c>playerbot_goal.goal_id</c> that must exist.</summary>
    GoalId = 10,

    /// <summary>A delta or value that must not be negative.</summary>
    NonNegative = 11,
}

/// <summary>An owner of rules: (<c>owner_kind</c>, <c>owner_id</c>).</summary>
public readonly record struct RuleOwner(RuleOwnerKind Kind, int Id)
{
    /// <summary>The global owner (every subject of a host).</summary>
    public static RuleOwner Global => default;

    public override string ToString() => Kind == RuleOwnerKind.Global ? "global" : $"{Kind.ToString().ToLowerInvariant()} {Id}";
}

/// <summary>The (up to four) integer arguments a host passes with an event firing.</summary>
public readonly record struct RuleEventArgs(int Value1 = 0, int Value2 = 0, int Value3 = 0, int Value4 = 0);

/// <summary>
/// A row the rule loader did not load: the table, the row id (for an action row, its owner's id; the reason names the seq) and why.
/// <see cref="Host"/> and <see cref="Owner"/> are set for <c>rule</c> and <c>rule_action</c> rows, so <c>.rules check</c> can narrow by them.
/// </summary>
public sealed record RuleRejection(string Table, int Id, string Reason)
{
    public RuleHostKind? Host { get; init; }

    public RuleOwner? Owner { get; init; }

    public override string ToString() => $"{Table} {Id}: {Reason}";
}

/// <summary>The names hosts go by in GM commands and the reference (the design's names), registered or not.</summary>
public static class RuleHostNames
{
    public static string Of(RuleHostKind kind) => kind switch
    {
        RuleHostKind.Any => "shared",
        RuleHostKind.Bot => "bot",
        RuleHostKind.Creature => "creature",
        RuleHostKind.GameObject => "gameobject",
        RuleHostKind.AreaTrigger => "areatrigger",
        RuleHostKind.WorldEvent => "world_event",
        RuleHostKind.Map => "map",
        RuleHostKind.Economy => "economy",
        _ => ((byte)kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The host a name or number names (case-insensitive), or null.</summary>
    public static RuleHostKind? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (byte.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out byte id))
        {
            return (RuleHostKind)id;
        }

        foreach (RuleHostKind kind in Enum.GetValues<RuleHostKind>())
        {
            if (kind != RuleHostKind.Any && Of(kind).Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }
}
