using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>A rolled item before it is placed in a <see cref="LootBag"/>.</summary>
public readonly record struct RolledLoot(uint ItemId, uint Count, bool IsQuestItem, uint ConditionId)
{
    /// <summary>Conditions on reference rows traversed to reach this item (LootMgr.cpp:1225-1243).</summary>
    public IReadOnlyList<uint> ReferenceConditions { get; init; } = [];
}

/// <summary>
/// Rolls a loot template (behaviour re-implemented from vmangos LootTemplate::Process and
/// LootGroup::Roll/Process, no code copied):
/// <list type="bullet">
/// <item>Ungrouped rows roll independently against |chance| percent (≥100 always drops).</item>
/// <item>A row with a negative mincountOrRef is a reference: when its chance rolls, the
/// reference_loot_template entry -mincountOrRef is processed maxcount times.</item>
/// <item>Each group (groupid &gt; 0) yields at most one row: rows with an explicit chance are
/// tried in order against a single roll in [0,100) whose remainder shrinks by each chance; if
/// none hit, one of the zero-chance ("equal chance") rows is picked uniformly.</item>
/// <item>The count is uniform in [mincount, maxcount].</item>
/// <item>A negative chance marks a quest drop; whether it may be generated is decided by the caller.</item>
/// </list>
/// Deterministic for a seeded <see cref="Random"/>. Not thread-safe (one generator per world thread).
/// </summary>
public sealed class LootGenerator(LootContent content, Random? random = null)
{
    /// <summary>Reference nesting limit (vmangos rejects reference cycles at load; this fails closed at runtime).</summary>
    public const int MaxReferenceDepth = 8;

    /// <summary>Default of <see cref="MaxProcessCallsPerRoll"/>: far above any real template (a roll runs tens of calls).</summary>
    public const int DefaultMaxProcessCallsPerRoll = 4096;

    private readonly Random _random = random ?? new Random();

    /// <summary>
    /// The groups of each template rolled so far (<see cref="PlanOf"/>), built on its first roll: the per-roll Where/GroupBy/OrderBy and the
    /// group copies were the cost of every kill, chest and reference visit. The content is read-only and fixed for this generator (a reload
    /// builds a new generator), so a plan never goes stale; one per template of the content at most, empty templates are not kept.
    /// </summary>
    private readonly Dictionary<(LootTableKind Kind, uint Entry), LootPlan> _plans = [];
    private int _processCalls;

    /// <summary>
    /// Hardening beyond vmangos: the most <c>Process</c> calls (template visits) one <see cref="Roll"/> may make. An imported reference
    /// <c>maxcount</c> is otherwise an unbounded repeat count (nesting multiplies it), which stalls the world thread; a hit stops the roll
    /// and keeps what was rolled so far. 0 disables the budget (vmangos behaviour). Only malformed data ever reaches the default.
    /// </summary>
    public int MaxProcessCallsPerRoll { get; init; } = DefaultMaxProcessCallsPerRoll;

    public LootContent Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>
    /// The Rate.Drop.Item.* multiplier of an ungrouped row (vmangos LootStoreItem::Roll(rate), LootMgr.cpp:256-268: the item quality's rate, or
    /// Rate.Drop.Item.Referenced for a reference row), or null for 1. Every table rolled here allows rates (vmangos turns them off only for
    /// mail loot, LootMgr.cpp:49) and a reference keeps the rate of the table that reached it (:1229-1230). Grouped rows are never scaled.
    /// </summary>
    public Func<LootStoreRow, float>? ChanceRate { get; set; }

    /// <summary>Roll the template <paramref name="entry"/> of <paramref name="kind"/>.</summary>
    public List<RolledLoot> Roll(LootTableKind kind, uint entry)
    {
        var result = new List<RolledLoot>();
        _processCalls = 0;
        Process(kind, entry, result, depth: 0);
        return result;
    }

    // vmangos LootTemplate::AddEntry/Process (LootMgr.cpp:1193-1244). A row joins a group only when
    // groupid > 0 AND mincountOrRef > 0; reference rows (mincountOrRef < 0) are stored with the ungrouped
    // entries and roll independently, whatever their groupid. A reference row's groupid instead selects
    // the single group of the REFERENCED template that is processed (0 = the whole template).
    private void Process(LootTableKind kind, uint entry, List<RolledLoot> result, int depth, byte groupFilter = 0,
        IReadOnlyList<uint>? inheritedConditions = null)
    {
        if (depth > MaxReferenceDepth || (MaxProcessCallsPerRoll > 0 && ++_processCalls > MaxProcessCallsPerRoll))
        {
            return;
        }

        LootPlan plan = PlanOf(kind, entry);
        if (groupFilter != 0)
        {
            // Group reference: only that group of this template (vmangos: Groups[groupId - 1].Process).
            // Built by group id, never by row order or adjacency.
            if (plan.Find(groupFilter) is { } members && PickFromGroup(members) is { } picked)
            {
                Emit(picked, result, depth, inheritedConditions);
            }

            return;
        }

        foreach (LootStoreRow row in plan.Rows)
        {
            if ((row.GroupId > 0 && row.MinCountOrRef > 0) || !RollRated(row))
            {
                continue;
            }

            Emit(row, result, depth, inheritedConditions);
        }

        foreach (LootGroup group in plan.Groups)
        {
            if (PickFromGroup(group) is { } picked)
            {
                Emit(picked, result, depth, inheritedConditions);
            }
        }
    }

