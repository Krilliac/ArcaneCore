using ArcaneCore.Game.Economy;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// The accept delay after a trade modification: D:\refs\vmangos\src\game\Handlers\TradeHandler.cpp:251-257 and :657-658
/// (delay 200 ms, measured with whole-second time(): difftime(time(nullptr), last) * 1000 is 0 or at least 1000).
/// </summary>
public sealed class TradeScamPreventionTests
{
    [Fact]
    public void Defaults_FollowVmangos()
    {
        var options = new EconomyOptions();
        Assert.Equal((200u, true, true), (options.TradeScamPreventionMs, options.TradeScamPreventionWholeSeconds, options.TradeSpaceNotifications));
    }

    [Theory]
    [InlineData(5_100L, 5_900L, true)]    // same wall-clock second: refused (vmangos effective behavior)
    [InlineData(5_900L, 6_050L, false)]   // 150 ms apart but a different second: allowed, as in vmangos
    [InlineData(5_000L, 7_000L, false)]
    public void WholeSeconds_RefusesOnlyWithinTheSameSecond(long lastMs, long nowMs, bool refused)
        => Assert.Equal(refused, TradeRules.ScamPrevented(lastMs, nowMs, 200, wholeSeconds: true));

    [Theory]
    [InlineData(5_100L, 5_250L, true)]    // 150 ms: refused
    [InlineData(5_100L, 5_300L, false)]   // exactly the delay: allowed
    [InlineData(5_900L, 6_050L, true)]
    public void Precise_UsesRealMilliseconds(long lastMs, long nowMs, bool refused)
        => Assert.Equal(refused, TradeRules.ScamPrevented(lastMs, nowMs, 200, wholeSeconds: false));

    [Fact]
    public void ZeroDelayDisables_AndANeverModifiedTradeIsNotDelayed()
    {
        Assert.False(TradeRules.ScamPrevented(5_000, 5_000, 0, true));
        Assert.False(TradeRules.ScamPrevented(0, 5_000, 200, true));
        Assert.False(TradeRules.ScamPrevented(0, 5_000, 200, false));
    }
}
