using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The sink a handed-off session keeps on the promoted world: the synthesized world port frames (golden bytes) and everything else delegated to the connection.</summary>
public sealed class HandoffSinkTests
{
    // SMSG_LOGIN_VERIFY_WORLD: u32 map 1, x 1.0f, y 2.0f, z 3.0f, o 0.5f (little endian), written out by hand.
    private static readonly byte[] LoginVerify =
    [
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x80, 0x3F,
        0x00, 0x00, 0x00, 0x40,
        0x00, 0x00, 0x40, 0x40,
        0x00, 0x00, 0x00, 0x3F,
    ];

    [Fact]
    public void ALoginVerifyWorld_BecomesATransferPendingWithTheMap_AndANewWorldWithTheSameBytes()
    {
        var inner = new RecordingSink();
        var written = new List<(WorldOpcode Opcode, byte[] Payload)>();
        var tracked = new List<WorldOpcode>();
        var sink = new GatewayClientConnection.HandoffSink(inner, tracked.Add, (opcode, payload) => written.Add((opcode, payload.ToArray())), synthesizeWorldPort: true);

        sink.OnServerPacket(WorldOpcode.SmsgLoginVerifyWorld, LoginVerify);

        Assert.Equal(2, written.Count);
        Assert.Equal(WorldOpcode.SmsgTransferPending, written[0].Opcode);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, written[0].Payload);
        Assert.Equal(WorldOpcode.SmsgNewWorld, written[1].Opcode);
        Assert.Equal(LoginVerify, written[1].Payload);
        Assert.Equal([WorldOpcode.SmsgLoginVerifyWorld], tracked);
        Assert.Empty(inner.Packets);

        // Only the first one is a world port: a later login on the promoted world passes through unchanged.
        sink.OnServerPacket(WorldOpcode.SmsgLoginVerifyWorld, LoginVerify);
        Assert.Equal(2, written.Count);
        Assert.Equal([WorldOpcode.SmsgLoginVerifyWorld], inner.Packets);
    }

    [Fact]
    public void WithoutAWorldPort_EveryPacketPassesToTheConnection()
    {
        var inner = new RecordingSink();
        var written = new List<WorldOpcode>();
        var sink = new GatewayClientConnection.HandoffSink(inner, _ => { }, (opcode, _) => written.Add(opcode), synthesizeWorldPort: false);

        sink.OnServerPacket(WorldOpcode.SmsgLoginVerifyWorld, LoginVerify);
        sink.OnServerPacket(WorldOpcode.SmsgQueryTimeResponse, new byte[8]);

        Assert.Empty(written);
        Assert.Equal([WorldOpcode.SmsgLoginVerifyWorld, WorldOpcode.SmsgQueryTimeResponse], inner.Packets);
    }

    [Fact]
    public void ALoginRedirectOnThePromotedWorld_IsResolvedByTheConnection_NotRefusedByTheInterfaceDefault()
    {
        var inner = new RecordingSink { RedirectAnswer = true };
        var sink = new GatewayClientConnection.HandoffSink(inner, _ => { }, (_, _) => { }, synthesizeWorldPort: true);
        var redirect = new GatewaySessionRedirect(5, 7, 1, 0);

        Assert.True(((IGatewaySessionSink)sink).OnRedirect(redirect));
        Assert.Equal(redirect, Assert.Single(inner.Redirects));

        inner.RedirectAnswer = false;
        Assert.False(((IGatewaySessionSink)sink).OnRedirect(redirect));
    }

    [Fact]
    public void TheClose_IsDelegatedToTheConnection()
    {
        var inner = new RecordingSink();
        var sink = new GatewayClientConnection.HandoffSink(inner, _ => { }, (_, _) => { }, synthesizeWorldPort: true);

        sink.OnClosed(ClusterSessionDetachReason.WorkerClosed);

        Assert.Equal([ClusterSessionDetachReason.WorkerClosed], inner.Closes);
    }

    private sealed class RecordingSink : IGatewaySessionSink
    {
        public List<WorldOpcode> Packets { get; } = [];

        public List<GatewaySessionRedirect> Redirects { get; } = [];

        public List<ClusterSessionDetachReason> Closes { get; } = [];

        public bool RedirectAnswer { get; set; }

        public void OnOpened(GatewaySessionOpenReply reply)
        {
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Packets.Add(opcode);

        public void OnClosed(ClusterSessionDetachReason reason) => Closes.Add(reason);

        bool IGatewaySessionSink.OnRedirect(GatewaySessionRedirect redirect)
        {
            Redirects.Add(redirect);
            return RedirectAnswer;
        }
    }
}
