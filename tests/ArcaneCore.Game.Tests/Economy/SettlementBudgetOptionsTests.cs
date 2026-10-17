using ArcaneCore.Game.Economy;
using ArcaneCore.Game.GameObjects;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>The economy and dungeon chest loot settlement budgets: shipped 15 s, configurable, never below one second.</summary>
public sealed class SettlementBudgetOptionsTests
{
    [Fact]
    public void Budgets_DefaultToTheShippedFifteenSeconds()
    {
        Assert.Equal(15, new EconomyOptions().SettlementBudgetSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), new EconomyOptions().SettlementBudget);
        Assert.Equal(15, new GameObjectOptions().LootSettlementBudgetSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), new GameObjectOptions().LootSettlementBudget);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void Budgets_FollowTheConfiguredSeconds_ButNeverBelowOne(int configured, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new EconomyOptions { SettlementBudgetSeconds = configured }.SettlementBudget);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new GameObjectOptions { LootSettlementBudgetSeconds = configured }.LootSettlementBudget);
    }
}
