using ArcaneCore.Game.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

public sealed class QuestNpcOptionsTests
{
    [Fact]
    public void SettlementBudget_DefaultsToTheShippedFiveSeconds()
    {
        var options = new QuestNpcOptions();

        Assert.Equal(5, options.SettlementBudgetSeconds);
        Assert.Equal(TimeSpan.FromSeconds(5), options.SettlementBudget);
    }

    [Theory]
    [InlineData(45, 45)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void SettlementBudget_FollowsTheConfiguredSeconds_ButNeverBelowOne(int configured, int expectedSeconds)
    {
        var options = new QuestNpcOptions { SettlementBudgetSeconds = configured };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.SettlementBudget);
    }
}
