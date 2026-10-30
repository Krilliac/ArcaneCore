using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The session link over real loopback sockets with a fake worker-side session: the handshake, the envelope rules
/// (generation, account, owner epoch, sequence) and the ordering guarantee that nothing of a session overtakes its open reply.
/// </summary>
public sealed class GatewaySessionLinkTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Secret = "link-secret-0123456789";

    [Fact]
    public async Task OpenedSession_DeliversBothDirectionsInOrder_AndNothingOvertakesTheReply()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor { SendDuringOpen = true };
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        var sink = new RecordingSink();

        GatewaySessionOpenReply reply = await h.Client.OpenAsync(Request(1), sink, cts.Token);
        Assert.True(reply.Accepted);
        Assert.Equal(1, reply.OwnerEpoch);
        FakeSession session = Assert.Single(acceptor.Sessions);

        for (byte i = 1; i <= 3; i++)
        {
            Assert.True(h.Client.SendClientPacket(1, WorldOpcode.CmsgCharEnum, [i]));
        }

        await WaitUntilAsync(() => session.Delivered.Count == 3, cts.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, session.Delivered.Select(d => d.Payload[0]).ToArray());

        Assert.True(session.Output!.TrySend(WorldOpcode.SmsgCharEnum, [7]));
        await WaitUntilAsync(() => sink.Events.Count >= 4, cts.Token);
        // Sent during the open (before the reply was queued), then from Start, then the explicit send: all after "opened".
        Assert.Equal(["opened", "packet:0x" + ((ushort)WorldOpcode.SmsgNotification).ToString("X"), "packet:0x" + ((ushort)WorldOpcode.SmsgWardenData).ToString("X"),
            "packet:0x" + ((ushort)WorldOpcode.SmsgCharEnum).ToString("X")], sink.Events.Take(4).ToArray());
        Assert.Equal(0, h.Host.DroppedClientPackets);
        Assert.Equal(0, h.Client.DroppedServerPackets);
    }

    [Fact]
    public async Task ReplayedOrOlderSequence_WrongAccount_AndWrongEpoch_AreDroppedAndCounted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        Assert.True((await h.Client.OpenAsync(Request(10), new RecordingSink(), cts.Token)).Accepted);
        FakeSession session = Assert.Single(acceptor.Sessions);

        Assert.True(h.Client.SendRawClientPacket(Packet(10, 7, epoch: 1, sequence: 5, marker: 1)));
        Assert.True(h.Client.SendRawClientPacket(Packet(10, 7, epoch: 1, sequence: 5, marker: 2)));  // replay
        Assert.True(h.Client.SendRawClientPacket(Packet(10, 7, epoch: 1, sequence: 3, marker: 3)));  // older
        Assert.True(h.Client.SendRawClientPacket(Packet(10, 8, epoch: 1, sequence: 6, marker: 4)));  // another account
        Assert.True(h.Client.SendRawClientPacket(Packet(10, 7, epoch: 2, sequence: 7, marker: 5)));  // stale/foreign epoch
        Assert.True(h.Client.SendRawClientPacket(Packet(11, 7, epoch: 1, sequence: 8, marker: 6)));  // unknown generation
        Assert.True(h.Client.SendRawClientPacket(Packet(10, 7, epoch: 1, sequence: 9, marker: 7)));

        await WaitUntilAsync(() => session.Delivered.Count == 2 && h.Host.DroppedClientPackets == 5, cts.Token);
        Assert.Equal(new byte[] { 1, 7 }, session.Delivered.Select(d => d.Payload[0]).ToArray());
        Assert.Equal(5, h.Host.DroppedClientPackets);
    }

    [Fact]
    public async Task OpenWithAGenerationNotAboveTheLast_IsRefused()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        Assert.True((await h.Client.OpenAsync(Request(5), new RecordingSink(), cts.Token)).Accepted);
        GatewaySessionOpenReply stale = await h.Client.OpenAsync(Request(4), new RecordingSink(), cts.Token);
        Assert.False(stale.Accepted);
        Assert.Equal(AuthResponseCode.Failed, stale.RefusalCode);
        Assert.Single(acceptor.Sessions);
    }

    [Fact]
    public async Task AcceptorRefusal_ReachesTheGatewayWithItsCode_AndOpensNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor { Refusal = AuthResponseCode.Banned };
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        var sink = new RecordingSink();
        GatewaySessionOpenReply reply = await h.Client.OpenAsync(Request(1), sink, cts.Token);
        Assert.False(reply.Accepted);
        Assert.Equal(AuthResponseCode.Banned, reply.RefusalCode);
        Assert.Empty(sink.Events);
        Assert.Equal(0, h.Host.SessionCount);
        Assert.False(h.Client.SendClientPacket(1, WorldOpcode.CmsgCharEnum, []));
    }

    [Fact]
    public async Task Closes_TravelBothWays_AndALostLinkClosesEverySession()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        var workerClosed = new RecordingSink();
        var gatewayClosed = new RecordingSink();
        var linkLost = new RecordingSink();
        Assert.True((await h.Client.OpenAsync(Request(1), workerClosed, cts.Token)).Accepted);
        Assert.True((await h.Client.OpenAsync(Request(2), gatewayClosed, cts.Token)).Accepted);
        Assert.True((await h.Client.OpenAsync(Request(3), linkLost, cts.Token)).Accepted);
        FakeSession[] sessions = [.. acceptor.Sessions];

        sessions[0].Output!.Close(ClusterSessionDetachReason.WorkerClosed);
        await WaitUntilAsync(() => workerClosed.Events.Contains("closed:WorkerClosed"), cts.Token);

        h.Client.CloseSession(2, ClusterSessionDetachReason.ClientDisconnect);
        await WaitUntilAsync(() => sessions[1].ClosedBy == ClusterSessionDetachReason.ClientDisconnect, cts.Token);
        Assert.DoesNotContain(gatewayClosed.Events, e => e.StartsWith("closed", StringComparison.Ordinal));

        await h.Client.DisposeAsync();
        await WaitUntilAsync(() => sessions[2].ClosedBy == ClusterSessionDetachReason.LinkLost, cts.Token);
        Assert.Contains("closed:LinkLost", linkLost.Events);
        await WaitUntilAsync(() => h.Host.SessionCount == 0, cts.Token);
    }

    [Theory]
    [InlineData("wrong-secret-0123456789", "dev")]
    [InlineData(Secret, "other-content")]
    public async Task WrongSecretOrManifest_RefusesTheLink(string secret, string contentRevision)
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using GatewaySessionLinkHost host = StartHost(acceptor);
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway, contentRevision), host.BoundEndPoint, secret);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(cts.Token));
        Assert.Contains("refused", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.AdmittedLinkCount);
    }

    [Fact]
    public async Task NonLoopbackBindWithoutASecret_IsRefusedAtConstruction()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new GatewaySessionLinkHost(
            Manifest(ClusterRole.Worker), new FakeAcceptor(), () => 1, string.Empty, new IPEndPoint(IPAddress.Any, 0)));
        Assert.Contains("LinkSecret", refused.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("")]
    [InlineData("fifteen-chars!!")]
    public void AnEmptyOrShortSecret_IsRefused_OnLoopbackToo_ByBothEnds(string secret)
    {
        // An empty secret's proof is HMAC("", nonce): any local process could compute it, register as a gateway and open a session
        // for any account. Worker mode refuses to start with it on every bind address, and so does the gateway.
        ArgumentException worker = Assert.Throws<ArgumentException>(() => new GatewaySessionLinkHost(
            Manifest(ClusterRole.Worker), new FakeAcceptor(), () => 1, secret, new IPEndPoint(IPAddress.Loopback, 0)));
        Assert.Contains("Cluster:Worker:LinkSecret", worker.Message, StringComparison.Ordinal);
        ArgumentException gateway = Assert.Throws<ArgumentException>(() => new GatewaySessionLinkClient(
            Manifest(ClusterRole.Gateway), new IPEndPoint(IPAddress.Loopback, 1), secret));
        Assert.Contains("Gateway:LinkSecret", gateway.Message, StringComparison.Ordinal);
        Assert.Equal(16, GatewaySessionLinkHost.MinLinkSecretLength);
    }

    [Fact]
    public async Task AnOpenWhoseGenerationWasTakenFirst_IsQueuedFirst_SoTheWorkerAcceptsBoth()
    {
        // Client A takes generation 1 and is slow to build its request; client B takes generation 2 meanwhile. Were the generation
        // taken outside the link's lock, B's open would leave first and the worker would refuse A as stale (AUTH_FAILED).
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        long next = 0;
        long NextGeneration() => Interlocked.Increment(ref next);
        using var aBuilding = new ManualResetEventSlim();
        using var bTookItsGeneration = new ManualResetEventSlim();

        Task<GatewaySessionOpenReply> a = Task.Run(() => h.Client.OpenAsync(NextGeneration, generation =>
        {
            aBuilding.Set();
            bTookItsGeneration.Wait(TimeSpan.FromSeconds(1)); // never set while A holds the lock: B cannot take a generation before A queued
            Thread.Sleep(200); // were B not held, time for it to queue its open first
            return Request(generation);
        }, new RecordingSink(), cts.Token));
        Assert.True(aBuilding.Wait(Deadline));
        Task<GatewaySessionOpenReply> b = Task.Run(() => h.Client.OpenAsync(() =>
        {
            long generation = NextGeneration();
            bTookItsGeneration.Set();
            return generation;
        }, Request, new RecordingSink(), cts.Token));

        GatewaySessionOpenReply[] replies = await Task.WhenAll(a, b);
        Assert.All(replies, reply => Assert.True(reply.Accepted, reply.Reason));
        Assert.Equal(2, acceptor.Sessions.Count);
    }

    [Fact]
    public async Task ABurstOfConcurrentOpens_IsAcceptedWhole()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new FakeAcceptor();
        await using Harness h = await Harness.StartAsync(acceptor, cts.Token);
        long next = 0;
        GatewaySessionOpenReply[] replies = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            h.Client.OpenAsync(() => Interlocked.Increment(ref next), Request, new RecordingSink(), cts.Token))));
        Assert.All(replies, reply => Assert.True(reply.Accepted, reply.Reason));
        Assert.Equal(64, acceptor.Sessions.Count);
        Assert.Equal(64, h.Host.SessionCount);
    }

    [Fact]
    public async Task ServerPacketReplays_AndForeignEpochs_AreDroppedByTheGateway()
    {
        using var cts = new CancellationTokenSource(Deadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), (IPEndPoint)listener.LocalEndpoint, Secret);
        Task connect = client.ConnectAsync(cts.Token);
        using TcpClient raw = await listener.AcceptTcpClientAsync(cts.Token);
        await using var worker = new GatewayLinkChannel(raw);
        worker.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[GatewaySessionWire.NonceBytes]));
        Assert.Equal((byte)GatewayWorkerFrameType.LinkProof, (await worker.ReadFrameAsync(cts.Token))![0]);
        Assert.Equal((byte)GatewayWorkerFrameType.HelloRegister, (await worker.ReadFrameAsync(cts.Token))![0]);
        worker.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, string.Empty));
        await connect;

        var sink = new RecordingSink();
        Task<GatewaySessionOpenReply> open = client.OpenAsync(Request(1), sink, cts.Token);
        byte[] openFrame = (await worker.ReadFrameAsync(cts.Token))!;
        (long requestId, _) = GatewaySessionWire.DecodeOpen(openFrame.AsSpan(1));
        worker.TryEnqueue(GatewaySessionWire.EncodeOpenReply(requestId, GatewaySessionOpenReply.Accept(4)));
        Assert.True((await open).Accepted);

        foreach ((long epoch, long sequence) in new (long, long)[] { (4, 1), (4, 1), (4, 0), (3, 2), (4, 2) })
        {
            worker.TryEnqueue(GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionServerPacket, 1, 7, 0, epoch, sequence,
                (uint)WorldOpcode.SmsgNotification, [(byte)sequence]));
        }

        await WaitUntilAsync(() => sink.Events.Count == 3 && client.DroppedServerPackets == 3, cts.Token);
        Assert.Equal(["opened", "packet:0x" + ((ushort)WorldOpcode.SmsgNotification).ToString("X"), "packet:0x" + ((ushort)WorldOpcode.SmsgNotification).ToString("X")], sink.Events.ToArray());
        Assert.Equal(new byte[] { 1, 2 }, sink.Payloads.Select(p => p[0]).ToArray());
    }

    [Fact]
    public async Task AnOpenCancelledByShutdown_IsRefusedWithoutRecordingAFault()
    {
        const int account = 7123; // a subject no other test reports under
        using var cts = new CancellationTokenSource(Deadline);
        var acceptor = new BlockingAcceptor();
        using AnomalyCapture faults = AnomalyLedger.Shared.Capture(n => n.Key == "cluster/gateway-fault" && n.Subject == "account-" + account);
        GatewaySessionLinkHost host = StartHost(acceptor);
        await using var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), host.BoundEndPoint, Secret);
        await client.ConnectAsync(cts.Token);
        Task<GatewaySessionOpenReply> open = client.OpenAsync(new GatewaySessionOpenRequest(1, account, "ACCOUNT", 5875, "198.51.100.4:4000"), new RecordingSink(), cts.Token);
        _ = open.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        await acceptor.Entered.Task.WaitAsync(cts.Token);

        await host.DisposeAsync(); // cancels the token the in-flight open is awaiting on
        await acceptor.Cancelled.Task.WaitAsync(cts.Token);
        await Task.Delay(500, cts.Token); // the host's catch runs right after the acceptor rethrows

        Assert.Empty(faults.Notices);
    }

    private static GatewaySessionOpenRequest Request(long generation) => new(generation, 7, "ACCOUNT", 5875, "198.51.100.4:4000");

    private static ClusterSessionPacket Packet(long generation, int account, long epoch, long sequence, byte marker) =>
        new(generation, account, 0, epoch, sequence, (uint)WorldOpcode.CmsgCharEnum, new byte[] { marker });

    private static ClusterNodeManifest Manifest(ClusterRole role, string content = "dev") =>
        GatewayLinkManifest.Create(role, role.ToString(), Guid.NewGuid(), 1, content, 1, 1);

    private static GatewaySessionLinkHost StartHost(IGatewaySessionAcceptor acceptor)
    {
        var host = new GatewaySessionLinkHost(Manifest(ClusterRole.Worker), acceptor, () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0));
        host.Start();
        return host;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required GatewaySessionLinkHost Host { get; init; }

        public required GatewaySessionLinkClient Client { get; init; }

        public static async Task<Harness> StartAsync(FakeAcceptor acceptor, CancellationToken token)
        {
            GatewaySessionLinkHost host = StartHost(acceptor);
            var client = new GatewaySessionLinkClient(Manifest(ClusterRole.Gateway), host.BoundEndPoint, Secret);
            await client.ConnectAsync(token);
            Assert.True(client.IsConnected);
            return new Harness { Host = host, Client = client };
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private sealed class FakeAcceptor : IGatewaySessionAcceptor
    {
        public bool SendDuringOpen { get; init; }

        public AuthResponseCode? Refusal { get; init; }

        public ConcurrentQueue<FakeSession> Sessions { get; } = new();

        public async ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Refusal is { } code)
            {
                return GatewaySessionAcceptResult.Refused(code, "refused by the fake");
            }

            if (SendDuringOpen)
            {
                Assert.True(output.TrySend(WorldOpcode.SmsgNotification, [0]));
            }

            var session = new FakeSession { Output = output, SendOnStart = SendDuringOpen };
            Sessions.Enqueue(session);
            return GatewaySessionAcceptResult.Accepted(session);
        }
    }

    /// <summary>An acceptor whose open never finishes on its own: it waits on the token it is given, as a database call does.</summary>
    private sealed class BlockingAcceptor : IGatewaySessionAcceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }

    private sealed class FakeSession : IGatewayRemoteSession
    {
        public IGatewaySessionOutput? Output { get; init; }

        public bool SendOnStart { get; init; }

        public ConcurrentQueue<(WorldOpcode Opcode, byte[] Payload)> Delivered { get; } = new();

        public ClusterSessionDetachReason? ClosedBy { get; private set; }

        public void Start()
        {
            if (SendOnStart)
            {
                Output!.TrySend(WorldOpcode.SmsgWardenData, [1]);
            }
        }

        public void Deliver(WorldOpcode opcode, byte[] payload) => Delivered.Enqueue((opcode, payload));

        public void CloseFromGateway(ClusterSessionDetachReason reason) => ClosedBy = reason;
    }

    internal sealed class RecordingSink : IGatewaySessionSink
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentQueue<byte[]> Payloads { get; } = new();

        public void OnOpened(GatewaySessionOpenReply reply) => Events.Enqueue("opened");

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            Payloads.Enqueue(payload.ToArray());
            Events.Enqueue("packet:0x" + ((ushort)opcode).ToString("X"));
        }

        public void OnClosed(ClusterSessionDetachReason reason) => Events.Enqueue("closed:" + reason);
    }
}
