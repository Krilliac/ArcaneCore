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

    private readonly Random _random = random ?? new Random();

    public LootContent Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>Roll the template <paramref name="entry"/> of <paramref name="kind"/>.</summary>
    public List<RolledLoot> Roll(LootTableKind kind, uint entry)
    {
        var result = new List<RolledLoot>();
        Process(kind, entry, result, depth: 0);
        return result;
    }

    private void Process(LootTableKind kind, uint entry, List<RolledLoot> result, int depth)
    {
        if (depth > MaxReferenceDepth)
        {
            return;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(kind, entry);
        int i = 0;
        while (i < rows.Count)
        {
            byte group = rows[i].GroupId;
            int end = i;
            while (end < rows.Count && rows[end].GroupId == group)
            {
                end++;
            }

            if (group == 0)
            {
                for (int r = i; r < end; r++)
                {
                    LootStoreRow row = rows[r];
                    if (!RollChance(Math.Abs(row.ChanceOrQuestChance)))
                    {
                        continue;
                    }

                    Emit(row, result, depth);
                }
            }
            else
            {
                if (PickFromGroup(rows, i, end) is { } picked)
                {
                    Emit(picked, result, depth);
                }
            }

            i = end;
        }
    }

    private void Emit(LootStoreRow row, List<RolledLoot> result, int depth)
    {
        if (row.MinCountOrRef < 0)
        {
            uint repeats = Math.Max(1u, row.MaxCount);
            for (uint n = 0; n < repeats; n++)
            {
                Process(LootTableKind.Reference, (uint)-row.MinCountOrRef, result, depth + 1);
            }

            return;
        }

        uint min = (uint)Math.Max(1, row.MinCountOrRef);
        uint max = Math.Max(min, row.MaxCount);
        uint count = min == max ? min : (uint)_random.NextInt64(min, (long)max + 1);
        result.Add(new RolledLoot(row.Item, count, row.ChanceOrQuestChance < 0, row.ConditionId));
    }

    /// <summary>vmangos LootGroup::Roll: explicit chances against one shrinking roll, then the equal-chance pool.</summary>
    private LootStoreRow? PickFromGroup(IReadOnlyList<LootStoreRow> rows, int start, int end)
    {
        double roll = _random.NextDouble() * 100.0;
        List<LootStoreRow>? equal = null;
        for (int r = start; r < end; r++)
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
