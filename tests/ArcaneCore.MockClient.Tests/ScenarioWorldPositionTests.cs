using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class ScenarioWorldPositionTests
{
    [Fact]
    public void MonsterMoveDecoderReadsCanonicalLinearDestination()
    {
        ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79994);
        byte[] body = CreatureMovePackets.BuildMove(guid, 1, 2, 3, 7, null, run: true, 1000, 11, 12, 13);

        MockMonsterMove move = ScenarioWire.MonsterMove(body);
        Assert.Equal(guid.Value, move.Guid);
        Assert.Equal((1f, 2f, 3f), (move.Start.X, move.Start.Y, move.Start.Z));
        Assert.Equal((11f, 12f, 13f), (move.Destination!.X, move.Destination.Y, move.Destination.Z));
        Assert.Equal(1000u, move.DurationMs);
        Assert.True(move.Linear);
        Assert.False(move.IsStop);
    }

    [Fact]
    public void MonsterMoveDecoderAcceptsTheShortStopForm()
    {
        ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79994);
        MockMonsterMove move = ScenarioWire.MonsterMove(CreatureMovePackets.BuildStop(guid, 4, 5, 6, 8));

        Assert.True(move.IsStop);
        Assert.Null(move.Destination);
        Assert.Equal((4f, 5f, 6f), (move.Start.X, move.Start.Y, move.Start.Z));
    }

    [Fact]
    public void MonsterMoveDecoderRejectsZeroGuidTruncationAndNonFiniteCoordinates()
    {
        ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79994);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(
            CreatureMovePackets.BuildMove(default, 1, 2, 3, 1, null, true, 100, 4, 5, 6)));

        byte[] valid = CreatureMovePackets.BuildMove(guid, 1, 2, 3, 1, null, true, 100, 4, 5, 6);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(valid[..^1]));

        byte[] nan = CreatureMovePackets.BuildMove(guid, float.NaN, 2, 3, 1, null, true, 100, 4, 5, 6);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(nan));
    }
}
