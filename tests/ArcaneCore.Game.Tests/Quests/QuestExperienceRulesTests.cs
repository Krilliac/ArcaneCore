using Xunit;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Tests.Quests;

/// <summary>
/// Quest experience (cmangos Quests/QuestDef.cpp:171-206, vmangos QuestDef.cpp:180-202): the RewXP column or the amount derived
/// from RewMoneyMaxLevel, in float32.
/// </summary>
public sealed class QuestExperienceRulesTests
{
    private static QuestTemplate Money(uint rewMoneyMaxLevel) => new() { Entry = 1, RewMoneyMaxLevel = rewMoneyMaxLevel };

    private static QuestTemplate Column(uint rewXp) => new() { Entry = 1, RewXP = rewXp };

    [Theory]
    [InlineData(1u, 40u)]
    [InlineData(5u, 40u)]
    [InlineData(6u, 40u)]    // quest level 1: full up to level 6
    [InlineData(7u, 32u)]    // +6: 0.8
    [InlineData(8u, 24u)]    // +7: 0.6
    [InlineData(9u, 16u)]    // +8: 0.4
    [InlineData(10u, 8u)]    // +9: 0.2
    [InlineData(11u, 4u)]    // beyond: 0.1
    [InlineData(60u, 4u)]
    public void DerivedXp_OfRewMoneyMaxLevel24_AtQuestLevel1(uint playerLevel, uint expected)
    {
        // 24 / 0.6f = 40 (QuestDef.cpp:187-188); the step table is lines 191-205.
        Assert.Equal(expected, QuestExperienceRules.Xp(Money(24), 1, playerLevel, QuestXpSource.Derived));
    }

    [Theory]
    [InlineData(7u, 1, 8u, 7u)]     // float32: 7/0.6f*0.6f ceils to 7; double math gives 8
    [InlineData(11u, 1, 8u, 11u)]   // double math gives 12
    [InlineData(14u, 1, 8u, 14u)]   // double math gives 15
    [InlineData(22u, 10, 17u, 22u)] // double math gives 23
    public void DerivedXp_IsComputedInFloat32_NotDouble(uint money, int questLevel, uint playerLevel, uint expected)
    {
        Assert.Equal(expected, QuestExperienceRules.Xp(Money(money), questLevel, playerLevel, QuestXpSource.Derived));

        // The double result that a naive port would produce differs for these inputs.
        double asDouble = Math.Ceiling(money / 0.6 * 0.6);
        Assert.NotEqual(expected, (uint)asDouble);
    }

    [Theory]
    [InlineData(60, 200u)]
    [InlineData(61, 100u)]
    [InlineData(62, 50u)]
    [InlineData(63, 34u)]   // 120 / 3.6f = 33.33 -> 34
    [InlineData(64, 25u)]
    [InlineData(65, 20u)]
    [InlineData(70, 20u)]
    [InlineData(0, 0u)]
    public void DerivedXp_UsesTheDivisorOfTheQuestLevel(int questLevel, uint expected)
    {
        // RewMoneyMaxLevel 120 at a player level that never reduces it (player level <= quest level + 5 for 1.. levels):
        // 1..60 /0.6, 61 /1.2, 62 /2.4, 63 /3.6, 64 /4.8, 65+ /6.0 (QuestDef.cpp:176-185).
        Assert.Equal(expected, QuestExperienceRules.Xp(Money(120), questLevel, 1, QuestXpSource.Derived));
    }

    [Fact]
    public void AQuestLevelOfMinusOne_ReadsAsAnUnsignedHugeLevel_LikeTheReferences()
    {
        // QuestLevel is uint32 in cmangos and vmangos: -1 becomes 4294967295 (>= 65), q + 5 wraps to 4, q + 6 to 5, ...
        Assert.Equal(20u, QuestExperienceRules.Xp(Money(120), -1, 4, QuestXpSource.Derived));    // 120/6.0 full while pLevel <= 4
        Assert.Equal(16u, QuestExperienceRules.Xp(Money(120), -1, 5, QuestXpSource.Derived));    // +6: 0.8
        Assert.Equal(12u, QuestExperienceRules.Xp(Money(120), -1, 6, QuestXpSource.Derived));    // +7: 0.6
        Assert.Equal(2u, QuestExperienceRules.Xp(Money(120), -1, 10, QuestXpSource.Derived));    // beyond: 0.1
        Assert.Equal(100u, QuestExperienceRules.Xp(Column(100), -1, 4, QuestXpSource.RewXpColumn));
        Assert.Equal(80u, QuestExperienceRules.Xp(Column(100), -1, 5, QuestXpSource.RewXpColumn));
    }

