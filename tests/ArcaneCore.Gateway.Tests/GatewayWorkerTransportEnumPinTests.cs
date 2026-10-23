using ArcaneCore.World.Cluster;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>Pins the numeric values of the gateway/worker link enums; they travel on the wire as single bytes, so a file split must not renumber them.</summary>
public sealed class GatewayWorkerTransportEnumPinTests
{
    [Fact]
    public void GatewayWorkerFrameType_ValuesAreStable()
    {
        var expected = new (string Name, byte Value)[]
        {
            ("HelloRegister", 1), ("HelloAck", 2), ("AttachSessionRequest", 3), ("AttachSessionReply", 4),
            ("DetachSessionRequest", 5), ("DetachSessionReply", 6), ("ClientSessionPacket", 7),
            ("ClientSessionPacketAck", 8), ("ServerSessionPacket", 9), ("ServerSessionDetached", 10),
            ("SessionOpen", 11), ("SessionOpenReply", 12), ("SessionClientPacket", 13), ("SessionServerPacket", 14),
            ("SessionClose", 15), ("LinkChallenge", 16), ("LinkProof", 17),
        };

        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(GatewayWorkerFrameType)));
        Assert.Equal(expected.Length, Enum.GetNames<GatewayWorkerFrameType>().Length);
        foreach ((string name, byte value) in expected)
        {
            Assert.Equal(value, (byte)Enum.Parse<GatewayWorkerFrameType>(name));
        }
    }

    [Fact]
    public void ClusterSessionDetachReason_ValuesAreStable()
    {
        var expected = new (string Name, byte Value)[]
        {
            ("ClientDisconnect", 0), ("Logout", 1), ("SupersededByNewLogin", 2), ("Transferred", 3),
            ("WorkerDraining", 4), ("OwnerEpochMismatch", 5), ("WorkerClosed", 6), ("LinkLost", 7),
        };

        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(ClusterSessionDetachReason)));
        Assert.Equal(expected.Length, Enum.GetNames<ClusterSessionDetachReason>().Length);
        foreach ((string name, byte value) in expected)
        {
            Assert.Equal(value, (byte)Enum.Parse<ClusterSessionDetachReason>(name));
        }
    }
}
