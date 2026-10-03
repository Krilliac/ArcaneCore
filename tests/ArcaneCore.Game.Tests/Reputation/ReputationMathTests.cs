using ArcaneCore.Game.Reputation;
using Xunit;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>vmangos ReputationMgr rank arithmetic and Player::CalculateReputationGain.</summary>
public sealed class ReputationMathTests
{
    [Theory]
    [InlineData(-50000, ReputationRank.Hated)]
    [InlineData(-42000, ReputationRank.Hated)]
    [InlineData(-6001, ReputationRank.Hated)]
    [InlineData(-6000, ReputationRank.Hostile)]
    [InlineData(-3001, ReputationRank.Hostile)]
    [InlineData(-3000, ReputationRank.Unfriendly)]
    [InlineData(-1, ReputationRank.Unfriendly)]
    [InlineData(0, ReputationRank.Neutral)]
    [InlineData(2999, ReputationRank.Neutral)]
    [InlineData(3000, ReputationRank.Friendly)]
    [InlineData(8999, ReputationRank.Friendly)]
    [InlineData(9000, ReputationRank.Honored)]
    [InlineData(20999, ReputationRank.Honored)]
    [InlineData(21000, ReputationRank.Revered)]
    [InlineData(41999, ReputationRank.Revered)]
    [InlineData(42000, ReputationRank.Exalted)]
    [InlineData(42999, ReputationRank.Exalted)]
    [InlineData(int.MaxValue, ReputationRank.Exalted)]
    public void RankBoundaries_MatchTheClientTable(int reputation, ReputationRank rank)
        => Assert.Equal(rank, ReputationMath.ToRank(reputation));

    [Fact]
    public void FirstPoints_Clamp_AndTableTotals()
    {
        Assert.Equal([-42000, -6000, -3000, 0, 3000, 9000, 21000, 42000],
            Enum.GetValues<ReputationRank>().Select(ReputationMath.FirstPointOf));
        Assert.Equal(85000, ReputationMath.PointsInRank.Sum());
        Assert.Equal(ReputationMath.Cap, ReputationMath.Clamp(long.MaxValue));
        Assert.Equal(ReputationMath.Bottom, ReputationMath.Clamp(long.MinValue));
        Assert.Equal(17, ReputationMath.Clamp(17));
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(5u, 0u)]
    [InlineData(6u, 1u)]
    [InlineData(20u, 13u)]
    [InlineData(39u, 31u)]
    [InlineData(40u, 31u)]
    [InlineData(60u, 47u)]
    public void GrayLevel_FollowsVanillaFormula(uint level, uint gray)
        => Assert.Equal(gray, ReputationMath.GrayLevel(level));

    [Theory]
    [InlineData(10u, 10u, 1f)]
    [InlineData(10u, 5u, 1f)]
    [InlineData(10u, 4u, 0.8f)]
    [InlineData(10u, 3u, 0.6f)]
    [InlineData(10u, 2u, 0.4f)]
    [InlineData(10u, 1u, 0.2f)]
    [InlineData(60u, 1u, 0.2f)]
    [InlineData(1u, 60u, 1f)]
    public void QuestLevelRate_DropsTwentyPercentPerLevel_ToTwentyPercent(uint player, uint quest, float rate)
        => Assert.Equal(rate, ReputationMath.QuestLevelRate(player, quest));

    [Fact]
    public void Gains_AreScaled_LossesAreNot()
    {
        var rates = new ReputationRates();
        Assert.Equal(25f, ReputationMath.GainBeforeDither(ReputationSource.Kill, 25, 20, 14, rates));
        Assert.Equal(5f, ReputationMath.GainBeforeDither(ReputationSource.Kill, 25, 20, 13, rates), 3);
        Assert.Equal(-25f, ReputationMath.GainBeforeDither(ReputationSource.Kill, -25, 20, 1, rates));
        Assert.Equal(50f, ReputationMath.GainBeforeDither(ReputationSource.Quest, 250, 10, 1, rates), 3);
        Assert.Equal(-250f, ReputationMath.GainBeforeDither(ReputationSource.Quest, -250, 10, 1, rates));
        Assert.Equal(275f, ReputationMath.GainBeforeDither(ReputationSource.Spell, 250, 10, 1, rates, 10), 3);
        Assert.Equal(0f, ReputationMath.GainBeforeDither(ReputationSource.Spell, 250, 10, 1, rates, -150));
        rates.Gain = 2;
        rates.LowLevelKill = 0.5f;
        Assert.Equal(25f, ReputationMath.GainBeforeDither(ReputationSource.Kill, 25, 20, 1, rates));
    }

    [Theory]
    [InlineData(2.25f, 0.24, 3)]
    [InlineData(2.25f, 0.25, 2)]
    [InlineData(2.0f, 0.0, 2)]
    [InlineData(-2.5f, 0.4, -2)]
    [InlineData(-2.5f, 0.6, -3)]
    [InlineData(float.NaN, 0.0, 0)]
    [InlineData(float.PositiveInfinity, 0.0, 0)]
    public void Dither_RoundsUpWithTheFractionalProbability(float value, double roll, int expected)
        => Assert.Equal(expected, ReputationMath.Dither(value, roll));
}
