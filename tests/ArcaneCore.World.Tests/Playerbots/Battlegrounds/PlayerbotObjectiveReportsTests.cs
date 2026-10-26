using System.Numerics;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// <see cref="PlayerbotObjectiveReports"/>: the battleground bot reports a refused walk once per (goal, origin) cell pair, not once per match
/// (#1498), from a set that is bounded and cleared at the match reset.
/// </summary>
public sealed class PlayerbotObjectiveReportsTests
{
    private static readonly Vector3 Goal = new(1_000f, 1_000f, 300f);
    private static readonly Vector3 Graveyard = new(100f, 200f, 300f);

    [Fact]
    public void TryAdd_ReportsAPairOnce_AndNotAgainFromTheSameCells()
    {
        var reports = new PlayerbotObjectiveReports();
        Assert.True(reports.TryAdd(Goal, Graveyard));
        Assert.False(reports.TryAdd(Goal, Graveyard));
        Assert.False(reports.TryAdd(Goal + new Vector3(1f, 1f, 0f), Graveyard + new Vector3(0.5f, 0.5f, 0f)));
        Assert.Equal(1, reports.Count);
    }

    [Fact]
    public void TryAdd_ReportsANewGoalOrANewOrigin()
    {
        var reports = new PlayerbotObjectiveReports();
        Assert.True(reports.TryAdd(Goal, Graveyard));
        Assert.True(reports.TryAdd(Goal + new Vector3(50f, 0f, 0f), Graveyard));
        Assert.True(reports.TryAdd(Goal, Graveyard + new Vector3(0f, 80f, 0f)));
        Assert.Equal(3, reports.Count);
    }

    [Fact]
    public void Clear_LetsThePairBeReportedAgain()
    {
        var reports = new PlayerbotObjectiveReports();
        Assert.True(reports.TryAdd(Goal, Graveyard));
        reports.Clear();
        Assert.Equal(0, reports.Count);
        Assert.True(reports.TryAdd(Goal, Graveyard));
    }

    [Fact]
    public void TryAdd_IsBounded_AndForgetsTheOldestPairFirst()
    {
        var reports = new PlayerbotObjectiveReports();
        for (int i = 0; i < PlayerbotObjectiveReports.Capacity + 4; i++)
            Assert.True(reports.TryAdd(Goal + new Vector3(i * 50f, 0f, 0f), Graveyard));
        Assert.Equal(PlayerbotObjectiveReports.Capacity, reports.Count);
        Assert.True(reports.TryAdd(Goal, Graveyard), "the first pair was forgotten");
        Assert.False(reports.TryAdd(Goal + new Vector3((PlayerbotObjectiveReports.Capacity + 3) * 50f, 0f, 0f), Graveyard), "the newest is kept");
    }
}
