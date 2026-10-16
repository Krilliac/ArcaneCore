using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Tests.Memo;

/// <summary>
/// <see cref="LootGenerator"/> as it was before its per-template plans (origin/main 89397354, src/ArcaneCore.Game/Loot/LootGenerator.cs): the
/// groups were rebuilt by Where/GroupBy/OrderBy on every visit and the equal-chance pool on every pick. Kept verbatim as the oracle of the
/// equivalence tests: same content, same seed, same rate, so every roll must come out identical, random draws included. Also linked into
/// ArcaneCore.World.Tests for the real world.db comparison.
/// </summary>
internal sealed class ReferenceLootGenerator(LootContent content, Random random)
{
    private readonly Random _random = random;
    private int _processCalls;

    public int MaxProcessCallsPerRoll { get; init; } = LootGenerator.DefaultMaxProcessCallsPerRoll;

    public LootContent Content { get; } = content;

    public Func<LootStoreRow, float>? ChanceRate { get; set; }

    public List<RolledLoot> Roll(LootTableKind kind, uint entry)
    {
        var result = new List<RolledLoot>();
        _processCalls = 0;
        Process(kind, entry, result, depth: 0);
        return result;
    }

    private void Process(LootTableKind kind, uint entry, List<RolledLoot> result, int depth, byte groupFilter = 0,
        IReadOnlyList<uint>? inheritedConditions = null)
    {
        if (depth > LootGenerator.MaxReferenceDepth || (MaxProcessCallsPerRoll > 0 && ++_processCalls > MaxProcessCallsPerRoll))
        {
            return;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(kind, entry);
        if (groupFilter != 0)
        {
            List<LootStoreRow> members = [.. rows.Where(r => r.GroupId == groupFilter && r.MinCountOrRef > 0)];
            if (members.Count > 0 && PickFromGroup(members) is { } picked)
            {
                Emit(picked, result, depth, inheritedConditions);
            }

            return;
        }

        foreach (LootStoreRow row in rows)
        {
            if ((row.GroupId > 0 && row.MinCountOrRef > 0) || !RollRated(row))
            {
                continue;
            }

            Emit(row, result, depth, inheritedConditions);
        }

        foreach (IGrouping<byte, LootStoreRow> group in rows.Where(r => r.GroupId > 0 && r.MinCountOrRef > 0).GroupBy(r => r.GroupId).OrderBy(g => g.Key))
        {
            if (PickFromGroup([.. group]) is { } picked)
            {
                Emit(picked, result, depth, inheritedConditions);
            }
        }
    }

    private void Emit(LootStoreRow row, List<RolledLoot> result, int depth, IReadOnlyList<uint>? inheritedConditions)
    {
        if (row.MinCountOrRef < 0)
        {
            IReadOnlyList<uint> conditions = row.ConditionId == 0 ? inheritedConditions ?? []
                : [.. (inheritedConditions ?? []), row.ConditionId];
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

    private bool RollRated(LootStoreRow row)
    {
        float chance = Math.Abs(row.ChanceOrQuestChance);
        return chance >= 100.0f || RollChance(chance * (ChanceRate?.Invoke(row) ?? 1.0f));
    }

    /// <summary>The rolls of both generators are the same item, count, quest flag, condition and reference conditions, in the same order.</summary>
    public static bool Same(IReadOnlyList<RolledLoot> expected, IReadOnlyList<RolledLoot> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            RolledLoot a = expected[i];
            RolledLoot b = actual[i];
            if (a.ItemId != b.ItemId || a.Count != b.Count || a.IsQuestItem != b.IsQuestItem || a.ConditionId != b.ConditionId
                || !a.ReferenceConditions.SequenceEqual(b.ReferenceConditions))
            {
                return false;
            }
        }

        return true;
    }
}
