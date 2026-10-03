using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Conditions;

/// <summary>One validated row of the conditions table.</summary>
public sealed record ConditionEntry(
    uint Entry,
    ConditionType Type,
    uint Value1,
    uint Value2,
    uint Value3,
    uint Value4,
    ConditionFlags Flags);

/// <summary>A row the loader dropped, as cmangos ObjectMgr::LoadConditions erases an invalid condition (ObjectMgr.cpp:5526-5543).</summary>
public sealed record ConditionRejection(uint Entry, int Type, string Reason);

/// <summary>Limits the structural validation needs; the defaults are the classic-db / cmangos classic values.</summary>
public sealed record ConditionTableOptions
{
    /// <summary>CONFIG_UINT32_MAX_PLAYER_LEVEL (the Level condition's upper bound).</summary>
    public uint MaxPlayerLevel { get; init; } = 60;

    /// <summary>sWorld.GetConfigMaxSkillValue (the skill conditions' upper bound).</summary>
    public uint MaxSkillValue { get; init; } = 300;
}

/// <summary>
/// The loaded <c>conditions</c> table: cmangos <c>sConditionStorage</c> after
/// <c>ObjectMgr::LoadConditions</c> (D:\refs\mangos-classic\src\game\Globals\ObjectMgr.cpp:5526-5543)
/// erased every row <c>ConditionEntry::IsValid</c> refused (Conditions.cpp:600-1000). Rows are
/// validated in ascending entry order, so an AND/OR/NOT may only reference a lower, still-valid
/// entry, which also makes evaluation recursion finite. Validation that needs other content
/// (an item, spell, quest, faction, skill or game event existing) is not applied here: a reference
/// to missing content evaluates as that content's absence.
/// Immutable; safe to share.
/// </summary>
public sealed class ConditionTable
{
    private const uint Alliance = 469;
    private const uint Horde = 67;

    // RACEMASK_ALL_PLAYABLE / CLASSMASK_ALL_PLAYABLE (SharedDefines.h:52-57, 91-96).
    private const uint RaceMaskAllPlayable = 0xFF;
    private const uint ClassMaskAllPlayable = 0x5DF;
    private const int MaxReputationRank = 8;

    private readonly Dictionary<uint, ConditionEntry> _entries;

    private ConditionTable(Dictionary<uint, ConditionEntry> entries, List<ConditionRejection> rejected)
    {
        _entries = entries;
        Rejected = rejected;
    }

    /// <summary>No conditions: every conditioned option stays hidden (what a missing row means in cmangos IsConditionSatisfied, Conditions.cpp:1023-1028).</summary>
    public static ConditionTable Empty { get; } = new([], []);

    public int Count => _entries.Count;

    public IReadOnlyList<ConditionRejection> Rejected { get; }

    public IEnumerable<ConditionEntry> Entries => _entries.Values;

    public ConditionEntry? Find(uint id) => _entries.GetValueOrDefault(id);

    /// <summary>Validate and index <paramref name="records"/> (cmangos LoadConditions + ConditionEntry::IsValid).</summary>
    public static ConditionTable Build(IEnumerable<ConditionRecord> records, ConditionTableOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        options ??= new ConditionTableOptions();
        var accepted = new Dictionary<uint, ConditionEntry>();
        var rejected = new List<ConditionRejection>();
        var seen = new HashSet<uint>();
        foreach (ConditionRecord record in records.OrderBy(r => r.Entry))
        {
            if (!seen.Add(record.Entry))
            {
                rejected.Add(new(record.Entry, record.Type, "duplicate condition_entry (the first row is kept)"));
                continue;
            }

            string? problem = Validate(record, accepted, options);
            if (problem is not null)
            {
                rejected.Add(new(record.Entry, record.Type, problem));
                continue;
            }

            accepted[record.Entry] = new ConditionEntry(record.Entry, (ConditionType)record.Type, record.Value1, record.Value2,
                record.Value3, record.Value4, (ConditionFlags)record.Flags);
        }

        return new ConditionTable(accepted, rejected);
    }

