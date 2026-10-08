using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

public sealed class EventAiCoverageTests
{
    [Fact]
    public void Coverage_SeparatesUsedUnsupportedTypesFromAbsentReferenceTypes()
    {
        CreatureAiEvent[] rows =
        [
            Row(1, 100, 4, 0),
            Row(2, 100, 35, 16),
            Row(3, 101, 4, 26),
            Row(4, 102, 250, 0),
        ];

        EventAiCoverageReport report = EventAiCoverage.Analyze(rows);

        Assert.Equal(4, report.Rows);
        Assert.Equal(3, report.CreaturesWithScripts);
        Assert.Equal(0, report.CreaturesFullySupported);
        Assert.Equal(1, report.UsedUnsupportedEventIds[35]);
        Assert.Equal(1, report.UsedUnsupportedEventIds[250]);
        Assert.Equal(1, report.UsedUnsupportedActionIds[16]);
        Assert.Equal(1, report.UsedUnsupportedActionIds[26]);
        Assert.Contains((byte)44, report.UnusedUnsupportedActionIds);
        Assert.DoesNotContain((byte)16, report.UnusedUnsupportedActionIds);
        Assert.DoesNotContain((byte)35, report.UnusedUnsupportedEventIds);
    }

    [Fact]
    public void Coverage_CountsAnEntireCreatureAsIncompleteWhenOneRowIsUnsupported()
    {
        EventAiCoverageReport report = EventAiCoverage.Analyze([Row(1, 100, 4, 0), Row(2, 100, 4, 44), Row(3, 101, 4, 0)]);
        Assert.Equal(2, report.CreaturesWithScripts);
        Assert.Equal(1, report.CreaturesFullySupported);
        Assert.Equal(1, report.UsedUnsupportedActionIds[44]);
    }

    [Fact]
    public void Coverage_CountsUnsupportedParameterRowsAndKeysSpawnGuidRowsSeparately()
    {
        // SPAWNED (11) accepts conditions 0-2 only (SpawnedEvent.UnsupportedReason); condition 3 is a parameter gap.
        CreatureAiEvent[] rows =
        [
            Row(1, 100, 11, 0) with { Param1 = 3 },
            Row(2, 100, 11, 0) with { CreatureGuid = 7, Param1 = 1 },
        ];

        EventAiCoverageReport report = EventAiCoverage.Analyze(rows);

        Assert.Equal(1, report.RowsWithUnsupportedParameters);
        Assert.Equal(2, report.CreaturesWithScripts);
        Assert.Equal(1, report.CreaturesFullySupported);
        Assert.Empty(report.UsedUnsupportedEventIds);
        Assert.Empty(report.UsedUnsupportedActionIds);
    }

    private static CreatureAiEvent Row(uint id, uint creature, byte eventType, byte actionType)
        => new() { Id = id, CreatureId = creature, EventType = eventType, Action1 = new CreatureAiAction(actionType, 0, 0, 0) };
}
