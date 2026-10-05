using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Economy;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

public sealed class TradePendingSpellTests
{
    [Fact]
    public void PendingEnchantment_IsSideLocalUntilTheSessionClearsBoth()
    {
        (Player first, _) = ItemTestData.CreatePlayer(1, 0, 0);
        (Player second, _) = ItemTestData.CreatePlayer(2, 1, 0);
        var trade = new TradeSession(first, second);
        var firstPending = new PendingTradeEnchantment(7001, ObjectGuid.Item(77));
        var secondPending = new PendingTradeEnchantment(7002, ObjectGuid.Item(78));

        trade.Initiator.PendingEnchantment = firstPending;
        trade.Target.PendingEnchantment = secondPending;

        Assert.Equal(firstPending, trade.SideOf(first).PendingEnchantment);
        Assert.Equal(secondPending, trade.SideOf(second).PendingEnchantment);
        Assert.True(trade.Involves(first));
        Assert.True(trade.Involves(second));

        trade.ClearPendingEnchantments();

        Assert.Null(trade.Initiator.PendingEnchantment);
        Assert.Null(trade.Target.PendingEnchantment);
    }

    [Fact]
    public void PendingEnchantment_PreservesSpellAndCastItemIdentity()
    {
        var pending = new PendingTradeEnchantment(12345, ObjectGuid.Item(0x1234));
        Assert.Equal(12345u, pending.SpellId);
        Assert.Equal(ObjectGuid.Item(0x1234), pending.CastItemGuid);
    }
}