    /// <summary>Whether <paramref name="type"/> is a type id the cmangos classic enum defines and IsValid accepts (Conditions.h:30-82, Conditions.cpp:969-998).</summary>
    public static bool IsKnownType(int type) => type switch
    {
        -3 or -2 or -1 or 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 17 or 18 or 19
            or 22 or 23 or 26 or 28 or 29 or 30 or 31 or 33 or 35 or 36 or 37 or 38 or 39 or 40 or 42 or 43 => true,
        _ => false,
    };

    private static string? Validate(ConditionRecord r, Dictionary<uint, ConditionEntry> accepted, ConditionTableOptions o)
    {
        if (!IsKnownType(r.Type))
        {
            return $"bad type {r.Type}";
        }

        if (r.Flags > (byte)(ConditionFlags.ReverseResult | ConditionFlags.SwapTargets))
        {
            return $"unknown flags {r.Flags}";
        }

        switch ((ConditionType)r.Type)
        {
            case ConditionType.Not:
                return CheckReference(r, r.Value1, accepted);
            case ConditionType.And or ConditionType.Or:
                return CheckReference(r, r.Value1, accepted) ?? CheckReference(r, r.Value2, accepted)
                    ?? (r.Value3 != 0 ? CheckReference(r, r.Value3, accepted) : null)
                    ?? (r.Value4 != 0 ? CheckReference(r, r.Value4, accepted) : null);
            case ConditionType.Item or ConditionType.ItemWithBank:
                return r.Value2 < 1 ? "item condition useless with count < 1" : null;
            case ConditionType.AreaId:
                return r.Value2 > 1 ? $"area mode {r.Value2} must be 0..1" : null;
            case ConditionType.ReputationRankMin or ConditionType.ReputationRankMax:
                return r.Value2 >= MaxReputationRank ? $"reputation rank {r.Value2} must be 0..{MaxReputationRank - 1}" : null;
            case ConditionType.Team:
                return r.Value1 is not (Alliance or Horde) ? $"unknown team {r.Value1}" : null;
            case ConditionType.Skill or ConditionType.SkillBelow:
                return r.Value2 < 1 || r.Value2 > o.MaxSkillValue ? $"invalid skill value {r.Value2}" : null;
            case ConditionType.AreaFlag:
                return r.Value1 == 0 && r.Value2 == 0 ? "both area flag values are 0" : null;
            case ConditionType.RaceClass:
                if (r.Value1 == 0 && r.Value2 == 0)
                {
                    return "both race and class masks are 0";
                }

                if (r.Value1 != 0 && (r.Value1 & RaceMaskAllPlayable) == 0)
                {
                    return $"race mask {r.Value1} has no playable race";
                }

                return r.Value2 != 0 && (r.Value2 & ClassMaskAllPlayable) == 0 ? $"class mask {r.Value2} has no playable class" : null;
            case ConditionType.Level:
                return r.Value1 == 0 || r.Value1 > o.MaxPlayerLevel ? $"invalid level {r.Value1}"
                    : r.Value2 > 2 ? $"level mode {r.Value2} must be 0..2" : null;
            case ConditionType.Spell:
                return r.Value2 > 1 ? $"spell mode {r.Value2} must be 0..1" : null;
            case ConditionType.LastWaypoint:
                return r.Value2 > 2 ? $"last waypoint mode {r.Value2} must be 0..2" : null;
            case ConditionType.Gender:
                return r.Value1 >= 3 ? $"gender {r.Value1} must be 0..2" : null;
            case ConditionType.DeadOrAway:
                return r.Value1 >= 4 ? $"dead-or-away mode {r.Value1} must be 0..3" : null;
            case ConditionType.CreatureInRange:
                return r.Value2 == 0 ? "creature-in-range range must be greater than 0" : null;
            case ConditionType.IsInCombat:
                return r.Value1 > 1 ? $"is-in-combat state {r.Value1} must be 0..1" : null;
            default:
                return null;
        }
    }

    private static string? CheckReference(ConditionRecord r, uint reference, Dictionary<uint, ConditionEntry> accepted)
    {
        if (reference >= r.Entry)
        {
            return $"references condition {reference}, which must be lower than entry {r.Entry}";
        }

        return accepted.ContainsKey(reference) ? null : $"references condition {reference}, which does not exist or is invalid";
    }
}
