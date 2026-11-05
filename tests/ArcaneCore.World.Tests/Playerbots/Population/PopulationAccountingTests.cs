using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>The out-of-band count, the held-back note in the action log and in the band-deficit note (pure logic, no host).</summary>
public sealed class PopulationAccountingTests
{
    private static PopulationBand[] Bands() =>
    [
        new PopulationBand { MinLevel = 1, MaxLevel = 10, Weight = 1 },
        new PopulationBand { MinLevel = 20, MaxLevel = 30, Weight = 1 },
    ];

    [Fact]
    public void PopulationCounts_ALevelInNoBand_IsCountedOutOfBand_AndFillsNoBand()
    {
        PopulationCounts counts = PopulationCounts.Of(Bands(), 10, 4, 4, [(byte)5, (byte)15, (byte)25, (byte)15]);

        Assert.Equal(2, counts.OutOfBand);
        Assert.Equal([1, 1], counts.Have);
    }

    [Fact]
    public void PopulationCounts_EveryLevelInABand_HasNoneOutOfBand()
    {
        PopulationCounts counts = PopulationCounts.Of(Bands(), 10, 2, 2, [(byte)5, (byte)25]);

        Assert.Equal(0, counts.OutOfBand);
        Assert.Equal([1, 1], counts.Have);
    }

    [Fact]
    public void PopulationActionLog_ANoteOnlyPass_IsHandedOutWithHeldBack_AndTheNoteIsClearedAfterwards()
    {
        var log = new PopulationActionLog();
        var pass = new PopulationPassActions { Note = "no start area has room" };
        Assert.True(pass.Any);
        log.Add(pass);

        string? line = log.Due(0, 60);
        Assert.NotNull(line);
        Assert.Contains("parked 0, started 0, created 0, moved 0 in 1 pass", line);
        Assert.EndsWith("; held back: no start area has room", line);

        // A later pass with a change but no note: the old note is gone.
        log.Add(new PopulationPassActions { Created = 1 });
        string? next = log.Due(61_000, 60);
        Assert.NotNull(next);
        Assert.DoesNotContain("held back", next);
    }

    [Fact]
    public void PopulationActionLog_APassWithNothingAndNoNote_AddsNothing()
    {
        var log = new PopulationActionLog();
        var pass = new PopulationPassActions();
        Assert.False(pass.Any);
        log.Add(pass);

        Assert.Null(log.Due(0, 60));
    }

    [Fact]
    public void PopulationActionLog_TheLastNoteOfTheWindowWins()
    {
        var log = new PopulationActionLog();
        log.Add(new PopulationPassActions { Note = "first" });
        log.Add(new PopulationPassActions { Note = "second" });

        string? line = log.Due(0, 60);
        Assert.Contains("in 2 passes", line);
        Assert.EndsWith("; held back: second", line);
    }

    [Fact]
    public void BandDeficitNote_CarriesTheHeldBackNote_WhenThePassHeldSomethingBack()
    {
        Assert.Equal("under-budget; held back: terrain", PlayerbotPopulationFeature.BandDeficitNote("under-budget", "terrain"));
        Assert.Equal("under-budget", PlayerbotPopulationFeature.BandDeficitNote("under-budget", null));
    }
}
