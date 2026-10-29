using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationZoneTroubleTests
{
    [Fact]
    public void Record_CountsABotOncePerInterval_AndForgetsAfterTheWindow()
    {
        var trouble = new PopulationZoneTrouble();
        Guid bot = Guid.NewGuid(), other = Guid.NewGuid();
        trouble.Record(41, bot, 1000);
        trouble.Record(41, bot, 1000 + PopulationZoneTrouble.RecordIntervalSeconds - 1); // too soon: ignored
        trouble.Record(41, other, 1100);
        trouble.Record(0, bot, 1100);                                                    // zone 0 is no zone
        Assert.Equal(2, trouble.Count(41, 1200));
        Assert.Equal(0, trouble.Count(0, 1200));
        Assert.Equal(0, trouble.Count(42, 1200));

        trouble.Record(41, bot, 1000 + PopulationZoneTrouble.RecordIntervalSeconds);
        Assert.Equal(3, trouble.Count(41, 1700));

        // Only the records of the last hour count; Prune drops the rest.
        Assert.Equal(1, trouble.Count(41, 1000 + PopulationZoneTrouble.RecordIntervalSeconds + PopulationZoneTrouble.WindowSeconds - 1));
        trouble.Prune(1000 + PopulationZoneTrouble.RecordIntervalSeconds + PopulationZoneTrouble.WindowSeconds);
        Assert.Equal(0, trouble.Count(41, 1000 + PopulationZoneTrouble.RecordIntervalSeconds + PopulationZoneTrouble.WindowSeconds));
    }
}
