using System.Buffers.Binary;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The Tier 3a session-link frames: every field survives a round trip, and every truncation or bad length is refused.</summary>
public sealed class GatewaySessionWireTests
{
    [Fact]
    public void Open_RoundTripsEveryField()
    {
        var request = new GatewaySessionOpenRequest(42, 7, "ACCOUNT", 5875, "203.0.113.9:50123");
        byte[] frame = GatewaySessionWire.EncodeOpen(99, request);
        Assert.Equal((byte)GatewayWorkerFrameType.SessionOpen, frame[0]);

        (long requestId, GatewaySessionOpenRequest decoded) = GatewaySessionWire.DecodeOpen(frame.AsSpan(1));
        Assert.Equal(99, requestId);
        Assert.Equal(request, decoded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenReply_RoundTripsAcceptAndRefusal(bool accepted)
    {
        GatewaySessionOpenReply reply = accepted
            ? GatewaySessionOpenReply.Accept(3)
            : GatewaySessionOpenReply.Refuse(AuthResponseCode.Banned, "banned");
        (long requestId, GatewaySessionOpenReply decoded) = GatewaySessionWire.DecodeOpenReply(GatewaySessionWire.EncodeOpenReply(5, reply).AsSpan(1));
        Assert.Equal(5, requestId);
        Assert.Equal(reply, decoded);
    }

    [Fact]
    public void Packet_RoundTripsTheEnvelopeAndPayload()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] frame = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, 11, 22, 33, 44, 55, (uint)WorldOpcode.CmsgCharEnum, payload);
        ClusterSessionPacket packet = GatewaySessionWire.DecodePacket(frame.AsMemory(1));
        Assert.Equal(11, packet.SessionGeneration);
        Assert.Equal(22, packet.AccountId);
        Assert.Equal(33UL, packet.CharacterGuid);
        Assert.Equal(44, packet.OwnerEpoch);
        Assert.Equal(55, packet.Sequence);
        Assert.Equal((uint)WorldOpcode.CmsgCharEnum, packet.Opcode);
        Assert.Equal(payload, packet.Payload.ToArray());

        // The record-based encoder writes the same bytes.
        Assert.Equal(frame, GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, packet));
    }

    [Fact]
    public void Close_RoundTrips()
    {
        byte[] frame = GatewaySessionWire.EncodeClose(8, 9, ClusterSessionDetachReason.WorkerClosed);
        Assert.Equal((8L, 9, ClusterSessionDetachReason.WorkerClosed), GatewaySessionWire.DecodeClose(frame.AsSpan(1)));
    }

    [Fact]
    public void EveryTruncation_IsRefusedAsInvalidData()
    {
        byte[] open = GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 1, "A", 5875, "127.0.0.1:1"));
        byte[] reply = GatewaySessionWire.EncodeOpenReply(1, GatewaySessionOpenReply.Refuse(AuthResponseCode.Failed, "x"));
        byte[] packet = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionServerPacket, 1, 1, 0, 1, 1, 1, [9, 9]);
        byte[] close = GatewaySessionWire.EncodeClose(1, 1, ClusterSessionDetachReason.ClientDisconnect);
        int checkedCuts = 0;
        for (int length = 0; length < open.Length - 1; length++)
        {
            Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeOpen(open.AsSpan(1, length)));
            checkedCuts++;
        }

        for (int length = 0; length < reply.Length - 1; length++)
        {
            Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeOpenReply(reply.AsSpan(1, length)));
            checkedCuts++;
        }

        for (int length = 0; length < packet.Length - 1; length++)
        {
            Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodePacket(packet.AsMemory(1, length)));
            checkedCuts++;
        }

        for (int length = 0; length < close.Length - 1; length++)
        {
            Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeClose(close.AsSpan(1, length)));
            checkedCuts++;
        }

        Assert.True(checkedCuts > 100, $"only {checkedCuts} truncations were checked");
    }

    [Fact]
    public void PayloadLengthPastTheFrame_AndNegativeTextLength_AreRefused()
    {
        byte[] packet = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionServerPacket, 1, 1, 0, 1, 1, 1, [9, 9]);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1 + 40, 4), 3); // claims 3 payload bytes, carries 2
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodePacket(packet.AsMemory(1)));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1 + 40, 4), -1);
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodePacket(packet.AsMemory(1)));

        byte[] open = GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 1, "A", 5875, "127.0.0.1:1"));
        BinaryPrimitives.WriteInt32LittleEndian(open.AsSpan(1 + 24, 4), -5); // account name length
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeOpen(open.AsSpan(1)));
    }

    [Fact]
    public void Proof_DependsOnSecretAndNonce()
    {
        byte[] nonce = new byte[GatewaySessionWire.NonceBytes];
        byte[] other = new byte[GatewaySessionWire.NonceBytes];
        other[0] = 1;
        Assert.Equal(GatewaySessionWire.ComputeProof("s", nonce), GatewaySessionWire.ComputeProof("s", nonce));
        Assert.NotEqual(GatewaySessionWire.ComputeProof("s", nonce), GatewaySessionWire.ComputeProof("t", nonce));
        Assert.NotEqual(GatewaySessionWire.ComputeProof("s", nonce), GatewaySessionWire.ComputeProof("s", other));
    }
}
