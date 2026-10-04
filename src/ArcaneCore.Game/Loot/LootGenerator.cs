using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>A rolled item before it is placed in a <see cref="LootBag"/>.</summary>
public readonly record struct RolledLoot(uint ItemId, uint Count, bool IsQuestItem, uint ConditionId);

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
    private int _processCalls;

    /// <summary>
    /// Hardening beyond vmangos: the most <c>Process</c> calls (template visits) one <see cref="Roll"/> may make. An imported reference
    /// <c>maxcount</c> is otherwise an unbounded repeat count (nesting multiplies it), which stalls the world thread; a hit stops the roll
    /// and keeps what was rolled so far. 0 disables the budget (vmangos behaviour). Only malformed data ever reaches the default.
    /// </summary>
    public int MaxProcessCallsPerRoll { get; init; } = DefaultMaxProcessCallsPerRoll;

    public LootContent Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

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
    private void Process(LootTableKind kind, uint entry, List<RolledLoot> result, int depth, byte groupFilter = 0)
    {
        if (depth > MaxReferenceDepth || (MaxProcessCallsPerRoll > 0 && ++_processCalls > MaxProcessCallsPerRoll))
        {
            return;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(kind, entry);
        if (groupFilter != 0)
        {
            // Group reference: only that group of this template (vmangos: Groups[groupId - 1].Process).
            // Built by group id, never by row order or adjacency.
            List<LootStoreRow> members = [.. rows.Where(r => r.GroupId == groupFilter && r.MinCountOrRef > 0)];
            if (members.Count > 0 && PickFromGroup(members) is { } picked)
            {
                Emit(picked, result, depth);
            }

            return;
        }

        foreach (LootStoreRow row in rows)
        {
            if ((row.GroupId > 0 && row.MinCountOrRef > 0) || !RollChance(Math.Abs(row.ChanceOrQuestChance)))
            {
                continue;
            }

            Emit(row, result, depth);
        }

        foreach (IGrouping<byte, LootStoreRow> group in rows.Where(r => r.GroupId > 0 && r.MinCountOrRef > 0).GroupBy(r => r.GroupId).OrderBy(g => g.Key))
        {
            if (PickFromGroup([.. group]) is { } picked)
            {
                Emit(picked, result, depth);
            }
        }
    }

    private void Emit(LootStoreRow row, List<RolledLoot> result, int depth)
    {
        if (row.MinCountOrRef < 0)
        {
            // vmangos LootMgr.cpp:1234: `loop < maxcount`, so maxcount 0 processes the reference zero times.
            for (uint n = 0; n < row.MaxCount; n++)
            {
                if (MaxProcessCallsPerRoll > 0 && _processCalls >= MaxProcessCallsPerRoll)
                {
                    break;
                }

                Process(LootTableKind.Reference, (uint)-row.MinCountOrRef, result, depth + 1, row.GroupId);
            }

            return;
        }

        uint min = (uint)Math.Max(1, row.MinCountOrRef);
        uint max = Math.Max(min, row.MaxCount);
        uint count = min == max ? min : (uint)_random.NextInt64(min, (long)max + 1);
        result.Add(new RolledLoot(row.Item, count, row.ChanceOrQuestChance < 0, row.ConditionId));
    }
    /// <summary>vmangos LootGroup::Roll: explicit chances against one shrinking roll, then the equal-chance pool.</summary>
    private LootStoreRow? PickFromGroup(IReadOnlyList<LootStoreRow> rows)
    {
        double roll = _random.NextDouble() * 100.0;
        List<LootStoreRow>? equal = null;
        for (int r = 0; r < rows.Count; r++)
        {
            LootStoreRow row = rows[r];
            float chance = Math.Abs(row.ChanceOrQuestChance);
            if (chance == 0)
            {
                (equal ??= []).Add(row);
                continue;
            }

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

        return equal is { Count: > 0 } ? equal[_random.Next(equal.Count)] : null;
    }

    private bool RollChance(float chance) => chance >= 100.0f || (chance > 0 && _random.NextDouble() * 100.0 < chance);
}

