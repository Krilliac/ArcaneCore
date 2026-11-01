using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Review follow-ups of the gateway side of the control channel (design _wf/handoff-control-DESIGN.md, commits C10, C13 and C14): the structured <see cref="ControlReasonCode"/> on the wire and
/// in the gateway's answers, the ship passenger's stay-aboard intent, the wake-up of a worker link's retry backoff by a retarget, a flushed hold whose link died driven through the real
/// <see cref="GatewayHandoffService"/>, and the operator restart relay when the gateway does not host it, stops or does not answer. Real gateway, real links and real worlds; the only fake is
/// the process boundary (a restart request the supervisor would take is never made in these tests).
/// </summary>
public sealed class GatewayControlHardeningTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private const string Secret = "control-hardening-link-secret-0123456789";
    private const string NodeA = "harden-a";
    private const string NodeB = "harden-b";
    private const string OperatorAccount = "HARDENOP";
    private static readonly Guid Op = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Inc = new("ffeeddcc-bbaa-9988-7766-554433221100");

    // ------------------------------------------------------------------ (2) the reason code on the wire

    [Fact]
    public void ReasonCode_Values_ArePinned()
    {
        // Wire values: a new code is appended, none is renumbered or reused.
        (ControlReasonCode Code, byte Value)[] pinned =
        [
            (ControlReasonCode.None, 0), (ControlReasonCode.Unspecified, 1), (ControlReasonCode.NotHosted, 2), (ControlReasonCode.StandbyLink, 3),
            (ControlReasonCode.UnsupportedKind, 4), (ControlReasonCode.GatewayStopping, 5), (ControlReasonCode.NotATransferSlot, 6), (ControlReasonCode.SessionNotOpen, 7),
            (ControlReasonCode.NoEligibleWorker, 8), (ControlReasonCode.TargetNotReady, 9), (ControlReasonCode.TargetAtCapacity, 10), (ControlReasonCode.SameWorker, 11),
            (ControlReasonCode.MultipleGateways, 12), (ControlReasonCode.WorkerUnreachable, 13), (ControlReasonCode.SlotBusy, 14), (ControlReasonCode.Unresolved, 15),
            (ControlReasonCode.AlreadyRunning, 16), (ControlReasonCode.GatewayFailure, 17), (ControlReasonCode.TransferAborted, 18),
        ];
        Assert.Equal(pinned.Length, Enum.GetValues<ControlReasonCode>().Length);
        Assert.All(pinned, p => Assert.Equal(p.Value, (byte)p.Code));
    }

    [Fact]
    public void ControlSubmitReply_CarriesTheCodeAsItsLastByte_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(13, false, Op, "no", ControlReasonCode.TargetAtCapacity));
        byte[] expected = [31, .. BitConverter.GetBytes(13UL), 0, .. Op.ToByteArray(), 2, 0, 0, 0, (byte)'n', (byte)'o', 10];
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void ControlNotice_CarriesTheCodeAsItsLastByte_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, 5, 7, 9, Op, ControlNoticeOutcome.Aborted, "x", ControlReasonCode.TransferAborted));
        byte[] expected =
        [
            32, 2, .. BitConverter.GetBytes(5L), .. BitConverter.GetBytes(7), .. BitConverter.GetBytes(9UL), .. Op.ToByteArray(), 2, 1, 0, 0, 0, (byte)'x', 18,
        ];
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void EveryReasonCode_RoundTrips_OnTheReplyAndOnTheNotice()
    {
        foreach (ControlReasonCode code in Enum.GetValues<ControlReasonCode>())
        {
            var reply = new ControlSubmitReply(8, code == ControlReasonCode.None, Op, "text", code);
            Assert.Equal(reply, ClusterControlWire.DecodeSubmitReply(ClusterControlWire.EncodeSubmitReply(reply).AsSpan(1)));
            var notice = new ControlNotice(ControlNoticeKind.TransferOutcome, 3, 4, 5, Op, ControlNoticeOutcome.Refused, "text", code);
            Assert.Equal(notice, ClusterControlWire.DecodeNotice(ClusterControlWire.EncodeNotice(notice).AsSpan(1)));
        }

        // A frame built without a code means None.
        Assert.Equal(ControlReasonCode.None, new ControlSubmitReply(1, true, Op, string.Empty).Code);
        Assert.Equal(ControlReasonCode.None, new ControlNotice(ControlNoticeKind.OperatorText, 0, 1, 0, Op, ControlNoticeOutcome.None, "t").Code);
    }

    [Fact]
    public void AnUnknownReasonCode_IsRefusedByTheEncoderAndTheDecoder_AndAFrameWithoutTheByteIsTruncated()
    {
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(1, false, Op, "x", (ControlReasonCode)99)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.OperatorText, 0, 1, 0, Op, ControlNoticeOutcome.None, "x", (ControlReasonCode)99)));

        byte[] reply = ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(1, false, Op, "x", ControlReasonCode.Unspecified))[1..];
        reply[^1] = 99;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply(reply));
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply(reply[..^1])); // the old layout (no code byte) is not accepted
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply([.. ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(1, false, Op, "x"))[1..], 0]));

        byte[] notice = ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.OperatorText, 0, 1, 0, Op, ControlNoticeOutcome.None, "x"))[1..];
        notice[^1] = 99;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeNotice(notice));
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeNotice(notice[..^1]));
    }

    [Theory]
    [InlineData(null, ControlReasonCode.NoEligibleWorker)]
    [InlineData("no-eligible-worker", ControlReasonCode.NoEligibleWorker)]
    [InlineData("map-unassigned", ControlReasonCode.NoEligibleWorker)]
    [InlineData("instance-unbound", ControlReasonCode.NoEligibleWorker)]
    [InlineData("worker-at-player-capacity", ControlReasonCode.TargetAtCapacity)]
    [InlineData("worker-at-instance-capacity", ControlReasonCode.TargetAtCapacity)]
    [InlineData("worker-not-ready", ControlReasonCode.TargetNotReady)]
    [InlineData("worker-draining", ControlReasonCode.TargetNotReady)]
    [InlineData("worker-unknown", ControlReasonCode.TargetNotReady)]
    [InlineData("something-new", ControlReasonCode.Unspecified)]
    public void APlacementFailure_MapsToItsWireCode_WithoutTheWorkerReadingText(string? failure, ControlReasonCode expected)
        => Assert.Equal(expected, GatewayTransferService.CodeOfPlacementFailure(failure));

    [Theory]
    [InlineData("worker-at-player-capacity", ControlReasonCode.TargetAtCapacity)]
    [InlineData("worker-not-ready", ControlReasonCode.TargetNotReady)]
    [InlineData("target-incarnation-changed", ControlReasonCode.TransferAborted)]
    [InlineData(null, ControlReasonCode.TransferAborted)]
    public void AnAbortedTransfer_KeepsASpecificCode_OrIsTransferAborted(string? failureCode, ControlReasonCode expected)
        => Assert.Equal(expected, GatewayTransferService.OutcomeCode(failureCode));

    // ------------------------------------------------------------------ (1) the stay-aboard intent

    [Theory]
    [InlineData(ControlTransferOptions.None, false)]
    [InlineData(ControlTransferOptions.NotLeaveTransport, true)]
    public void ASubmitsNotLeaveTransportOption_IsCarriedIntoTheTransferRequest(ControlTransferOptions options, bool expected)
    {
        Guid transfer = Guid.NewGuid();
        var token = new CharacterOwnershipToken("character:42", 3, Inc);
        var body = new ControlTransferSubmit(transfer, 42, 7, 99, 0, 0, 1, 0, new TransferPosition(1f, 2f, 3f, 0.5f), options, token);

        PlayerTransferRequest request = GatewayTransferService.RequestOf("ek", body);

        Assert.Equal(expected, request.NotLeaveTransport);
        Assert.Equal(transfer, request.TransferId);
        Assert.Equal(new WorkerNodeId("ek"), request.SourceWorkerId);
        Assert.Equal(token, request.SourceOwnershipToken);
        Assert.Equal(expected, (request with { ExplicitTargetWorkerId = new WorkerNodeId("target") }).NotLeaveTransport); // the target pin the service adds keeps the intent
    }

    // ------------------------------------------------------------------ (4) C13

    [Fact]
    public async Task ARetarget_WakesTheRetryBackoffOfALinkWithoutAConnection()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        GatewayWorkerEndpoint endpoint = a.Services.GetRequiredService<GatewayWorkerEndpoint>();
        await endpoint.StartAsync(cts.Token);
        try
        {
            // The gateway points at a port nobody listens on and would wait an hour before its second attempt.
            GatewayServer gateway = NewGateway(a, ClosedPort(), TimeSpan.FromHours(1));
            await gateway.StartAsync(cts.Token);
            try
            {
                GatewayWorkerLink link = Assert.Single(gateway.Workers);
                while (link.Status != GatewayWorkerLinkStatus.Backoff)
                {
                    await Task.Delay(20, cts.Token);
                }

                Assert.Null(link.Link);
                link.Retarget(endpoint.Host!.BoundEndPoint);

                // Without the wake-up this waits out the hour and the wait below times out.
                await link.WaitForLinkAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(60), cts.Token);
                Assert.NotNull(link.Link);
                Assert.Equal(endpoint.Host.BoundEndPoint, link.ConnectedEndPoint);
            }
            finally
            {
                await gateway.StopAsync(CancellationToken.None);
                gateway.Dispose();
            }
        }
        finally
        {
            await endpoint.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFlushedHoldWhoseLinkDied_IsKicked_WhenTheHandoffAborts_AfterTheRealServicePathFlushedIt()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: true, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Player, cts.Token);
        GatewayClientConnection connection = Assert.Single(AllConnections(rig.Gateway));
        FieldInfo flushedField = typeof(GatewayClientConnection).GetField("_handoffFlushed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool flushedByTheService = false;

        // The mark is set by GatewayHandoffService's own Flush step (never by hand): the stage hook runs after it. Then the source's process "dies" and the handoff aborts.
        await Assert.ThrowsAsync<WorldHandoffAbortedException>(() => rig.Service!.HandoffAsync(NodeA, async (stage, ct) =>
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                flushedByTheService = (bool)flushedField.GetValue(connection)!;
                await rig.AEndpoint.StopAsync(CancellationToken.None);
                while (rig.Gateway.Workers[0].Link is not null)
                {
                    await Task.Delay(20, ct);
                }

                throw new InvalidOperationException("injected failure after the source died at " + stage);
            }
        }, cts.Token));

        Assert.True(flushedByTheService, "the handoff service did not mark the hold flushed");
        // The abort released the hold of a connection whose link was gone: nothing to forward to, so it is disconnected with a reason.
        Assert.Equal("the worker link is gone", connection.KickReason);
    }

    // ------------------------------------------------------------------ (5) C14

    [Fact]
    public async Task ClusterRestart_WhenTheGatewayHostsNoRestart_AnswersNotHosted_ThroughTheCommandAndTheRelay()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: false, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);

        // The relay alone: the gateway refuses at once and says why, in text and in the code.
        ClusterRestartSubmission refused = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.False(refused.Accepted);
        Assert.Equal(ControlReasonCode.NotHosted, refused.Code);
        Assert.Contains("Gateway:Handoff:Enabled", refused.Reason);

        // '.cluster restart' typed by the operator: the command goes through the registered relay and tells the operator what the gateway answered.
        var context = new CommandContext(op.Session, op.Session.Player!, ChatCommands.CreateTable());
        Assert.True(await rig.A.World.InvokeAsync(() => ClusterCommands.Restart(context, NodeA)));
        WorldFrame reply = await op.ReadSystemTextAsync("Cluster restart was not started", cts.Token);
        Assert.Contains("hosts no operator restart", Encoding.UTF8.GetString(reply.Payload));
        await context.WhenSettled.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task TheRelay_TimesOutWithAClearMessage_WhenNoGatewayServiceHandlesTheSubmit()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: false, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);
        rig.Gateway.RestartSubmitsHandled = true; // pretend a restart service listens: the server stays silent and nobody answers

        var relay = new ClusterRestartRelay(TimeSpan.FromMilliseconds(500));
        relay.Attach(rig.AEndpoint.Host!, rig.AEndpoint.Acceptor!);
        ClusterRestartSubmission timedOut = await relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);

        Assert.False(timedOut.Accepted);
        Assert.Equal(Guid.Empty, timedOut.OperationId);
        Assert.Contains("did not answer within 0.5 s", timedOut.Reason);
        Assert.Contains("Gateway:Handoff:Enabled", timedOut.Reason);
    }

    [Fact]
    public async Task AStoppedRestartService_RefusesAtOnce_AndAnsweringResumesWhenItStartsAgain()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: true, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);
        Assert.True(rig.Gateway.RestartSubmitsHandled);

        await rig.Restart!.StopAsync(cts.Token);
        Assert.False(rig.Gateway.RestartSubmitsHandled);
        ClusterRestartSubmission whileStopped = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.False(whileStopped.Accepted);
        Assert.Contains(whileStopped.Code, new[] { ControlReasonCode.NotHosted, ControlReasonCode.GatewayStopping });
        await rig.Restart.StopAsync(cts.Token); // a second stop is a no-op

        // StopAsync reset the started flag: a new StartAsync takes the service up again (it used to stay "started" and never re-subscribe).
        await rig.Restart.StartAsync(cts.Token);
        Assert.True(rig.Gateway.RestartSubmitsHandled);
        ClusterRestartSubmission again = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, "no-such-worker", cts.Token);
        Assert.False(again.Accepted);
        Assert.Contains("Unknown worker", again.Reason); // answered by the service itself
    }

    [Fact]
    public async Task AGatewayThatStopsAfterAcceptingARestart_TellsTheOperatorTheOutcomeIsUnknown()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: true, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Administrator, cts.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdictSeen = new TaskCompletionSource<ControlNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.AEndpoint.Host!.ControlNoticeReceived += notice => verdictSeen.TrySetResult(notice);

        // The handoff service's first await with the run's token is the transfer guard, before any stage and before the coordinator (which would turn a cancellation into its own abort verdict).
        // Parking there is the point where the cancellation of a stopping gateway reaches the restart run as an OperationCanceledException, with the operation id already told to the worker.
        rig.Service!.Guard = new BlockingGuard(entered);

        ClusterRestartSubmission started = await op.Relay.SubmitRestartAsync(op.Session, AccountSecurity.Administrator, NodeA, cts.Token);
        Assert.True(started.Accepted, started.Reason);
        await entered.Task.WaitAsync(cts.Token);

        await rig.Restart!.StopAsync(cts.Token);

        ControlNotice verdict = await verdictSeen.Task.WaitAsync(cts.Token);
        Assert.Equal(ControlReasonCode.GatewayStopping, verdict.Code);
        Assert.Equal(ControlNoticeOutcome.Failed, verdict.Outcome);
        Assert.Equal(started.OperationId, verdict.OperationId);
        Assert.Contains("The gateway stopped while the restart ran", verdict.Text);
    }

    // ------------------------------------------------------------------ (3) the standby link

    [Fact]
    public async Task ASubmitArrivingOnTheStandbyLink_IsAnsweredStandbyLink()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: true, cts.Token);
        var answered = new TaskCompletionSource<ControlSubmitReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BEndpoint!.Host!.ControlSubmitReplyReceived += reply => answered.TrySetResult(reply);

        // The standby link carries no sessions, so the worker side cannot send a submit on it through TrySendControlSubmit; the receive path of the gateway's client is driven directly
        // (its private OnControlSubmit is what the link's receive loop calls), and the answer travels the real link back to B's host.
        GatewayStandbyLink standby = rig.Service!.StandbyLink(NodeA)!;
        var client = (GatewaySessionLinkClient)typeof(GatewayStandbyLink).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(standby)!;
        typeof(GatewaySessionLinkClient).GetMethod("OnControlSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(client, [new ControlSubmit(77, new ControlRestartSubmit(1, 1, 0, NodeA))]);

        ControlSubmitReply reply = await answered.Task.WaitAsync(cts.Token);
        Assert.Equal(77UL, reply.RequestId);
        Assert.False(reply.Accepted);
        Assert.Equal(ControlReasonCode.StandbyLink, reply.Code);
        Assert.Contains("standby", reply.Reason);
    }

    // ------------------------------------------------------------------ (4) the kept link of a superseded retarget

    [Fact]
    public async Task ASecondRetargetBeforeTheFirstMigration_KeepsTheOlderLinkThatCarriesTheSessions()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Rig rig = await Rig.StartAsync(withHandoff: true, cts.Token);
        await using Operator op = await rig.ConnectOperatorAsync(AccountSecurity.Player, cts.Token);
        GatewayClientConnection connection = Assert.Single(AllConnections(rig.Gateway));
        GatewayWorkerLink link = rig.Gateway.Workers[0];
        GatewaySessionLinkClient original = link.Link!;
        IPEndPoint aEndpoint = rig.AEndpoint.Host!.BoundEndPoint;
        IPEndPoint bEndpoint = rig.BEndpoint!.Host!.BoundEndPoint;
        Assert.True(connection.IsForwardingOn(original));
        Assert.True(original.SessionCount > 0);

        int firstMigrations = 0;
        var migrated = new TaskCompletionSource<MigrationView>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task FirstMigrate(GatewaySessionLinkClient? previous, GatewaySessionLinkClient next, CancellationToken token)
        {
            Interlocked.Increment(ref firstMigrations);
            return Task.CompletedTask;
        }

        Task SecondMigrate(GatewaySessionLinkClient? previous, GatewaySessionLinkClient next, CancellationToken token)
        {
            migrated.TrySetResult(new MigrationView(
                previous, previous?.IsConnected ?? false, previous?.SessionCount ?? 0, previous is not null && connection.IsForwardingOn(previous), connection.KickReason));
            return Task.CompletedTask;
        }

        // The retarget to B is superseded while B's link is being registered: WorkerLinkUp runs on the link's loop right after the registration and before the loop checks its target, so the
        // second retarget (back to A) lands exactly between "B connected" and "first migration".
        int registrations = 0;
        rig.Gateway.WorkerLinkUp += node =>
        {
            if (node == NodeA && Interlocked.Increment(ref registrations) == 1)
            {
                link.Retarget(aEndpoint, SecondMigrate);
            }
        };

        link.Retarget(bEndpoint, FirstMigrate);

        MigrationView view = await migrated.Task.WaitAsync(cts.Token);
        Assert.Equal(0, firstMigrations); // superseded: the second callback replaced it
        // The link that carries the operator's session is the one handed to the migration; the newer, empty link to B was the one closed.
        Assert.Same(original, view.Previous);
        Assert.True(view.PreviousConnected);
        Assert.True(view.PreviousSessions > 0);
        Assert.True(view.StillForwarding);
        Assert.Null(view.KickReason);
        Assert.Null(connection.KickReason);
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<GatewayClientConnection> AllConnections(GatewayServer gateway)
    {
        var connections = (ConcurrentDictionary<GatewayClientConnection, Task>)typeof(GatewayServer)
            .GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gateway)!;
        return connections.Keys;
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A single-worker gateway pointing at <paramref name="port"/> on loopback; the Warden posture is the worker's, as <see cref="StandbyWorkerTests.LinkAsync"/> builds it.</summary>
    private static GatewayServer NewGateway(SyntheticArcaneServer server, int port, TimeSpan retryDelay)
    {
        IServiceProvider services = server.Services;
        int realmId = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
        var warden = new GatewayWarden(
            realmId,
            services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
        return new GatewayServer(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions
            {
                BindAddress = IPAddress.Loopback.ToString(),
                Port = 0,
                WorkerAddress = IPAddress.Loopback.ToString(),
                WorkerPort = port,
                LinkSecret = StandbyWorkerTests.Secret,
                LinkRetryDelay = retryDelay,
            }),
            services.GetRequiredService<ILoggerFactory>(),
            warden: warden);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }

    /// <summary>A real operator client connected through the gateway to A, with A's world session and the worker's relay.</summary>
    private sealed class Operator(WorldClient client, WorldSession session, IClusterRestartRelay relay) : IAsyncDisposable
    {
        public WorldSession Session { get; } = session;

        public IClusterRestartRelay Relay { get; } = relay;

        public async Task<WorldFrame> ReadSystemTextAsync(string fragment, CancellationToken token)
        {
            while (true)
            {
                WorldFrame frame = await client.ReadAsync(token);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgMessagechat && Encoding.UTF8.GetString(frame.Payload).Contains(fragment, StringComparison.Ordinal))
                {
                    return frame;
                }
            }
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }

    /// <summary>
    /// A synthetic world A (and, with the handoff, a standby B) behind a real gateway. <c>withHandoff: false</c> is a gateway with <c>Gateway:Handoff:Enabled</c> off: no handoff service, no restart
    /// service, while the worker still registers its restart relay (<c>Cluster:Worker:Handoff:Enabled</c>).
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly LocalWorldHandoffSource _source;
        private readonly SyntheticArcaneServer? _b;
        private readonly GatewayWorkerEndpoint? _bEndpoint;

        private Rig(
            SyntheticArcaneServer a, SyntheticArcaneServer? b, GatewayWorkerEndpoint aEndpoint, GatewayWorkerEndpoint? bEndpoint, GatewayServer gateway,
            GatewayHandoffService? service, GatewayRestartOrchestration? restart)
        {
            A = a;
            _b = b;
            AEndpoint = aEndpoint;
            _bEndpoint = bEndpoint;
            Gateway = gateway;
            Service = service;
            Restart = restart;
            _source = (LocalWorldHandoffSource)a.Services.GetRequiredService<IWorldHandoffSource>();
        }

        public SyntheticArcaneServer A { get; }

        public GatewayWorkerEndpoint? BEndpoint => _bEndpoint;

        public GatewayWorkerEndpoint AEndpoint { get; }

        public GatewayServer Gateway { get; }

        public GatewayHandoffService? Service { get; }

        public GatewayRestartOrchestration? Restart { get; }

        public static async Task<Rig> StartAsync(bool withHandoff, CancellationToken token)
        {
            SyntheticArcaneServer a = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { EnableFencing = true, Configuration = Worker(NodeA, standby: false) }, configureServices: null, token);
            SyntheticArcaneServer? b = null;
            GatewayWorkerEndpoint? aEndpoint = null;
            GatewayWorkerEndpoint? bEndpoint = null;
            GatewayServer? gateway = null;
            try
            {
                aEndpoint = a.Services.GetRequiredService<GatewayWorkerEndpoint>();
                await aEndpoint.StartAsync(token);
                if (withHandoff)
                {
                    b = await SyntheticArcaneServer.StartAsync(
                        new SyntheticServerOptions { EnableFencing = true, SharedDatabaseDirectory = a.DataDirectory, Configuration = Worker(NodeB, standby: true) }, configureServices: null, token);
                    bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
                    await bEndpoint.StartAsync(token);
                }

                IServiceProvider services = a.Services;
                int realmId = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
                var warden = new GatewayWarden(
                    realmId,
                    services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                    services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
                var entry = new GatewayWorkerEntry { NodeId = NodeA, Address = IPAddress.Loopback.ToString(), Port = aEndpoint.Host!.BoundEndPoint.Port };
                if (withHandoff)
                {
                    entry.StandbyNodeId = NodeB;
                    entry.StandbyAddress = IPAddress.Loopback.ToString();
                    entry.StandbyPort = bEndpoint!.Host!.BoundEndPoint.Port;
                }

                gateway = new GatewayServer(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = 0,
                        Workers = [entry],
                        LinkSecret = Secret,
                        LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                    }),
                    services.GetRequiredService<ILoggerFactory>(),
                    warden: warden,
                    handoff: withHandoff ? Options.Create(new GatewayHandoffOptions { Enabled = true }) : null,
                    control: withHandoff ? Options.Create(new GatewayControlOptions()) : null);
                await gateway.StartAsync(token);
                await gateway.WaitForLinkAsync(token);

                GatewayHandoffService? service = null;
                GatewayRestartOrchestration? restart = null;
                if (withHandoff)
                {
                    service = new GatewayHandoffService(gateway, services.GetRequiredService<IWorldHandoffStore>(), NullLoggerFactory.Instance);
                    await service.StartAsync(token);
                    restart = new GatewayRestartOrchestration(gateway, service, new NoRestartRequester(), NullLoggerFactory.Instance);
                    await restart.StartAsync(token);
                    await WaitUntilAsync(() => service.ActiveChannel(NodeA) is not null && service.StandbyChannel(NodeA) is not null, token);
                }

                return new Rig(a, b, aEndpoint, bEndpoint, gateway, service, restart);
            }
            catch
            {
                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (bEndpoint is not null)
                {
                    await bEndpoint.StopAsync(CancellationToken.None);
                }

                if (aEndpoint is not null)
                {
                    await aEndpoint.StopAsync(CancellationToken.None);
                }

                if (b is not null)
                {
                    await b.DisposeAsync();
                }

                await a.DisposeAsync();
                throw;
            }
        }

        /// <summary>Creates the operator account at <paramref name="security"/>, logs it in through the gateway and enters the world on A.</summary>
        public async Task<Operator> ConnectOperatorAsync(AccountSecurity security, CancellationToken token)
        {
            int accountId = await A.AddAccountAsync(OperatorAccount, "PASSWORD", token);
            await using (AsyncServiceScope scope = A.Services.CreateAsyncScope())
            {
                if (security != AccountSecurity.Player)
                {
                    Assert.True(await scope.ServiceProvider.GetRequiredService<IAccountStore>().UpdateSecurityAsync(OperatorAccount, security, token));
                }
            }

            LogonResult logon = await LogonClient.AuthenticateAsync(A.RealmEndpoint, OperatorAccount, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(OperatorAccount, logon.SessionKey, token));
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate("Hardenop"), token);
            Assert.Equal(new byte[] { 0x2E }, (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token)).Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            byte[] login = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(login, BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));
            await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
            await WaitUntilAsync(() => A.Services.GetRequiredService<SessionRegistry>().Find(accountId) is not null, token);

            WorldSession session = A.Services.GetRequiredService<SessionRegistry>().Find(accountId)!;
            return new Operator(client, session, A.Services.GetRequiredService<IClusterRestartRelay>());
        }

        public async ValueTask DisposeAsync()
        {
            _source.Dispose(); // release A's parked simulation thread before its host stops
            if (Restart is not null)
            {
                await Restart.StopAsync(CancellationToken.None);
            }

            if (Service is not null)
            {
                await Service.DisposeAsync();
            }

            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            if (_bEndpoint is not null)
            {
                await _bEndpoint.StopAsync(CancellationToken.None);
            }

            await AEndpoint.StopAsync(CancellationToken.None);
            Exception? stopFailure = null;
            if (_b is not null)
            {
                try
                {
                    await _b.DisposeAsync();
                }
                catch (Exception ex)
                {
                    stopFailure = ex;
                }
            }

            try
            {
                await A.DisposeAsync();
            }
            catch (Exception ex)
            {
                stopFailure ??= ex;
            }

            // A retired or killed A logs its player out as it stops: every write it still attempts is refused by the characters fence, nothing else fails.
            if (stopFailure is not null)
            {
                Assert.All(Leaves(stopFailure), leaf => Assert.IsType<CharacterOwnershipRejectedException>(leaf));
            }
        }

        private static Dictionary<string, string?> Worker(string nodeId, bool standby) => new()
        {
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:Standby"] = standby ? "true" : "false",
            ["Cluster:Worker:NodeId"] = nodeId,
            ["Cluster:Worker:Port"] = "0",
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Worker:Handoff:Enabled"] = "true",
        };

        private static IEnumerable<Exception> Leaves(Exception error)
        {
            IEnumerable<Exception> inner = error is AggregateException aggregate ? aggregate.InnerExceptions
                : error.InnerException is { } one ? [one] : [];
            bool any = false;
            foreach (Exception child in inner)
            {
                any = true;
                foreach (Exception leaf in Leaves(child))
                {
                    yield return leaf;
                }
            }

            if (!any)
            {
                yield return error;
            }
        }
    }

    /// <summary>The supervisor boundary: no process is ever relaunched in these tests.</summary>
    private sealed record MigrationView(GatewaySessionLinkClient? Previous, bool PreviousConnected, int PreviousSessions, bool StillForwarding, string? KickReason);

    /// <summary>A transfer guard that parks the handoff at its first step until the run's token is cancelled.</summary>
    private sealed class BlockingGuard(TaskCompletionSource entered) : IGatewayHandoffGuard
    {
        public async Task<IDisposable> EnterHandoffAsync(string slotNode, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("the blocking guard was released without a cancellation");
        }
    }

    private sealed class NoRestartRequester : IWorkerRestartRequester
    {
        public Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
