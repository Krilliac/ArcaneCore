using ArcaneCore.Game.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>The honor part of Player::GetReputationPriceDiscount (vmangos Player.cpp:19470-19510).</summary>
public sealed class HonorPriceDiscountTests
{
    [Theory]
    [InlineData(72u)]
    [InlineData(47u)]
    [InlineData(54u)]
    [InlineData(69u)]
    [InlineData(730u)]
    [InlineData(68u)]
    [InlineData(76u)]
    [InlineData(81u)]
    [InlineData(530u)]
    [InlineData(729u)]
    public void The_ten_capital_and_battleground_factions_get_the_vendor_discount_from_visual_rank_3(uint faction)
    {
        Assert.Equal(1.0f, HonorPriceDiscount.Apply(1.0f, 2, faction, taxi: false));
        Assert.Equal(1.0f - 0.1f, HonorPriceDiscount.Apply(1.0f, 3, faction, taxi: false));
        Assert.Equal(1.0f - 0.1f, HonorPriceDiscount.Apply(1.0f, 14, faction, taxi: false));
    }

    [Fact]
    public void The_honor_discount_is_additive_with_the_honored_discount()
    {
        float honored = 1.0f - 0.1f;
        Assert.Equal(1.0f - 0.1f - 0.1f, HonorPriceDiscount.Apply(honored, 3, 76, taxi: false)); // 0.8, not 0.9 * 0.9 = 0.81
        Assert.NotEqual(0.81f, HonorPriceDiscount.Apply(honored, 3, 76, taxi: false));
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 0.95)]
    [InlineData(3, 0.95)]
    [InlineData(4, 0.90)]
    [InlineData(10, 0.90)]
    public void Flight_masters_take_five_percent_at_rank_2_and_five_more_at_rank_4(int visualRank, double expected)
        => Assert.Equal((float)expected, HonorPriceDiscount.Apply(1.0f, (sbyte)visualRank, 72, taxi: true), 0.0001f);

    [Fact]
    public void An_unlisted_faction_or_no_faction_gets_no_honor_discount()
    {
        Assert.Equal(1.0f, HonorPriceDiscount.Apply(1.0f, 14, 529, taxi: false)); // Argent Dawn
        Assert.Equal(1.0f, HonorPriceDiscount.Apply(1.0f, 14, 0, taxi: false));
        Assert.Equal(1.0f, HonorPriceDiscount.Apply(1.0f, 14, 529, taxi: true));
        Assert.False(HonorPriceDiscount.Applies(0));
        Assert.True(HonorPriceDiscount.Applies(729));
    }

    [Fact]
    public void A_vendor_discount_does_not_apply_to_flight_masters_and_the_other_way_round()
    {
        Assert.Equal(1.0f, HonorPriceDiscount.Apply(1.0f, 1, 72, taxi: false));
        Assert.Equal(0.9f, HonorPriceDiscount.Apply(1.0f, 3, 72, taxi: false), 0.0001f);
        Assert.Equal(0.95f, HonorPriceDiscount.Apply(1.0f, 3, 72, taxi: true), 0.0001f);
    }
}
