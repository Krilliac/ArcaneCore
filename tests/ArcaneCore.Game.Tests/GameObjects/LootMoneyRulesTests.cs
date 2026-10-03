using ArcaneCore.Game.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// LootMoneyRules ports vmangos Loot::GenerateMoneyLoot (D:\refs\vmangos\src\game\LootMgr.cpp:735-746).
/// Fixtures use real classic-db creature gold ranges (Onyxia 937551..1273511, a max&lt;min row).
/// </summary>
public sealed class LootMoneyRulesTests
{
    [Fact]
    public void MaxZero_GeneratesNothing() => Assert.Equal(0u, LootMoneyRules.Generate(0, 0, 1f, new Random(1)));

    [Fact]
    public void MaxBelowMin_UsesMax_NotMin() => Assert.Equal(16194u, LootMoneyRules.Generate(20000, 16194, 1f, new Random(2)));

    [Fact]
    public void MaxEqualMin_UsesMax() => Assert.Equal(77u, LootMoneyRules.Generate(77, 77, 1f, new Random(2)));

    [Fact]
    public void RangeBelow32700_IsUniformInclusive()
    {
        var random = new Random(5);
        var seen = new HashSet<uint>();
        for (int i = 0; i < 400; i++)
        {
            uint gold = LootMoneyRules.Generate(10, 13, 1f, random);
            Assert.InRange(gold, 10u, 13u);
            seen.Add(gold);
        }

        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void RangeAtLeast32700_UsesShiftedBytes_SoResultIsAMultipleOf256()
    {
        var random = new Random(6);
        for (int i = 0; i < 200; i++)
        {
            uint gold = LootMoneyRules.Generate(937551, 1273511, 1f, random);
            Assert.Equal(0u, gold % 256);
            Assert.InRange(gold, (937551u >> 8) << 8, ((1273511u >> 8) << 8));
        }
    }

    [Fact]
    public void RangeJustBelowThreshold_IsNotShifted()
    {
        var random = new Random(7);
        bool anyUnaligned = false;
        for (int i = 0; i < 100; i++)
        {
            anyUnaligned |= LootMoneyRules.Generate(1000, 1000 + 32699, 1f, random) % 256 != 0;
        }

        Assert.True(anyUnaligned);
    }

    [Fact]
    public void RateIsAppliedBeforeTheShift_AndTruncates()
    {
        Assert.Equal(15u, LootMoneyRules.Generate(10, 10, 1.5f, new Random(1)));
        Assert.Equal(0u, LootMoneyRules.Generate(100000, 140000, 0f, new Random(1)));
    }
}