    [Theory]
    [InlineData(15u, 100u)]
    [InlineData(16u, 80u)]
    [InlineData(17u, 61u)]   // 100 * 0.6f = 60.0000038 in float32, which ceils to 61 (the reference's float32 artifact)
    [InlineData(18u, 40u)]
    [InlineData(19u, 20u)]
    [InlineData(20u, 10u)]
    [InlineData(60u, 10u)]
    public void ColumnXp_ReducesAboveQuestLevelPlusFive(uint playerLevel, uint expected)
        => Assert.Equal(expected, QuestExperienceRules.Xp(Column(100), 10, playerLevel, QuestXpSource.RewXpColumn));

    [Fact]
    public void TheColumnSource_IgnoresRewMoneyMaxLevel_AndTheDerivedSource_IgnoresRewXp()
    {
        var both = new QuestTemplate { Entry = 1, RewXP = 100, RewMoneyMaxLevel = 24 };
        Assert.Equal(100u, QuestExperienceRules.Xp(both, 10, 1, QuestXpSource.RewXpColumn));
        Assert.Equal(40u, QuestExperienceRules.Xp(both, 10, 1, QuestXpSource.Derived));
        Assert.Equal(0u, QuestExperienceRules.Xp(Money(24), 10, 1, QuestXpSource.RewXpColumn));
        Assert.Equal(0u, QuestExperienceRules.Xp(Column(100), 10, 1, QuestXpSource.Derived));
    }

    [Theory]
    [InlineData(QuestXpSource.Auto, false, QuestXpSource.Derived)]
    [InlineData(QuestXpSource.Auto, true, QuestXpSource.RewXpColumn)]
    [InlineData(QuestXpSource.Derived, true, QuestXpSource.Derived)]
    [InlineData(QuestXpSource.RewXpColumn, false, QuestXpSource.RewXpColumn)]
    public void AutoResolvesPerDataset(QuestXpSource configured, bool datasetHasRewXp, QuestXpSource expected)
        => Assert.Equal(expected, QuestExperienceRules.Resolve(configured, datasetHasRewXp));

    [Fact]
    public void TheStoreKnowsWhetherTheDatasetCarriesRewXp()
    {
        Assert.False(new QuestStore(new QuestContent([Money(24), new QuestTemplate { Entry = 2 }], [], [])).HasRewXpColumn);
        Assert.True(new QuestStore(new QuestContent([Money(24), new QuestTemplate { Entry = 2, RewXP = 5 }], [], [])).HasRewXpColumn);
        Assert.False(QuestStore.Empty.HasRewXpColumn);
    }

    [Fact]
    public void TheFloat32MathMatchesAnIndependentSingleStepEmulation_ForEveryLevelPair()
    {
        // Independent of the implementation's expression structure: emulate each float32 operation by rounding a double
        // result to float (exact for + - * / since double carries more than 2p+2 bits), over many money values and levels.
        static float F(double v) => (float)v;
        for (uint money = 1; money <= 600; money++)
        {
            for (int q = 1; q <= 60; q += 7)
            {
                for (uint p = 1; p <= 62; p++)
                {
                    float full = F(money / (double)0.6f);
                    float reduced = p <= q + 5 ? full : F(full * (double)(p == q + 6 ? 0.8f : p == q + 7 ? 0.6f : p == q + 8 ? 0.4f : p == q + 9 ? 0.2f : 0.1f));
                    uint expected = (uint)Math.Ceiling((double)reduced);
                    Assert.True(expected == QuestExperienceRules.Xp(Money(money), q, p, QuestXpSource.Derived), $"money {money} quest {q} player {p}");
                }
            }
        }
    }
}
