using ArcaneCore.World.Cluster.Peer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Peer;

/// <summary>Pins the wire values of the peer link frame types: a renumbered or reused value breaks every deployed peer.</summary>
public sealed class RealmServiceFrameTypeTests
{
    [Theory]
    [InlineData(RealmServiceFrameType.Challenge, 1)]
    [InlineData(RealmServiceFrameType.Proof, 2)]
    [InlineData(RealmServiceFrameType.Hello, 3)]
    [InlineData(RealmServiceFrameType.HelloAck, 4)]
    [InlineData(RealmServiceFrameType.Ping, 5)]
    [InlineData(RealmServiceFrameType.Pong, 6)]
    public void FrameType_KeepsItsWireValue(RealmServiceFrameType type, byte expected)
    {
        Assert.Equal(expected, (byte)type);
    }

    [Fact]
    public void FrameTypes_AreExactlyTheSixPinnedOnes_AtProtocolVersionOne()
    {
        // Appending a frame type means adding a row above, raising the version, and updating these two numbers on purpose.
        Assert.Equal(6, Enum.GetValues<RealmServiceFrameType>().Length);
        Assert.Equal((ushort)1, RealmServiceProtocol.Version);
    }
}
