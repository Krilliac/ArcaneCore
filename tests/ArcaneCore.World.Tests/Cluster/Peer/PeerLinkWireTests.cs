using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Peer;

/// <summary>Golden bytes of the peer link frames and the decoders' length checks.</summary>
public sealed class PeerLinkWireTests
{
    private static readonly Guid Incarnation = new("11111111-2222-3333-4444-555555555555");

    private static ClusterNodeManifest Manifest() => GatewayLinkManifest.Create(ClusterRole.Worker, "node-a", Incarnation, 1, "rev", 1, 1);

    [Fact]
    public void Challenge_IsTheTypeByteThenTheNonce()
    {
        byte[] frame = PeerLinkWire.EncodeNonce(RealmServiceFrameType.Challenge, new byte[32].Select(_ => (byte)0x11).ToArray());
        Assert.Equal("01" + new string('1', 64), Convert.ToHexString(frame));
        Assert.Equal(new byte[32].Select(_ => (byte)0x11).ToArray(), PeerLinkWire.DecodeNonce(frame.AsSpan(1), "Challenge"));
    }

    [Fact]
    public void Ping_GoldenBytes_AndRoundTrip()
    {
        byte[] frame = PeerLinkWire.EncodeEcho(RealmServiceFrameType.Ping, 1, 2, new byte[] { 0xAA, 0xBB });
        Assert.Equal("05" + "0100000000000000" + "0200000000000000" + "02000000" + "AABB", Convert.ToHexString(frame));

        (long requestId, long idempotencyId, byte[] payload) = PeerLinkWire.DecodeEcho(frame.AsMemory(1), "Ping");
        Assert.Equal(1, requestId);
        Assert.Equal(2, idempotencyId);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, payload);
    }

    [Fact]
    public void Pong_GoldenBytes()
    {
        byte[] frame = PeerLinkWire.EncodeEcho(RealmServiceFrameType.Pong, 0x0102030405060708, -1, []);
        Assert.Equal("06" + "0807060504030201" + "FFFFFFFFFFFFFFFF" + "00000000", Convert.ToHexString(frame));
    }

    [Fact]
    public void RefusedHelloAck_GoldenBytes_AndRoundTrip()
    {
        byte[] frame = PeerLinkWire.EncodeHelloAck(false, "bad", null);
        Assert.Equal("04" + "00" + "03000000" + "626164", Convert.ToHexString(frame));

        (bool accepted, string reason, PeerHelloInfo? info) = PeerLinkWire.DecodeHelloAck(frame.AsMemory(1));
        Assert.False(accepted);
        Assert.Equal("bad", reason);
        Assert.Null(info);
    }

    [Fact]
    public void Hello_GoldenLayout_CarriesNodeIncarnationAndLeaseTokens()
    {
        ClusterNodeManifest manifest = Manifest();
        byte[] frame = PeerLinkWire.EncodeHello(new PeerHelloInfo(1, manifest, [new PeerLeaseToken("social", 5)]));

        byte[] manifestBytes = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(manifest)[1..];
        string expected = "03" + "0100" + Convert.ToHexString(BitConverter.GetBytes(manifestBytes.Length)) + Convert.ToHexString(manifestBytes)
            + "01000000" + "06000000" + "736F6369616C" + "0500000000000000";
        Assert.Equal(expected, Convert.ToHexString(frame));

        PeerHelloInfo decoded = PeerLinkWire.DecodeHello(frame.AsMemory(1));
        Assert.Equal((ushort)1, decoded.ProtocolVersion);
        Assert.Equal("node-a", decoded.Manifest.NodeId);
        Assert.Equal(Incarnation, decoded.Manifest.NodeIncarnation);
        Assert.Equal(new PeerLeaseToken("social", 5), Assert.Single(decoded.Leases));
    }

    [Fact]
    public void AcceptedHelloAck_RoundTripsTheHoldersInfo()
    {
        byte[] frame = PeerLinkWire.EncodeHelloAck(true, string.Empty, new PeerHelloInfo(1, Manifest(), [new PeerLeaseToken("economy", 9)]));
        (bool accepted, _, PeerHelloInfo? info) = PeerLinkWire.DecodeHelloAck(frame.AsMemory(1));
        Assert.True(accepted);
        Assert.NotNull(info);
        Assert.Equal(new PeerLeaseToken("economy", 9), Assert.Single(info.Leases));
    }

    [Fact]
    public void Decoders_RejectTruncatedOversizedAndTrailingBytes()
    {
        byte[] ping = PeerLinkWire.EncodeEcho(RealmServiceFrameType.Ping, 1, 2, new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => PeerLinkWire.DecodeEcho(ping.AsMemory(1, ping.Length - 2), "Ping"));
        Assert.Throws<InvalidDataException>(() => PeerLinkWire.DecodeEcho(new byte[ping.Length + 1].AsMemory(1), "Ping"));

        byte[] oversized = new byte[1 + 8 + 8 + 4];
        BitConverter.TryWriteBytes(oversized.AsSpan(17, 4), PeerLinkWire.MaxPayloadBytes + 1);
        Assert.Throws<InvalidDataException>(() => PeerLinkWire.DecodeEcho(oversized.AsMemory(1), "Ping"));

        byte[] hello = PeerLinkWire.EncodeHello(new PeerHelloInfo(1, Manifest(), []));
        Assert.Throws<InvalidDataException>(() => PeerLinkWire.DecodeHello(hello.AsMemory(1, hello.Length - 3)));
        Assert.Throws<InvalidDataException>(() => PeerLinkWire.DecodeNonce(new byte[31], "Proof"));
    }

    [Fact]
    public void Encoders_RejectTooManyLeasesAndOversizedPayloads()
    {
        PeerLeaseToken[] many = Enumerable.Range(0, PeerLinkWire.MaxLeases + 1).Select(i => new PeerLeaseToken("s" + i, i)).ToArray();
        Assert.Throws<ArgumentException>(() => PeerLinkWire.EncodeHello(new PeerHelloInfo(1, Manifest(), many)));
        Assert.Throws<ArgumentException>(() => PeerLinkWire.EncodeEcho(RealmServiceFrameType.Ping, 1, 1, new byte[PeerLinkWire.MaxPayloadBytes + 1]));
    }
}
