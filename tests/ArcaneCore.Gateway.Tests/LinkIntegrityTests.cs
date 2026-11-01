using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Link protocol v3 (gateway contract version 3): mutual proof of the link secret and a MAC tag on every frame after it. The golden
/// vectors were computed outside the code under test (HMAC-SHA256 and HKDF-SHA256 per RFC 2104 and RFC 5869) from the inputs below, so a
/// change to the construction fails here before it can silently split a mixed deployment.
/// </summary>
public sealed class LinkIntegrityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Secret = "link-secret-0123456789";
    private const string OtherSecret = "another-secret-0123456789";

    private static byte[] Range(int start) => Enumerable.Range(start, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Golden_ProofsKeysAndTag_FromFixedNonces()
    {
        byte[] listenerNonce = Range(1);
        byte[] connectorNonce = Range(0x80);

        Assert.Equal("94E1E88C683B1ADE84EA92588FC9C4A98E219E3A9193B949998FC8FF3DF1597E", Convert.ToHexString(GatewaySessionWire.ComputeProof(Secret, listenerNonce)));
        Assert.Equal("013F8544EBDB6E0781444E8C896B3C7041488DD109CBC7F947A0280B5F1C88EC", Convert.ToHexString(LinkAuth.ComputeServerProof(Secret, listenerNonce, connectorNonce)));

        LinkKeys keys = LinkAuth.DeriveKeys(Secret, listenerNonce, connectorNonce);
        Assert.Equal("DFDE6959C9E7F52A6A0A4E0CCEF1B5A191547B38E5A557E9A195FB708F74E2C4", Convert.ToHexString(keys.ClientToServer));
        Assert.Equal("62C9DB6730E8A744468194E54E9E2CE677ED7B91B6A088B549D0071686821855", Convert.ToHexString(keys.ServerToClient));

        byte[] frame = [0x14, 1, 2, 3];
        byte[] tag = new byte[LinkMac.TagBytes];
        LinkMac.Compute(keys.ClientToServer, 0, frame, tag);
        Assert.Equal("F9E595371FC884AD54940A52E0641835", Convert.ToHexString(tag));
        LinkMac.Compute(keys.ClientToServer, 1, frame, tag);
        Assert.Equal("DCBAC0BFB218A7A85F1829B660EF2220", Convert.ToHexString(tag));
        Assert.True(LinkMac.Verify(keys.ClientToServer, 1, frame, tag));
        Assert.False(LinkMac.Verify(keys.ClientToServer, 0, frame, tag)); // another position
        Assert.False(LinkMac.Verify(keys.ServerToClient, 1, frame, tag)); // the other direction
    }

    [Fact]
    public void ServerProof_IsNotTheClientProof_AndCoversBothNonces()
    {
        byte[] listenerNonce = Range(1);
        byte[] connectorNonce = Range(0x80);
        byte[] server = LinkAuth.ComputeServerProof(Secret, listenerNonce, connectorNonce);
        Assert.NotEqual(GatewaySessionWire.ComputeProof(Secret, listenerNonce), server);
        Assert.NotEqual(server, LinkAuth.ComputeServerProof(Secret, listenerNonce, Range(0x81)));
        Assert.NotEqual(server, LinkAuth.ComputeServerProof(OtherSecret, listenerNonce, connectorNonce));
    }

    [Fact]
    public void ProofFrameBody_SplitsIntoProofAndNonce_AndFlagsTheLegacyLength()
    {
        byte[] body = [.. Range(1), .. Range(0x80)];
        Assert.True(LinkAuth.TrySplitClientProof(body, out byte[] proof, out byte[] nonce, out bool legacy));
        Assert.False(legacy);
        Assert.Equal(Range(1), proof);
        Assert.Equal(Range(0x80), nonce);

        Assert.False(LinkAuth.TrySplitClientProof(Range(1), out _, out _, out legacy));
        Assert.True(legacy);
        Assert.False(LinkAuth.TrySplitClientProof(new byte[40], out _, out _, out legacy));
        Assert.False(legacy);
    }

    [Fact]
    public async Task WorkerWithoutTheSecret_IsRefusedByTheGateway_AndNeverAdmitted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();
        using var raw = new TcpClient();
        await raw.ConnectAsync(host.BoundEndPoint, cts.Token);
        NetworkStream stream = raw.GetStream();
        byte[] challenge = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))!;
        byte[] nonce = challenge[1..];

        // A proof under the wrong secret, then a registration as if it had passed: the host answers the proof with a refusal and closes.
        await GatewayLinkFraming.WriteFrameAsync(stream, LinkAuth.Frame((byte)GatewayWorkerFrameType.LinkProof, GatewaySessionWire.ComputeProof(OtherSecret, nonce), Range(0x80)), cts.Token);
        byte[] answer = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))!;
        Assert.Equal((byte)GatewayWorkerFrameType.HelloAck, answer[0]);
        Assert.Equal(0, answer[1]); // refused
        await AssertClosedAsync(stream, cts.Token);
        Assert.Equal(0, host.AdmittedLinkCount);
    }

    [Fact]
    public async Task GatewayPeerThatSendsTheLegacyProof_IsRefusedAsAnOlderProtocol()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();
        using var raw = new TcpClient();
        await raw.ConnectAsync(host.BoundEndPoint, cts.Token);
        NetworkStream stream = raw.GetStream();
        byte[] nonce = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))![1..];

        // The pre-v3 reply: the bare 32-byte proof under the right secret.
        await GatewayLinkFraming.WriteFrameAsync(stream, GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkProof, GatewaySessionWire.ComputeProof(Secret, nonce)), cts.Token);
        byte[] answer = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))!;
        Assert.Equal((byte)GatewayWorkerFrameType.HelloAck, answer[0]);
        Assert.Equal(0, answer[1]);
        Assert.Contains("older", System.Text.Encoding.UTF8.GetString(answer, 6, answer.Length - 6), StringComparison.Ordinal);
        Assert.Equal(0, host.AdmittedLinkCount);
    }

    [Fact]
    public async Task Gateway_RefusesAWorkerThatCannotProveTheSecretBack()
    {
        using var cts = new CancellationTokenSource(Deadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), (IPEndPoint)listener.LocalEndpoint, Secret);
        Task connect = client.ConnectAsync(cts.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(cts.Token);
        await using var imposter = new GatewayLinkChannel(accepted);
        byte[] nonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
        imposter.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, nonce));
        byte[] proofFrame = (await imposter.ReadFrameAsync(cts.Token))!;
        Assert.True(LinkAuth.TrySplitClientProof(proofFrame.AsSpan(1), out _, out byte[] connectorNonce, out _));

        // The imposter ignores the secret and sends an accepting ack in the clear: the gateway must not take either as proof.
        imposter.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkProof, LinkAuth.ComputeServerProof(OtherSecret, nonce, connectorNonce)));
        imposter.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Gateway_RefusesAnAcceptingAckBeforeTheWorkerProved()
    {
        using var cts = new CancellationTokenSource(Deadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), (IPEndPoint)listener.LocalEndpoint, Secret);
        Task connect = client.ConnectAsync(cts.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(cts.Token);
        await using var imposter = new GatewayLinkChannel(accepted);
        imposter.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes)));
        imposter.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task FlippedBitInAFrame_ClosesTheLink()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();
        using var raw = new TcpClient();
        await raw.ConnectAsync(host.BoundEndPoint, cts.Token);
        var gateway = await RawGateway.HandshakeAsync(raw, Secret, cts.Token);
        await gateway.RegisterAsync(Manifest(ClusterRole.Gateway), cts.Token);
        Assert.Equal(1, host.AdmittedLinkCount);

        // An untouched, correctly tagged frame is accepted (the link stays up), then the same kind of frame with one bit flipped is not.
        await gateway.SendAsync(GatewaySessionWire.EncodeLinkClock(1), tamper: false, cts.Token);
        await Task.Delay(100, cts.Token);
        Assert.Equal(1, host.AdmittedLinkCount);

        await gateway.SendAsync(GatewaySessionWire.EncodeClose(1, 1, ClusterSessionDetachReason.ClientDisconnect), tamper: true, cts.Token);
        await AssertClosedAsync(gateway.Stream, cts.Token);
        await GatewaySessionLinkTests.WaitUntilAsync(() => host.AdmittedLinkCount == 0, cts.Token);
    }

    [Fact]
    public async Task ReplayedFrame_ClosesTheLink()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();
        using var raw = new TcpClient();
        await raw.ConnectAsync(host.BoundEndPoint, cts.Token);
        var gateway = await RawGateway.HandshakeAsync(raw, Secret, cts.Token);
        await gateway.RegisterAsync(Manifest(ClusterRole.Gateway), cts.Token);
        Assert.Equal(1, host.AdmittedLinkCount);

        // A recorded frame sent again carries the tag of an earlier position: the receive counter has moved on, so it fails.
        byte[] wire = gateway.WireFor(GatewaySessionWire.EncodeLinkClock(1), tamper: false);
        await gateway.Stream.WriteAsync(wire, cts.Token);
        await gateway.Stream.WriteAsync(wire, cts.Token);
        await AssertClosedAsync(gateway.Stream, cts.Token);
        await GatewaySessionLinkTests.WaitUntilAsync(() => host.AdmittedLinkCount == 0, cts.Token);
    }

    [Fact]
    public async Task ReplayedHandshake_IsRefused()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();

        // Record a complete, valid proof from one connection.
        using var first = new TcpClient();
        await first.ConnectAsync(host.BoundEndPoint, cts.Token);
        var recorded = await RawGateway.HandshakeAsync(first, Secret, cts.Token);
        await recorded.RegisterAsync(Manifest(ClusterRole.Gateway), cts.Token);
        Assert.Equal(1, host.AdmittedLinkCount);

        // Play it back on a new connection: that connection's challenge differs, so the proof does not verify.
        using var second = new TcpClient();
        await second.ConnectAsync(host.BoundEndPoint, cts.Token);
        NetworkStream stream = second.GetStream();
        byte[] challenge = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))!;
        Assert.NotEqual(recorded.Challenge, challenge);
        await GatewayLinkFraming.WriteFrameAsync(stream, recorded.ProofFrame, cts.Token);
        byte[] answer = (await GatewayLinkFraming.ReadFrameAsync(stream, cts.Token))!;
        Assert.Equal((byte)GatewayWorkerFrameType.HelloAck, answer[0]);
        Assert.Equal(0, answer[1]);
        await AssertClosedAsync(stream, cts.Token);
        Assert.Equal(1, host.AdmittedLinkCount); // only the first, genuine link
    }

    [Fact]
    public async Task RealClientAndHost_StillTalkAfterTheHandshake_UnderTheMac()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using GatewaySessionLinkHost host = StartHost();
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), host.BoundEndPoint, Secret);
        await client.ConnectAsync(cts.Token);
        Assert.True(client.IsConnected);
        Assert.Equal(1, host.AdmittedLinkCount);
    }

    /// <summary>
    /// What a fake worker (the listening end) does for the v3 handshake against a real <see cref="GatewaySessionLinkClient"/>: challenge, check the
    /// proof, prove back, switch both directions to MAC, and read the registration. Existing fake-worker tests call this where they used to
    /// answer with a bare challenge.
    /// </summary>
    internal static async Task FakeWorkerHandshakeAsync(GatewayLinkChannel worker, string secret, CancellationToken token)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
        worker.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, nonce));
        byte[] proofFrame = (await worker.ReadFrameAsync(token))!;
        Assert.Equal((byte)GatewayWorkerFrameType.LinkProof, proofFrame[0]);
        Assert.True(LinkAuth.TrySplitClientProof(proofFrame.AsSpan(1), out byte[] proof, out byte[] connectorNonce, out _));
        Assert.Equal(GatewaySessionWire.ComputeProof(secret, nonce), proof);
        LinkKeys keys = LinkAuth.DeriveKeys(secret, nonce, connectorNonce);
        worker.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkProof, LinkAuth.ComputeServerProof(secret, nonce, connectorNonce)));
        worker.EnqueueSendKey(keys.ServerToClient);
        worker.ActivateReceiveKey(keys.ClientToServer);
        Assert.Equal((byte)GatewayWorkerFrameType.HelloRegister, (await worker.ReadFrameAsync(token))![0]);
    }

    private static ClusterNodeManifest Manifest(ClusterRole role) =>
        GatewayLinkManifest.Create(role, role.ToString(), Guid.NewGuid(), 1, ArcaneCore.World.Cluster.Gateway.GatewayManifestVersions.ContentRevisionFor(ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion), ArcaneCore.Data.Characters.CharacterDbContext.Schema.CurrentVersion, ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion);

    private static GatewaySessionLinkHost StartHost()
    {
        var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), new RefusingAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0));
        host.Start();
        return host;
    }

    /// <summary>Reads until the peer closes the link (end of stream or a reset); times out through <paramref name="token"/> if it never does.</summary>
    private static async Task AssertClosedAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (await GatewayLinkFraming.ReadFrameAsync(stream, token, GatewayLinkFraming.MaxFrameBytes + LinkMac.TagBytes) is not null)
            {
            }
        }
        catch (IOException)
        {
            // reset: closed
        }
    }

    private sealed class RefusingAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(AuthResponseCode.Unavailable, "test"));
    }

    /// <summary>A gateway written against the raw socket so a test can record, replay and corrupt bytes the real client would never produce.</summary>
    private sealed class RawGateway
    {
        private readonly LinkKeys _keys;
        private ulong _sendCounter;
        private ulong _receiveCounter;

        private RawGateway(NetworkStream stream, LinkKeys keys, byte[] challenge, byte[] proofFrame)
        {
            Stream = stream;
            _keys = keys;
            Challenge = challenge;
            ProofFrame = proofFrame;
        }

        public NetworkStream Stream { get; }

        public byte[] Challenge { get; }

        public byte[] ProofFrame { get; }

        public static async Task<RawGateway> HandshakeAsync(TcpClient raw, string secret, CancellationToken token)
        {
            NetworkStream stream = raw.GetStream();
            byte[] challenge = (await GatewayLinkFraming.ReadFrameAsync(stream, token))!;
            Assert.Equal((byte)GatewayWorkerFrameType.LinkChallenge, challenge[0]);
            byte[] nonce = challenge[1..];
            byte[] connectorNonce = RandomNumberGenerator.GetBytes(GatewaySessionWire.NonceBytes);
            byte[] proofFrame = LinkAuth.Frame((byte)GatewayWorkerFrameType.LinkProof, GatewaySessionWire.ComputeProof(secret, nonce), connectorNonce);
            await GatewayLinkFraming.WriteFrameAsync(stream, proofFrame, token);

            byte[] serverProof = (await GatewayLinkFraming.ReadFrameAsync(stream, token))!;
            Assert.Equal((byte)GatewayWorkerFrameType.LinkProof, serverProof[0]);
            Assert.Equal(LinkAuth.ComputeServerProof(secret, nonce, connectorNonce), serverProof[1..]);
            return new RawGateway(stream, LinkAuth.DeriveKeys(secret, nonce, connectorNonce), challenge, proofFrame);
        }

        /// <summary>Sends the HelloRegister under the MAC and checks the worker's accepting HelloAck (also MAC-protected).</summary>
        public async Task RegisterAsync(ClusterNodeManifest manifest, CancellationToken token)
        {
            await SendAsync(LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(manifest), tamper: false, token);
            byte[] wire = (await GatewayLinkFraming.ReadFrameAsync(Stream, token, GatewayLinkFraming.MaxFrameBytes + LinkMac.TagBytes))!;
            int length = wire.Length - LinkMac.TagBytes;
            Assert.True(LinkMac.Verify(_keys.ServerToClient, _receiveCounter++, wire.AsSpan(0, length), wire.AsSpan(length)));
            Assert.Equal((byte)GatewayWorkerFrameType.HelloAck, wire[0]);
            Assert.Equal(1, wire[1]); // accepted
        }

        public Task SendAsync(byte[] frame, bool tamper, CancellationToken token) =>
            Stream.WriteAsync(WireFor(frame, tamper), token).AsTask();

        /// <summary>The next outgoing frame as it goes on the wire: length, frame, tag for the next counter; <paramref name="tamper"/> flips one bit of the frame after tagging.</summary>
        public byte[] WireFor(byte[] frame, bool tamper)
        {
            byte[] wire = new byte[4 + frame.Length + LinkMac.TagBytes];
            BinaryPrimitives.WriteInt32LittleEndian(wire, frame.Length + LinkMac.TagBytes);
            frame.CopyTo(wire, 4);
            LinkMac.Compute(_keys.ClientToServer, _sendCounter++, frame, wire.AsSpan(4 + frame.Length, LinkMac.TagBytes));
            if (tamper)
            {
                wire[4 + frame.Length - 1] ^= 0x01;
            }

            return wire;
        }
    }
}
