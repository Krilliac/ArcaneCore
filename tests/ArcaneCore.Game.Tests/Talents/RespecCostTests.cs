using ArcaneCore.Game.Talents;
using Xunit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Respec economy. Reference: vmangos src/game/Objects/Player.cpp:4028-4073 (UpdateResetTalentsMultiplier,
/// GetResetTalentsCost), :4132-4142 (post-respec), World.cpp:544-549 (rates), Common.h:131 (MONTH = 30 days).
/// </summary>
public sealed class RespecCostTests
{
    private const uint Gold = 10000;
    private const long Month = 30L * 86400;
    private const long T0 = 1_700_000_000;

    private static readonly TalentOptions Retail = new();

    [Fact]
    public void OptionDefaults_AreTheVmangosRetailValues()
    {
        var options = new TalentOptions();
        Assert.Equal(1.0, options.PointsRate);
        Assert.Equal(1u, options.RespecBaseCostGold);
        Assert.Equal(5u, options.RespecMultiplicativeCostGold);
        Assert.Equal(2u, options.RespecMinMultiplier);
        Assert.Equal(10u, options.RespecMaxMultiplier);
        Assert.True(options.RespecPriceDecay);
        Assert.False(options.IdempotentRespecDecay);
        Assert.Equal(Month, RespecCost.MonthSeconds);
    }

    [Theory]
    [InlineData(0u, 1u * Gold)]
    [InlineData(1u, 5u * Gold)]
    [InlineData(2u, 10u * Gold)]
    [InlineData(9u, 45u * Gold)]
    [InlineData(10u, 50u * Gold)]
    [InlineData(11u, 50u * Gold)]   // a stored value over the cap is clamped to the cap
    public void Copper_FollowsTheMultiplier(uint multiplier, uint expected)
        => Assert.Equal(expected, RespecCost.Copper(multiplier, Retail));

    [Fact]
    public void Copper_UsesTheConfiguredCosts()
    {
        var options = new TalentOptions { RespecBaseCostGold = 3, RespecMultiplicativeCostGold = 7, RespecMaxMultiplier = 4 };
        Assert.Equal(3u * Gold, RespecCost.Copper(0, options));
        Assert.Equal(14u * Gold, RespecCost.Copper(2, options));
        Assert.Equal(28u * Gold, RespecCost.Copper(9, options));
    }

    [Fact]
    public void Copper_SaturatesInsteadOfWrapping()
    {
        var options = new TalentOptions { RespecMultiplicativeCostGold = uint.MaxValue, RespecMaxMultiplier = uint.MaxValue };
        Assert.Equal(uint.MaxValue, RespecCost.Copper(uint.MaxValue, options));
    }

    [Theory]
    [InlineData(10u, 3, 7u)]     // plain decay
    [InlineData(1u, 1, 0u)]      // months (1) is not greater than the multiplier (1): 1 - 1 = 0
    [InlineData(2u, 5, 2u)]      // months > multiplier -> 0, then clamped back to the minimum because the stored value was >= it
    [InlineData(3u, 1, 2u)]      // 3 - 1 = 2
    [InlineData(3u, 2, 2u)]      // 3 - 2 = 1, clamped up to 2
    [InlineData(1u, 9, 0u)]      // stored below the minimum: no clamp
    [InlineData(10u, 0, 10u)]    // under a month: untouched
    public void Decay_FollowsUpdateResetTalentsMultiplier(uint stored, int months, uint expected)
    {
        var state = new RespecState(stored, T0);
        RespecState decayed = RespecCost.Decay(state, T0 + (months * Month) + 5, Retail);
        Assert.Equal(expected, decayed.Multiplier);
        Assert.Equal(T0, decayed.TimeUnix);   // vmangos decays the multiplier but never advances the time
    }

    [Fact]
    public void Decay_IgnoresAClockBeforeTheLastRespec_AndHonoursTheDecaySwitch()
    {
        Assert.Equal(5u, RespecCost.Decay(new RespecState(5, T0), T0 - 10 * Month, Retail).Multiplier);
        var noDecay = new TalentOptions { RespecPriceDecay = false };
        Assert.Equal(10u, RespecCost.Decay(new RespecState(10, T0), T0 + 8 * Month, noDecay).Multiplier);
    }

    [Fact]
    public void Quote_PriceMatchesTheDecayedMultiplier()
    {
        RespecQuote quote = RespecCost.Quote(new RespecState(10, T0), T0 + (3 * Month), Retail);
        Assert.Equal(35u * Gold, quote.Cost);
        Assert.Equal(7u, quote.Effective.Multiplier);
    }

    [Fact]
    public void Quote_ReproducesVmangosRepeatedDecayByDefault()
    {
        // vmangos mutates m_resetTalentsMultiplier on every cost read without moving the time, so reading the
        // price twice in a later month decays it twice (Player.cpp:4028-4051).
        RespecState stored = new(10, T0);
        long now = T0 + Month + 1;
        RespecQuote first = RespecCost.Quote(stored, now, Retail);
        Assert.Equal(9u, first.Effective.Multiplier);
        Assert.Equal(first.Effective, first.ToStore);
        RespecQuote second = RespecCost.Quote(first.ToStore, now, Retail);
        Assert.Equal(8u, second.Effective.Multiplier);
    }

    [Fact]
    public void Quote_IdempotentSwitch_DoesNotPersistTheDecay()
    {
        var options = new TalentOptions { IdempotentRespecDecay = true };
        RespecState stored = new(10, T0);
        long now = T0 + Month + 1;
        RespecQuote first = RespecCost.Quote(stored, now, options);
        Assert.Equal(9u, first.Effective.Multiplier);
        Assert.Equal(stored, first.ToStore);
        Assert.Equal(first.Effective, RespecCost.Quote(first.ToStore, now, options).Effective);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 2u)]
    [InlineData(9u, 10u)]
    [InlineData(10u, 10u)]
    [InlineData(40u, 10u)]
    public void AfterRespec_AdvancesTheMultiplierAndStampsTheTime(uint effective, uint expected)
    {
        RespecState after = RespecCost.AfterRespec(new RespecState(effective, T0), T0 + 99, Retail);
        Assert.Equal(expected, after.Multiplier);
        Assert.Equal(T0 + 99, after.TimeUnix);
    }

    [Fact]
    public void TheCostChainIsOneFiveTenUpToFifty()
    {
        RespecState state = new(0, 0);
        long now = 1_000;
        uint[] expected = [1, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 50];
        foreach (uint gold in expected)
        {
            RespecQuote quote = RespecCost.Quote(state, now, Retail);
            Assert.Equal(gold * Gold, quote.Cost);
            state = RespecCost.AfterRespec(quote.Effective, now, Retail);
        }
    }
}
