using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Accounts;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>Score decay, the escalation ladder under its ceiling, the autoban points and ladder, and option validation.</summary>
public sealed class AntiCheatScoresTests
{
    [Fact]
    public void Scores_AddDecayLinearly_ClampEachWeight_AndPruneWhenFullyDecayed()
    {
        var scores = new AntiCheatScores();
        Assert.Equal(25f, scores.Add(7, 25f, 1000, decayPerSecond: 2f));
        Assert.Equal(45f, scores.Add(7, 30f, 6000, 2f), 3); // 25 - 5 s * 2 + 30
        Assert.Equal(145f, scores.Add(7, 500f, 6000, 2f), 3); // one finding adds at most 100
        Assert.Equal(3, scores.Findings(7));
        Assert.Equal(0f, scores.Add(8, float.NaN, 6000, 2f));

        Assert.Equal(125f, scores.Score(7, 16000, 2f), 3);
        Assert.Equal(2, scores.Prune(80000, 2f)); // 145 - 74 s * 2 is gone, and the zero-weight entry with it
        Assert.Equal(0, scores.Count);
    }

    [Fact]
    public void Top_ListsTheHighestLiveScoresFirst_AndSetOverwrites()
    {
        var scores = new AntiCheatScores();
        scores.Add(1, 10f, 0, 0f);
        scores.Add(2, 50f, 0, 0f);
        scores.Add(3, 30f, 0, 0f);
        scores.Set(1, 70f, 0, 0f);
        scores.Set(4, -5f, 0, 0f);
        IReadOnlyList<(int CharacterId, float Score)> top = scores.Top(2, 0, 0f);
        Assert.Equal([(1, 70f), (2, 50f)], top);
        Assert.True(scores.Remove(1));
        Assert.Equal(2, scores.Top(10, 0, 0f)[0].CharacterId);
    }

    [Theory]
    [InlineData(10f, AntiCheatAction.Kick, AntiCheatAction.Log)]
    [InlineData(30f, AntiCheatAction.Kick, AntiCheatAction.GmAlert)]
    [InlineData(60f, AntiCheatAction.Kick, AntiCheatAction.Rubberband)]
    [InlineData(120f, AntiCheatAction.Kick, AntiCheatAction.Kick)]
    [InlineData(500f, AntiCheatAction.Log, AntiCheatAction.Log)] // the default ceiling: log only, whatever the score
    [InlineData(500f, AntiCheatAction.GmAlert, AntiCheatAction.GmAlert)]
    [InlineData(500f, AntiCheatAction.None, AntiCheatAction.None)]
    public void Escalation_IsTheHighestWarrantedAction_CappedByTheCeiling(float score, AntiCheatAction ceiling, AntiCheatAction expected)
        => Assert.Equal(expected, AntiCheatEscalation.Decide(score, new AntiCheatOptions { Action = ceiling }));

    [Fact]
    public void TheDefaults_AreEnabledAndLogOnly_AndValid()
    {
        var options = new AntiCheatOptions();
        Assert.True(options.Enabled);
        Assert.Equal(AntiCheatAction.Log, options.Action);
        Assert.Equal(AccountSecurity.Moderator, options.ExemptSecurity);
        Assert.True(options.ExemptManagedBots);
        Assert.False(options.Autoban.Enabled);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validation_ListsEveryNonsensicalValue()
    {
        var options = new AntiCheatOptions
        {
            Action = (AntiCheatAction)9,
            ScoreGmAlert = 100,
            ScoreRubberband = 50,
            DecayPerSecond = -1,
            TeleportDistance = 0,
            MaxLatencySlackMs = 10,
        };
        options.SpeedClock.MinSamples = 50;
        options.Autoban.FirstBanSeconds = -1;
        IReadOnlyList<string> problems = options.Validate();
        Assert.Contains(problems, p => p.Contains("Action", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("must not decrease", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("DecayPerSecond", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("TeleportDistance", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("MaxLatencySlackMs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("MinSamples", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Autoban", StringComparison.Ordinal));
    }

    [Fact]
    public void Clone_IsDeep_SoALiveEditNeverShowsHalfAChange()
    {
        var options = new AntiCheatOptions();
        AntiCheatOptions copy = options.Clone();
        copy.SpeedClock.Window = 99;
        copy.Autoban.Threshold = 1;
        copy.Log.CoalesceMs = 1;
        Assert.Equal(20, options.SpeedClock.Window);
        Assert.Equal(25f, options.Autoban.Threshold);
        Assert.Equal(5000, options.Log.CoalesceMs);
    }

    [Fact]
    public void Autoban_ThreeQuickKicksCrossTheThreshold_SpacedKicksDecayAway()
    {
        var options = new AntiCheatAutobanOptions { Enabled = true };
        Assert.Equal(25f, options.Threshold);
        var ledger = new AutobanLedger();
        Assert.False(ledger.AddKick(5, 1_000_000, options));
        Assert.False(ledger.AddKick(5, 1_000_060, options));
        Assert.True(ledger.AddKick(5, 1_000_120, options));
        Assert.Equal(0f, ledger.Points(5, 1_000_120, options)); // reset after the ban

        // One kick every ten hours never adds up: the decay of 1 point per hour takes each kick's 10 points away first.
        long now = 2_000_000;
        for (int i = 0; i < 6; i++, now += 10 * 3600)
        {
            Assert.False(ledger.AddKick(6, now, options));
        }

        Assert.Equal(10f, ledger.Points(6, now - (10 * 3600), options), 3);
    }

    [Fact]
    public void Autoban_TheLadderIsOneDaySevenDaysThenPermanent_AndTheLastStepIsSticky()
    {
        var options = new AntiCheatAutobanOptions();
        Assert.Equal(86400, options.DurationFor(0));
        Assert.Equal(604800, options.DurationFor(1));
        Assert.Equal(0, options.DurationFor(2));
        Assert.Equal(0, options.DurationFor(9));
    }
}
