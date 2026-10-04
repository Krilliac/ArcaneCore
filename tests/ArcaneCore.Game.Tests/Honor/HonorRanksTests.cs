using ArcaneCore.Game.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>Rank thresholds and the rank bar byte (vmangos HonorMgr.cpp:909-913, 980-1033).</summary>
public sealed class HonorRanksTests
{
    // 60000 sits exactly on "rankPoints > (rankCount - 1) * 5000" (not greater), so it is rank 17 + 1.
    [Theory]
    [InlineData(0f, 0, 0, 0f, 2000f)]
    [InlineData(1f, 5, 1, 0f, 2000f)]
    [InlineData(1999f, 5, 1, 0f, 2000f)]
    [InlineData(2000f, 6, 2, 2000f, 5000f)]
    [InlineData(4999f, 6, 2, 2000f, 5000f)]
    [InlineData(5000f, 7, 3, 5000f, 10000f)]
    [InlineData(9999f, 7, 3, 5000f, 10000f)]
    [InlineData(10000f, 8, 4, 10000f, 15000f)]
    [InlineData(30000f, 12, 8, 30000f, 35000f)]
    [InlineData(55000f, 17, 13, 55000f, 60000f)]
    [InlineData(60000f, 18, 14, 60000f, 65000f)]
    [InlineData(64999f, 18, 14, 60000f, 65000f)]
    [InlineData(65000f, 18, 14, 60000f, 65000f)]
    [InlineData(70000f, 18, 14, 60000f, 65000f)]
    public void Positive_rank_points_map_to_internal_and_visual_ranks(float rp, int rank, int visual, float min, float max)
    {
        HonorRankInfo info = HonorRanks.Calculate(rp);
        Assert.Equal((byte)rank, info.Rank);
        Assert.Equal((sbyte)visual, info.VisualRank);
        Assert.Equal(min, info.MinRp);
        Assert.Equal(max, info.MaxRp);
        Assert.True(info.Positive);
    }

    [Theory]
    [InlineData(-1f, 4, -4)]
    [InlineData(-1999f, 4, -4)]
    [InlineData(-2000f, 3, -3)]
    [InlineData(-5000f, 2, -2)]
    [InlineData(-20000f, 1, -1)]
    public void Negative_rank_points_map_to_dishonor_ranks(float rp, int rank, int visual)
    {
        HonorRankInfo info = HonorRanks.Calculate(rp);
        Assert.Equal((byte)rank, info.Rank);
        Assert.Equal((sbyte)visual, info.VisualRank);
        Assert.False(info.Positive);
    }

    [Theory]
    [InlineData(1000f, 127)]
    [InlineData(2000f, 0)]
    [InlineData(3500f, 127)]
    [InlineData(65000f, 255)]
    [InlineData(0f, 0)]
    public void Rank_bar_is_the_truncated_fraction_of_the_current_band(float rp, int bar)
        => Assert.Equal((byte)bar, HonorRanks.RankBar(rp, HonorRanks.Calculate(rp)));

    [Fact]
    public void Counts_match_the_retail_rank_table()
    {
        Assert.Equal(4, HonorRanks.NegativeRankCount);
        Assert.Equal(15, HonorRanks.PositiveRankCount);
        Assert.Equal(19, HonorRanks.RankCount);
        Assert.Equal(488f, HonorKillPoints.RacialLeaderHonor);
    }
}
