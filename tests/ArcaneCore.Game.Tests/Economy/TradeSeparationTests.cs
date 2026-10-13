using ArcaneCore.Game.Economy;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// vmangos Player::SetPosition (Player.cpp:5977-5979) cancels with IsWithinDistInMap(trader, INTERACTION_DISTANCE).
/// INTERACTION_DISTANCE is 5 yards (ObjectDefines.h) plus both bounding radii, strict less-than.
/// Initiate and accept still allow TRADE_DISTANCE (11.11) while both players stand still.
/// </summary>
public sealed class TradeSeparationTests
{
    [Fact]
    public void StandingStill_BetweenInteractionAndTradeDistance_DoesNotSeparate()
    {
        (var a, _) = ItemTestData.CreatePlayer(1, 0, 0);
        (var b, _) = ItemTestData.CreatePlayer(2, 10, 0);
        var trade = new TradeSession(a, b);

        Assert.True(TradeSession.InRange(a, b));
        Assert.False(trade.SeparatedByMovement());
        Assert.False(trade.SeparatedByMovement());
    }

    [Fact]
    public void TurningInPlace_PastInteractionDistance_DoesNotSeparate()
    {
        (var a, _) = ItemTestData.CreatePlayer(1, 0, 0);
        (var b, _) = ItemTestData.CreatePlayer(2, 10, 0);
        var trade = new TradeSession(a, b);

        b.Relocate(b.X, b.Y, b.Z, 1.5f, 0);

        Assert.False(trade.SeparatedByMovement());
    }

    [Fact]
    public void OneStep_PastInteractionReach_Separates_AStepInsideDoesNot()
    {
        (var nearA, _) = ItemTestData.CreatePlayer(1, 0, 0);
        (var nearB, _) = ItemTestData.CreatePlayer(2, 2, 0);
        var near = new TradeSession(nearA, nearB);
        float reach = TradeRules.InteractionDistance + nearA.BoundingRadius + nearB.BoundingRadius;
        nearB.Relocate(reach - 0.05f, 0, nearB.Z, 0, 0);
        Assert.False(near.SeparatedByMovement());

        (var farA, _) = ItemTestData.CreatePlayer(3, 0, 0);
        (var farB, _) = ItemTestData.CreatePlayer(4, 2, 0);
        var far = new TradeSession(farA, farB);
        farB.Relocate(reach + 0.05f, 0, farB.Z, 0, 0);
        Assert.True(far.SeparatedByMovement());
        Assert.True(TradeSession.InRange(farA, farB));
    }

    [Fact]
    public void EqualToTheReach_EndsTheTrade_StandingStillDoesNot()
    {
        const float reach = TradeRules.InteractionDistance;
        Assert.True(TradeRules.MovementEndsTrade(positionChanged: true, distanceSquared: reach * reach, combinedBoundingRadius: 0f));
        Assert.False(TradeRules.MovementEndsTrade(positionChanged: false, distanceSquared: reach * reach, combinedBoundingRadius: 0f));
        Assert.False(TradeRules.MovementEndsTrade(positionChanged: true, distanceSquared: MathF.BitDecrement(reach * reach), combinedBoundingRadius: 0f));
    }
}
