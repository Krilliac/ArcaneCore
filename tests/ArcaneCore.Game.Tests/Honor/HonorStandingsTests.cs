using ArcaneCore.Game.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>Weekly standing maths (vmangos HonorMgr.cpp:35-46, 468-615, 1.12 branch).</summary>
public sealed class HonorStandingsTests
{
    private static float[] Series(int n) => Enumerable.Range(1, n).Select(i => (float)((n + 1 - i) * 100)).ToArray();

    [Fact]
    public void Break_points_and_curve_for_a_pool_of_100()
    {
        HonorScoreCurve c = HonorStandings.Generate(Series(100), 0);
        Assert.Equal(new float[] { 100, 85, 70, 57, 44, 33, 23, 16, 10, 6, 4, 2, 1, 0 }, c.Brk);
        Assert.Equal(new float[] { 0, 1550, 3050, 4350, 5650, 6750, 7750, 8450, 9050, 9450, 9650, 9850, 9950, 10000, 0 }, c.Fx);
        Assert.Equal(new float[] { 0, 400, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000, 11000, 12000, 13000 }, c.Fy);
    }

    [Theory]
    [InlineData(1, 12000.0)]
    [InlineData(2, 10500.0)]
    [InlineData(3, 9750.0)]
    [InlineData(4, 9250.0)]
    [InlineData(5, 8750.0)]
    [InlineData(10, 7125.0)]
    [InlineData(25, 4850.0)]
    [InlineData(50, 2576.923)]
    [InlineData(75, 820.0)]
    [InlineData(100, 25.806)]
    public void Earning_interpolates_between_break_points(int position, double expected)
    {
        float[] cp = Series(100);
        HonorScoreCurve c = HonorStandings.Generate(cp, 0);
        Assert.InRange(HonorStandings.Earning(cp[position - 1], c), expected - 0.01, expected + 0.01);
    }

    [Fact]
    public void Position_lookup_is_one_based_and_total()
    {
        float[] cp = { 30f, 20f, 10f };
        Assert.Equal(0f, HonorStandings.CpByPosition(cp, 0));
        Assert.Equal(30f, HonorStandings.CpByPosition(cp, 1));
        Assert.Equal(10f, HonorStandings.CpByPosition(cp, 3));
        Assert.Equal(0f, HonorStandings.CpByPosition(cp, 4));
    }

    [Theory]
    [InlineData(12000f, 20000f, 28000f)]   // earning 12000 vs 4000 decay: +8000
    [InlineData(10500f, 20000f, 26500f)]
    [InlineData(500f, 0f, 500f)]           // from zero the earning is the new RP
    [InlineData(0f, 13000f, 11700f)]       // inactive: 20 % decay, delta -2600 halved -1300
    [InlineData(0f, 50000f, 47500f)]       // delta -10000 halves to -5000, clamps at -2500
    public void Decay_applies_twenty_percent_halves_losses_and_floors_at_minus_2500(float earning, float rp, float expected)
        => Assert.Equal(expected, HonorStandings.Decay(earning, rp, 0.2f));

    [Theory]
    [InlineData(1, 6500f)]
    [InlineData(29, 6500f)]
    [InlineData(30, 7150f)]
    [InlineData(35, 12025f)]
    [InlineData(36, 13325f)]
    [InlineData(39, 17225f)]
    [InlineData(40, 18850f)]
    [InlineData(43, 23725f)]
    [InlineData(44, 26000f)]
    [InlineData(52, 44200f)]
    [InlineData(53, 46800f)]
    [InlineData(60, 65000f)]
    [InlineData(61, 65000f)]
    public void Level_caps_on_rank_points(int level, float cap)
        => Assert.Equal(cap, HonorStandings.MaximumRankPointsAtLevel((byte)level));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(14)]
    public void Small_pools_never_index_out_of_range(int size)
    {
        float[] cp = Series(size);
        HonorScoreCurve c = HonorStandings.Generate(cp, 0);
        foreach (float v in cp)
        {
            Assert.True(float.IsFinite(HonorStandings.Earning(v, c)));
        }
    }

    [Fact]
    public void Pool_size_override_replaces_the_list_size()
    {
        HonorScoreCurve c = HonorStandings.Generate(Series(100), 50);
        Assert.Equal(50f, c.Brk[0]);
        Assert.Equal(42f, c.Brk[1]); // floor(0.845 * 50 + 0.5)
    }

    [Fact]
    public void Empty_standing_list_is_rejected()
        => Assert.Throws<ArgumentException>(() => HonorStandings.Generate(Array.Empty<float>(), 0));
}
