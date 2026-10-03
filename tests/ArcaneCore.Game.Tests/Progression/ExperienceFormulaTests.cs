using ArcaneCore.Game.Progression;
using Xunit;

namespace ArcaneCore.Game.Tests.Progression;

/// <summary>Table tests for the vanilla XP formulas (vmangos 4b3d241 Formulas.h behaviour).</summary>
public sealed class ExperienceFormulaTests
{
    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(5u, 0u)]
    [InlineData(6u, 1u)]
    [InlineData(10u, 4u)]
    [InlineData(20u, 13u)]
    [InlineData(39u, 31u)]
    [InlineData(40u, 31u)]
    [InlineData(50u, 39u)]
    [InlineData(59u, 47u)]
    [InlineData(60u, 47u)]
    public void GrayLevel_MatchesTheVanillaSteps(uint playerLevel, uint gray)
        => Assert.Equal(gray, ExperienceFormulas.GrayLevel(playerLevel));

    [Theory]
    [InlineData(1u, 5u)]
    [InlineData(7u, 5u)]
    [InlineData(8u, 6u)]
    [InlineData(10u, 7u)]
    [InlineData(12u, 8u)]
    [InlineData(16u, 9u)]
    [InlineData(20u, 11u)]
    [InlineData(30u, 12u)]
    [InlineData(40u, 13u)]
    [InlineData(45u, 14u)]
    [InlineData(50u, 15u)]
    [InlineData(55u, 16u)]
    [InlineData(60u, 17u)]
    public void ZeroDifference_MatchesTheVanillaTable(uint playerLevel, uint zeroDifference)
        => Assert.Equal(zeroDifference, ExperienceFormulas.ZeroDifference(playerLevel));

    [Theory]
    // Same level: (5 × level + 45).
    [InlineData(1u, 1u, false, false, 50u)]
    [InlineData(10u, 10u, false, false, 95u)]
    [InlineData(60u, 60u, false, false, 345u)]
    // Higher creatures: +5 % per level, capped at +4.
    [InlineData(10u, 12u, false, false, 104u)] // 95 × 1.1 = 104.5 in float, nearest-even
    [InlineData(10u, 20u, false, false, 114u)]
    // Lower creatures: linear over the zero difference (level 10: zd 7), zero at the gray level.
    [InlineData(10u, 8u, false, false, 68u)]
    [InlineData(10u, 5u, false, false, 27u)]
    [InlineData(10u, 4u, false, false, 0u)]
    [InlineData(60u, 47u, false, false, 0u)]
    // Elites double; in a non-raid dungeon ×2.5 (862.5 rounds to even).
    [InlineData(60u, 60u, true, false, 690u)]
    [InlineData(60u, 60u, true, true, 862u)]
    [InlineData(10u, 10u, true, true, 238u)]
    public void KillGain_TableOfLevelsEliteAndDungeon(uint player, uint creature, bool elite, bool dungeon, uint xp)
        => Assert.Equal(xp, ExperienceFormulas.KillGain(player, creature, elite, dungeon));

    [Fact]
    public void KillGain_AppliesKillAndEliteRates()
    {
        Assert.Equal(190u, ExperienceFormulas.KillGain(10, 10, elite: false, nonRaidDungeon: false, killRate: 2.0f));
        Assert.Equal(285u, ExperienceFormulas.KillGain(10, 10, elite: true, nonRaidDungeon: false, killRate: 1.0f, eliteRate: 1.5f));
        Assert.Equal(0u, ExperienceFormulas.KillGain(10, 10, elite: false, nonRaidDungeon: false, killRate: 0f));
        Assert.Equal(0u, ExperienceFormulas.KillGain(10, 10, elite: false, nonRaidDungeon: false, killRate: float.NaN));
    }

    [Theory]
    [InlineData(1, 1.0f)]
    [InlineData(2, 1.0f)]
    [InlineData(3, 1.166f)]
    [InlineData(4, 1.3f)]
    [InlineData(5, 1.4f)]
    [InlineData(10, 0.5f)]
    [InlineData(40, 0.01f)]
    public void GroupRate_Table(int count, float rate) => Assert.Equal(rate, ExperienceFormulas.GroupRate(count), 3);

    [Fact]
    public void XpTable_EveryRowEqualsXpToLevelRoundedToTheNearestHundred()
    {
        for (uint level = 1; level <= PlayerXpTable.MaxTableLevel; level++)
        {
            // MaNGOS::XP::xp_to_level for lvl < 60: (8 lvl + diff(lvl)) × (5 lvl + 45), nearest 100.
            uint diff = level < 29 ? 0 : level == 29 ? 1 : level == 30 ? 3 : level == 31 ? 6 : 5 * (level - 30);
            uint raw = ((8 * level) + diff) * ((5 * level) + 45);
            Assert.Equal((raw + 50) / 100 * 100, PlayerXpTable.XpForLevel(level));
        }
    }

    [Fact]
    public void XpTable_IsZeroAtOrAboveTheMaximumAndForInvalidLevels()
    {
        Assert.Equal(0u, PlayerXpTable.XpForLevel(0));
        Assert.Equal(400u, PlayerXpTable.XpForLevel(1));
        Assert.Equal(209800u, PlayerXpTable.XpForLevel(59));
        Assert.Equal(0u, PlayerXpTable.XpForLevel(60));
        Assert.Equal(0u, PlayerXpTable.XpForLevel(20, maxPlayerLevel: 20));
        Assert.Equal(21300u, PlayerXpTable.XpForLevel(19, maxPlayerLevel: 20));
    }

    [Fact]
    public void Distribute_SoloKillerReceivesTheFullGainAndCredit()
    {
        KillShare share = Assert.Single(KillExperience.Distribute([new KillCandidate(10, true, false)], 10, false, false));
        Assert.Equal(new KillShare(0, 95, true), share);
    }

    [Fact]
    public void Distribute_GroupSplitsByLevelWithTheGroupRate()
    {
        // Two level-10 members: rate 1.0, each gets 95 × 10/20.
        IReadOnlyList<KillShare> pair = KillExperience.Distribute([new(10, true, false), new(10, true, false)], 10, false, false);
        Assert.All(pair, s => Assert.Equal(47u, s.Experience));

        // Three members: 1.166 group rate, 95 × 1.166 × 10/30 = 36.9.
        IReadOnlyList<KillShare> three = KillExperience.Distribute(
            [new(10, true, false), new(10, true, false), new(10, true, false)], 10, false, false);
        Assert.All(three, s => Assert.Equal(36u, s.Experience));
    }

    [Fact]
    public void Distribute_DeadMembersGetNoXpButKeepCreditUntilReleased()
    {
        IReadOnlyList<KillShare> shares = KillExperience.Distribute(
            [new(10, true, false), new(10, false, false), new(10, false, true)], 10, false, false);
        Assert.Equal(95u, shares[0].Experience); // only alive members count toward the split
        Assert.Equal(0u, shares[1].Experience);
        Assert.True(shares[1].QuestCredit);
        Assert.Equal(0u, shares[2].Experience);
        Assert.False(shares[2].QuestCredit);
    }

    [Fact]
    public void Distribute_GrayForTheHighestMember_HalvesPlusOne_AndHigherMembersGetNothing()
    {
        // Level 20 sees a level 13 creature gray (gray 13); level 10 does not (gray 4).
        IReadOnlyList<KillShare> shares = KillExperience.Distribute([new(20, true, false), new(10, true, false)], 13, false, false);
        uint xp = ExperienceFormulas.KillGain(10, 13, false, false);
        Assert.Equal(0u, shares[0].Experience);
        Assert.True(shares[0].QuestCredit);
        Assert.Equal((uint)((xp * (10f / 30f) / 2) + 1), shares[1].Experience);
    }

    [Fact]
    public void Distribute_GrayForEveryone_GivesCreditButNoXp()
    {
        IReadOnlyList<KillShare> shares = KillExperience.Distribute([new(60, true, false), new(60, true, false)], 10, true, true);
        Assert.All(shares, s => Assert.Equal(0u, s.Experience));
        Assert.All(shares, s => Assert.True(s.QuestCredit));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(20u, 20u)]
    [InlineData(21u, 30u)]
    [InlineData(100u, 820u)]
    public void HealthBonusFromStamina(uint stamina, uint bonus) => Assert.Equal(bonus, ExperienceFormulas.HealthBonusFromStamina(stamina));

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(20u, 20u)]
    [InlineData(21u, 35u)]
    [InlineData(100u, 1220u)]
    public void ManaBonusFromIntellect(uint intellect, uint bonus) => Assert.Equal(bonus, ExperienceFormulas.ManaBonusFromIntellect(intellect));
}
