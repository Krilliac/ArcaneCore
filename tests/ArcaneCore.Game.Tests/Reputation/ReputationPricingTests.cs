using ArcaneCore.Game.Reputation;
using Xunit;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>Half-up single-precision price rounding (vmangos uint32(price * discount + 0.5f)).</summary>
public sealed class ReputationPricingTests
{
    [Theory]
    [InlineData(25u, 0.9f, 23u)]   // 22.5 + 0.5: vmangos charges 23, the old floor charged 22
    [InlineData(10u, 0.9f, 9u)]
    [InlineData(100u, 0.9f, 90u)]
    [InlineData(7u, 0.9f, 6u)]     // 6.3 -> 6
    [InlineData(55u, 0.95f, 52u)]  // 52.25 -> 52
    [InlineData(10u, 1.0f, 10u)]
    [InlineData(0u, 0.9f, 0u)]
    public void Round_IsHalfUpInSinglePrecision(uint price, float discount, uint expected)
        => Assert.Equal(expected, ReputationPricing.Round(price, discount));

    [Fact]
    public void Round_ClampsOverflowAndNegativeResults()
    {
        Assert.Equal(uint.MaxValue, ReputationPricing.Round(ulong.MaxValue, 1f));
        Assert.Equal(0u, ReputationPricing.Round(100, -1f));
    }
}