    /// <summary>The rows of a template and its groups (groupid &gt; 0 and mincountOrRef &gt; 0), by ascending group id, rows in table order.</summary>
    private LootPlan PlanOf(LootTableKind kind, uint entry)
    {
        if (_plans.TryGetValue((kind, entry), out LootPlan? plan))
        {
            return plan;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(kind, entry);
        if (rows.Count == 0)
        {
            return LootPlan.Empty;
        }

        LootGroup[] groups = [.. rows.Where(r => r.GroupId > 0 && r.MinCountOrRef > 0).GroupBy(r => r.GroupId).OrderBy(g => g.Key)
            .Select(g => new LootGroup(g.Key,
                [.. g.Where(r => Math.Abs(r.ChanceOrQuestChance) != 0)], [.. g.Where(r => Math.Abs(r.ChanceOrQuestChance) == 0)]))];
        plan = new LootPlan(rows, groups);
        _plans[(kind, entry)] = plan;
        return plan;
    }

    private void Emit(LootStoreRow row, List<RolledLoot> result, int depth, IReadOnlyList<uint>? inheritedConditions)
    {
        if (row.MinCountOrRef < 0)
        {
            IReadOnlyList<uint> conditions = row.ConditionId == 0 ? inheritedConditions ?? []
                : [.. (inheritedConditions ?? []), row.ConditionId];
            // vmangos LootMgr.cpp:1234: `loop < maxcount`, so maxcount 0 processes the reference zero times.
            for (uint n = 0; n < row.MaxCount; n++)
            {
                if (MaxProcessCallsPerRoll > 0 && _processCalls >= MaxProcessCallsPerRoll)
                {
                    break;
                }

                Process(LootTableKind.Reference, (uint)-row.MinCountOrRef, result, depth + 1, row.GroupId, conditions);
            }

            return;
        }

        uint min = (uint)Math.Max(1, row.MinCountOrRef);
        uint max = Math.Max(min, row.MaxCount);
        uint count = min == max ? min : (uint)_random.NextInt64(min, (long)max + 1);
        result.Add(new RolledLoot(row.Item, count, row.ChanceOrQuestChance < 0, row.ConditionId)
        {
            ReferenceConditions = inheritedConditions ?? [],
        });
    }
    /// <summary>
    /// vmangos LootGroup::Roll: explicit chances, in table order, against one shrinking roll, then the equal-chance (zero) pool. The roll is
    /// drawn even for a group with no explicit chance, and the pool pick only when no explicit row hit, as before the plans.
    /// </summary>
    private LootStoreRow? PickFromGroup(LootGroup group)
    {
        double roll = _random.NextDouble() * 100.0;
        foreach (LootStoreRow row in group.Explicit)
        {
            float chance = Math.Abs(row.ChanceOrQuestChance);
            if (chance >= 100.0f)
            {
                return row;
            }

            roll -= chance;
            if (roll < 0)
            {
                return row;
            }
        }

        return group.Equal.Length > 0 ? group.Equal[_random.Next(group.Equal.Length)] : null;
    }

    private bool RollChance(float chance) => chance >= 100.0f || (chance > 0 && _random.NextDouble() * 100.0 < chance);

    // vmangos LootStoreItem::Roll: a chance of 100 or more drops before the rate is looked at; otherwise the rate scales the chance.
    private bool RollRated(LootStoreRow row)
    {
        float chance = Math.Abs(row.ChanceOrQuestChance);
        return chance >= 100.0f || RollChance(chance * (ChanceRate?.Invoke(row) ?? 1.0f));
    }

    /// <summary>One group of a template: the rows with a chance and the equal-chance rows, each in table order.</summary>
    private sealed record LootGroup(byte Id, LootStoreRow[] Explicit, LootStoreRow[] Equal);

    /// <summary>A template's rows as the content holds them, and its groups by ascending id.</summary>
    private sealed class LootPlan(IReadOnlyList<LootStoreRow> rows, LootGroup[] groups)
    {
        public static readonly LootPlan Empty = new([], []);

        public IReadOnlyList<LootStoreRow> Rows { get; } = rows;

        public LootGroup[] Groups { get; } = groups;

        public LootGroup? Find(byte id)
        {
            foreach (LootGroup group in Groups)
            {
                if (group.Id == id)
                {
                    return group;
                }
            }

            return null;
        }
    }
}

