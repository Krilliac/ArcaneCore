using ArcaneCore.Game.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>Honorable and dishonorable kill points (vmangos Formulas.h:179-224, HonorMgr.cpp:1035-1050).</summary>
public sealed class HonorKillPointsTests
{
    [Theory]
    [InlineData(0, 188.3)]
    [InlineData(1, 198.61)]
    [InlineData(2, 209.49)]
    [InlineData(3, 220.96)]
    [InlineData(4, 233.06)]
    [InlineData(5, 245.82)]
    [InlineData(6, 259.28)]
    [InlineData(7, 273.47)]
    [InlineData(8, 288.45)]
    [InlineData(9, 304.24)]
    [InlineData(10, 320.9)]
    [InlineData(11, 338.47)]
    [InlineData(12, 357.01)]
    [InlineData(13, 376.56)]
    [InlineData(14, 397.17)]
    public void Victim_rank_raises_the_gain_by_e_to_the_0_05331_per_rank(int victimRank, double expected)
        => Assert.InRange(HonorKillPoints.Honorable(60, 60, (sbyte)victimRank, 0, 1), expected - 0.01, expected + 0.01);

    [Theory]
    [InlineData(0u, 188.3)]
    [InlineData(1u, 169.47)]
    [InlineData(2u, 150.64)]
    [InlineData(3u, 131.81)]
    [InlineData(4u, 112.98)]
    [InlineData(5u, 94.15)]
    [InlineData(6u, 75.32)]
    [InlineData(7u, 56.49)]
    [InlineData(8u, 37.66)]
    [InlineData(9u, 18.83)]
    [InlineData(10u, 0.0)]
    [InlineData(25u, 0.0)]
    public void Same_victim_repeats_lose_ten_percent_each_and_stop_at_ten(uint totalKills, double expected)
        => Assert.InRange(HonorKillPoints.Honorable(60, 60, 0, totalKills, 1), expected - 0.01, expected + 0.01);

    [Theory]
    [InlineData(60u, 62u, 207.13)]
    [InlineData(60u, 57u, 155.07)]
    [InlineData(60u, 47u, 0.0)] // GrayLevel(60) == 47: no honor
    [InlineData(60u, 48u, 55.38)]
    [InlineData(30u, 30u, 64.66)]
    [InlineData(20u, 20u, 38.98)]
    [InlineData(50u, 55u, 215.68)]
    public void Level_coefficient_and_level_factor_apply(uint killer, uint victim, double expected)
        => Assert.InRange(HonorKillPoints.Honorable(killer, victim, 0, 0, 1), expected - 0.01, expected + 0.01);

    [Fact]
    public void Killers_at_or_below_level_19_use_the_lowest_coefficient()
        => Assert.InRange(HonorKillPoints.Honorable(19, 19, 0, 0, 1), (0.1212 * 188.3) - 0.01, (0.1212 * 188.3) + 0.01);

    [Fact]
    public void Group_size_divides_the_gain()
        => Assert.InRange(HonorKillPoints.Honorable(60, 60, 0, 0, 3), (188.3 / 3) - 0.01, (188.3 / 3) + 0.01);

    [Fact]
    public void Zero_group_size_yields_nothing()
        => Assert.Equal(0f, HonorKillPoints.Honorable(60, 60, 0, 0, 0));

    [Fact]
    public void Negative_victim_rank_is_clamped_instead_of_the_vmangos_uint32_wrap_to_infinity()
    {
        // vmangos passes a negative int8 visual rank as uint32 (exp() overflows to +inf). Pinned guard.
        float v = HonorKillPoints.Honorable(60, 60, -3, 0, 1);
        Assert.True(float.IsFinite(v));
        Assert.InRange(v, 188.29, 188.31);
    }

    [Theory]
    [InlineData(1, 10.0)]
    [InlineData(29, 10.0)]
    [InlineData(30, 11.5)]
    [InlineData(35, 19.0)]
    [InlineData(36, 21.0)]
    [InlineData(41, 31.0)]
    [InlineData(42, 34.2)]
    [InlineData(50, 59.8)]
    [InlineData(51, 64.0)]
    [InlineData(55, 80.0)]
    [InlineData(60, 100.0)]
    [InlineData(70, 100.0)]
    public void Dishonorable_kill_points_follow_the_level_bands(int level, double expected)
        => Assert.InRange(HonorKillPoints.Dishonorable((byte)level), expected - 0.01, expected + 0.01);
}
