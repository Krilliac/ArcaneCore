using ArcaneCore.World.Cluster.Peer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Peer;

/// <summary>Pins the wire values of the peer link frame types: a renumbered, renamed or reused value breaks every deployed peer.</summary>
public sealed class RealmServiceFrameTypeTests
{
    [Fact]
    public void RealmServiceFrameType_ValuesAreStable()
    {
        // Appending a frame type means adding a row here, raising the protocol version, and updating the version pin below on purpose.
        var expected = new (string Name, byte Value)[]
        {
            ("Challenge", 1), ("Proof", 2), ("Hello", 3), ("HelloAck", 4), ("Ping", 5), ("Pong", 6),
        };

        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(RealmServiceFrameType)));
        Assert.Equal(expected.Length, Enum.GetNames<RealmServiceFrameType>().Length);
        foreach ((string name, byte value) in expected)
        {
            Assert.Equal(value, (byte)Enum.Parse<RealmServiceFrameType>(name));
        }
    }

    [Fact]
    public void RealmServiceProtocol_VersionIsTwo()
    {
        Assert.Equal((ushort)2, RealmServiceProtocol.Version);
    }
}
