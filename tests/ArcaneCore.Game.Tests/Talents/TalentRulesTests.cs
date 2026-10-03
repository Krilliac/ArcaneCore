using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Talents;
using Xunit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Pure talent rules. Reference: vmangos src/game/Objects/Player.cpp (CalculateTalentsPoints :20500,
/// LearnTalent :20684-20800, UpdateFreeTalentPoints :3217-3247) and DBCStores.cpp:466-480.
/// </summary>
public sealed class TalentRulesTests
{
    private const uint WarriorMask = 1u << 0;

    // tab 1 = warrior, tab 2 = another class
    private static readonly TalentCatalog Catalog = new(
        [new TalentTabRecord(1, WarriorMask, 0), new TalentTabRecord(2, 1u << 1, 1)],
        [
            new TalentRecord(10, 1, 0, 0, [100, 101, 102, 0, 0], 0, 0, 0),
            new TalentRecord(11, 1, 1, 0, [110, 111, 0, 0, 0], 0, 0, 0),
            new TalentRecord(12, 1, 2, 0, [120, 121, 0, 0, 0], 11, 1, 0),   // needs full rank (index 1) of talent 11
            new TalentRecord(13, 1, 0, 1, [130, 0, 0, 0, 0], 11, 0, 0),     // any rank of talent 11
            new TalentRecord(14, 1, 0, 2, [140, 0, 0, 0, 0], 0, 0, 555),    // needs spell 555
            new TalentRecord(20, 2, 0, 0, [200, 201, 0, 0, 0], 0, 0, 0),
        ]);

    private static Func<uint, bool> Knows(params uint[] spells)
    {
        var set = spells.ToHashSet();
        return set.Contains;
    }

    [Theory]
    [InlineData(1, 1.0, 0u)]
    [InlineData(9, 1.0, 0u)]
    [InlineData(10, 1.0, 1u)]
    [InlineData(11, 1.0, 2u)]
    [InlineData(60, 1.0, 51u)]
    [InlineData(20, 0.5, 5u)]
    [InlineData(10, 2.0, 2u)]
    [InlineData(0, 1.0, 0u)]
    public void PointsForLevel(int level, double rate, uint expected)
        => Assert.Equal(expected, TalentRules.PointsForLevel(level, rate));

    [Fact]
    public void CostOfSpell_IsRankPlusOne_AndZeroForOtherSpells()
    {
        Assert.Equal(1u, TalentRules.CostOfSpell(Catalog, 100));
        Assert.Equal(3u, TalentRules.CostOfSpell(Catalog, 102));
        Assert.Equal(0u, TalentRules.CostOfSpell(Catalog, 99999));
    }

    [Fact]
    public void UsedPoints_SumsEveryKnownRankSpell()
    {
        Assert.Equal(0u, TalentRules.UsedPoints(Catalog, Knows()));
        Assert.Equal(3u + 2u + 1u, TalentRules.UsedPoints(Catalog, Knows(102, 111, 200, 4242)));
    }

    [Fact]
    public void SpentInTab_CountsOnlyThatTab()
    {
        Func<uint, bool> has = Knows(102, 111, 200);
        Assert.Equal(5u, TalentRules.SpentInTab(Catalog, 1, has));
        Assert.Equal(1u, TalentRules.SpentInTab(Catalog, 2, has));
        Assert.Equal(0u, TalentRules.SpentInTab(Catalog, 9, has));
    }

    [Fact]
    public void HighestKnownRank_IsOneBasedAndZeroWhenNone()
    {
        TalentRecord talent = Catalog.ById(10)!;
        Assert.Equal(0, TalentRules.HighestKnownRank(talent, Knows()));
        Assert.Equal(2, TalentRules.HighestKnownRank(talent, Knows(100, 101)));
        Assert.Equal(1, TalentRules.HighestKnownRank(talent, Knows(100)));
    }

    private static TalentLearnResult Learn(uint talent, uint rank, uint free, uint classMask = WarriorMask, params uint[] known)
        => TalentRules.EvaluateLearn(Catalog, Knows(known), talent, rank, free, classMask);

    [Fact]
    public void Learn_AcceptsFirstRankWithOnePoint()
    {
        TalentLearnResult result = Learn(10, 0, 1);
        Assert.Equal(TalentLearnOutcome.Ok, result.Outcome);
        Assert.Equal(100u, result.RankSpell);
        Assert.Equal(1u, result.PointsNeeded);
    }

    [Fact]
    public void Learn_Refusals_CarryTheirReason()
    {
        Assert.Equal(TalentLearnOutcome.NoFreePoints, Learn(10, 0, 0).Outcome);
        Assert.Equal(TalentLearnOutcome.RankOutOfRange, Learn(10, 5, 9).Outcome);
        Assert.Equal(TalentLearnOutcome.UnknownTalent, Learn(999, 0, 9).Outcome);
        Assert.Equal(TalentLearnOutcome.WrongClass, Learn(20, 0, 9).Outcome);
        Assert.Equal(TalentLearnOutcome.AlreadyKnown, Learn(10, 1, 9, WarriorMask, 101).Outcome);
        Assert.Equal(TalentLearnOutcome.AlreadyKnown, Learn(10, 0, 9, WarriorMask, 101).Outcome);
        Assert.Equal(TalentLearnOutcome.MissingRankSpell, Learn(10, 3, 9).Outcome);
    }

    [Fact]
    public void Learn_JumpingRanksCostsTheDelta()
    {
        Assert.Equal(TalentLearnOutcome.NotEnoughPoints, Learn(10, 2, 2).Outcome);
        TalentLearnResult three = Learn(10, 2, 3);
        Assert.Equal(TalentLearnOutcome.Ok, three.Outcome);
        Assert.Equal(3u, three.PointsNeeded);
        TalentLearnResult delta = Learn(10, 2, 2, WarriorMask, 100);
        Assert.Equal(TalentLearnOutcome.Ok, delta.Outcome);
        Assert.Equal(2u, delta.PointsNeeded);
        Assert.Equal(TalentLearnOutcome.NotEnoughPoints, Learn(10, 2, 1, WarriorMask, 100).Outcome);
    }

    [Fact]
    public void Learn_TierGate_NeedsFivePointsPerRowInTheSameTab()
    {
        // talent 11 is row 1: needs 5 points spent in tab 1
        Assert.Equal(TalentLearnOutcome.TierLocked, Learn(11, 0, 1, WarriorMask, 101).Outcome);        // 2 spent
        Assert.Equal(TalentLearnOutcome.TierLocked, Learn(11, 0, 1, WarriorMask, 102, 130).Outcome);   // 3 + 1 = 4
        Assert.Equal(TalentLearnOutcome.Ok, Learn(11, 0, 1, WarriorMask, 102, 130, 140).Outcome);             // 3 + 1 + 1 = 5
        Assert.Equal(TalentLearnOutcome.TierLocked, Learn(11, 0, 1, WarriorMask, 102, 200).Outcome);          // other tab does not count
    }

    [Fact]
    public void Learn_TierTwoNeedsTenPoints()
    {
        // talent 12 is row 2 (needs 10 points in the tab) with prerequisite talent 11 at its last rank (111).
        // The evaluator sums j+1 over every known rank spell, so {100,101,102} counts 1+2+3.
        Assert.Equal(TalentLearnOutcome.TierLocked, Learn(12, 0, 1, WarriorMask, 102, 111, 130, 140).Outcome);          // 3+2+1+1 = 7
        Assert.Equal(TalentLearnOutcome.Ok, Learn(12, 0, 1, WarriorMask, 100, 101, 102, 111, 130, 140).Outcome);        // 1+2+3+2+1+1 = 10
    }

    [Fact]
    public void Learn_PrerequisiteRank_IsAnIndexAndAnyHigherRankCounts()
    {
        // talent 13: DependsOnRank 0, any rank of 11 satisfies. Row 0, so no tier gate.
        Assert.Equal(TalentLearnOutcome.PrerequisiteTalent, Learn(13, 0, 1).Outcome);
        Assert.Equal(TalentLearnOutcome.Ok, Learn(13, 0, 1, WarriorMask, 110).Outcome);
        Assert.Equal(TalentLearnOutcome.Ok, Learn(13, 0, 1, WarriorMask, 111).Outcome);
    }

    [Fact]
    public void Learn_PrerequisiteWithRankIndexOne_RefusesRankZero()
    {
        // A row-0 catalog, so the tier gate cannot interfere: talent 12 needs rank index 1 (the last rank) of talent 11.
        var flat = new TalentCatalog(
            [new TalentTabRecord(1, WarriorMask, 0)],
            [
                new TalentRecord(11, 1, 0, 0, [110, 111, 0, 0, 0], 0, 0, 0),
                new TalentRecord(12, 1, 0, 1, [120, 0, 0, 0, 0], 11, 1, 0),
            ]);
        Assert.Equal(TalentLearnOutcome.PrerequisiteTalent,
            TalentRules.EvaluateLearn(flat, Knows(110), 12, 0, 1, WarriorMask).Outcome);
        Assert.Equal(TalentLearnOutcome.Ok,
            TalentRules.EvaluateLearn(flat, Knows(111), 12, 0, 1, WarriorMask).Outcome);
    }

    [Fact]
    public void Learn_DependsOnSpell()
    {
        Assert.Equal(TalentLearnOutcome.PrerequisiteSpell, Learn(14, 0, 1).Outcome);
        Assert.Equal(TalentLearnOutcome.Ok, Learn(14, 0, 1, WarriorMask, 555).Outcome);
    }

    [Fact]
    public void Learn_ClassMaskUsesAnySharedBit()
    {
        Assert.Equal(TalentLearnOutcome.Ok, Learn(10, 0, 1, WarriorMask | (1u << 5)).Outcome);
        Assert.Equal(TalentLearnOutcome.WrongClass, Learn(10, 0, 1, 1u << 5).Outcome);
    }

    [Theory]
    [InlineData(9, 0u, false, false, null, null)]                // nothing to do
    [InlineData(9, 2u, true, false, 0u, true)]                   // below 10 with talents: reset then zero
    [InlineData(9, 2u, false, false, 0u, false)]                 // below 10, no reset requested: zero only
    [InlineData(12, 2u, true, false, 1u, false)]                 // 3 allowed, 2 used
    [InlineData(12, 3u, true, false, 0u, false)]
    [InlineData(12, 4u, true, false, null, true)]                // overspent: reset
    [InlineData(12, 4u, true, true, 0u, false)]                  // overspent administrator: zero, keep spells
    [InlineData(12, 4u, false, false, 0u, false)]                // overspent without reset permission: zero
    public void FreePointsDecision(int level, uint used, bool resetIfNeed, bool administrator, uint? expectedFree, bool? expectedReset)
    {
        TalentFreePointsDecision decision = TalentRules.DecideFreePoints(level, used, 1.0, resetIfNeed, administrator);
        if (expectedFree is null && expectedReset is null)
        {
            Assert.False(decision.Reset);
            Assert.Null(decision.FreePoints);
            return;
        }

        Assert.Equal(expectedFree, decision.FreePoints);
        Assert.Equal(expectedReset ?? false, decision.Reset);
    }
}
