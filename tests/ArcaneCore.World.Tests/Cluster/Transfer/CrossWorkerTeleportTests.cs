using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Commit C10 of _wf/handoff-control-DESIGN.md (sections 4.3 and 4.5): the worker's <see cref="GatewayClusterFarTeleportRouter"/> and its feature. The router runs over a real world runtime and real players; the
/// <see cref="ControlSubmit"/> travels over a real <see cref="GatewaySessionLinkHost"/> and <see cref="GatewaySessionLinkClient"/> on loopback (link proof, MAC, admission and framing included), and the gateway's
/// answers come back as real frames. Fakes sit only at the process boundary: the gateway's coordinator (the client's submit handler) and the world's blocker probe. The end-to-end run over two worlds, the real gateway
/// and a mock client is <c>GatewayCrossWorkerTeleportTests</c> in the gateway test project (it needs the mock client).
/// </summary>
public sealed class CrossWorkerTeleportTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private const string Secret = "cross-worker-teleport-secret-0123456789";
    private const int AccountId = 5;
    private const long Generation = 42;
    private static readonly CharacterOwnershipToken Token = new("worker:ek", 3, Guid.NewGuid());

    private readonly WorldRuntime _world;
    private readonly RecordingSession _session = new(AccountId, Generation);
    private readonly Player _player;
    private readonly FakeProbe _probe = new();
    private readonly List<(long Generation, ControlSubmit Submit)> _submits = [];
    private bool _linkAvailable = true;

    public CrossWorkerTeleportTests()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = Token,
        };
        _player = NewPlayer(7, "Wanderer", _session);
        _world.AddPlayer(_player, _world.GetMap(0));
        _world.UseManualClock();
        _world.Start();
    }

    public void Dispose() => _world.Stop();

    private static Player NewPlayer(int id, string name, IPlayerSession session)
    {
        var character = new CharacterRecord { Id = id, AccountId = session.AccountId, Name = name, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        player.Relocate(12f, 34f, 56f, 1.5f, 0);
        return player;
    }

    private GatewayClusterFarTeleportRouter NewRouter(TimeProvider? time = null, long? generation = Generation) => new(
        _world, _probe, _ => generation,
        (gen, submit) =>
        {
            _submits.Add((gen, submit));
            return _linkAvailable;
        },
        timeProvider: time);

    /// <summary>Runs the queued world-thread work (the router posts its player messages there) before the caller looks at what the session received.</summary>
    private Task FlushWorldAsync() => _world.InvokeAsync(() => true);

    private static readonly TeleportDestination Kalimdor = new(1, -1196.22f, 29.0f, 176.949f, 3.1f);

    // ------------------------------------------------------------------ the request

    [Fact]
    public void TryBeginTransfer_QueuesOneSubmit_WithTheCharacterTheSessionTheMapsAndTheSourceToken()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();

        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.NotLeaveTransport));

        (long gen, ControlSubmit submit) = Assert.Single(_submits);
        Assert.Equal(Generation, gen);
        var body = Assert.IsType<ControlTransferSubmit>(submit.Body);
        Assert.Equal(_player.Guid.Value, body.CharacterGuid);
        Assert.Equal(AccountId, body.AccountId);
        Assert.Equal(Generation, body.SessionGeneration);
        Assert.Equal((0u, 0u), (body.SourceMapId, body.SourceInstanceId));
        Assert.Equal((1u, 0u), (body.TargetMapId, body.TargetInstanceId)); // the target worker allocates an instance id for an instanced map (Q8)
        Assert.Equal(new TransferPosition(Kalimdor.X, Kalimdor.Y, Kalimdor.Z, Kalimdor.Orientation), body.TargetPosition);
        Assert.Equal(ControlTransferOptions.NotLeaveTransport, body.Options);
        Assert.Equal(Token, body.SourceToken);
        Assert.NotEqual(Guid.Empty, body.TransferId);
        Assert.Equal(1, router.InFlightCount);
    }

    [Fact]
    public void TryBeginTransfer_WithoutTheOption_SendsNone_AndTheSecondRequestOfTheSameCharacterIsRefusedWhileTheFirstIsInFlight()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();

        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        Assert.Equal(ControlTransferOptions.None, Assert.IsType<ControlTransferSubmit>(Assert.Single(_submits).Submit.Body).Options);
        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        Assert.Single(_submits);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("infinity")]
    public void TryBeginTransfer_RefusesANonFiniteDestination_AndSendsNothing(string kind)
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        float bad = kind == "nan" ? float.NaN : float.PositiveInfinity;

        Assert.False(router.TryBeginTransfer(_player, Kalimdor with { Z = bad }, TeleportOptions.None));

        Assert.Empty(_submits);
        Assert.Equal(0, router.InFlightCount);
    }

    [Fact]
    public void TryBeginTransfer_RefusesAPlayerWithoutAGatewaySession()
    {
        GatewayClusterFarTeleportRouter router = NewRouter(generation: null);

        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        Assert.Empty(_submits);
    }

    [Fact]
    public void TryBeginTransfer_RefusesAPlayerWithAnActiveBlocker_AndAWorldWithoutAnOwnershipToken()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        _probe.Blocked = true;
        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        _probe.Blocked = false;
        Assert.Empty(_submits);

        _world.CharacterOwnership = null;
        _player.CharacterOwnership = null; // AddPlayer gave the player the world's token
        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        Assert.Empty(_submits);
        Assert.Equal(0, router.InFlightCount);
    }

    [Fact]
    public void TryBeginTransfer_WhenTheLinkCannotTakeTheFrame_RefusesAndForgetsTheRequest()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        _linkAvailable = false;

        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        Assert.Equal(0, router.InFlightCount);

        _linkAvailable = true;
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None)); // the character can try again at once
    }

    [Fact]
    public void AnInFlightTransferThatNeverGetsANotice_ExpiresSoTheCharacterCanTryAgain()
    {
        var time = new FakeTimeProvider();
        GatewayClusterFarTeleportRouter router = NewRouter(time);
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        time.Advance(GatewayClusterFarTeleportRouter.InFlightExpiry + TimeSpan.FromSeconds(1));

        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        Assert.Equal(2, _submits.Count);
        Assert.Equal(1, router.InFlightCount);
    }

    // ------------------------------------------------------------------ the answers

    [Fact]
    public async Task ARefusedSubmit_ClearsTheEntry_AndTellsThePlayer_WithTheMaxPlayersPacketOnlyForACapacityLimit()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        ControlSubmit first = _submits[0].Submit;

        router.OnSubmitReply(new ControlSubmitReply(first.RequestId, false, Guid.Empty, "target map (1, instance 0) cannot be routed: no-eligible-worker"));
        await FlushWorldAsync();

        Assert.Equal(0, router.InFlightCount);
        Assert.DoesNotContain(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
        Assert.Contains(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);

        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        _session.Clear();
        router.OnSubmitReply(new ControlSubmitReply(_submits[1].Submit.RequestId, false, Guid.Empty, "target map (1, instance 0) cannot be routed: worker-at-player-capacity"));
        await FlushWorldAsync();

        (WorldOpcode _, byte[] payload) = Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
        Assert.Equal(new[] { (byte)TransferAbortReason.MaxPlayers }, payload);
        Assert.Contains(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task AnAcceptedSubmit_KeepsTheEntry_UntilTheNoticeEndsIt()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        (_, ControlSubmit submit) = _submits[0];
        var body = (ControlTransferSubmit)submit.Body;

        router.OnSubmitReply(new ControlSubmitReply(submit.RequestId, true, body.TransferId, string.Empty));
        Assert.Equal(1, router.InFlightCount);
        Assert.False(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        // A notice of another transfer of the character is not this one's.
        router.OnNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, Generation, AccountId, body.CharacterGuid, Guid.NewGuid(), ControlNoticeOutcome.Completed, string.Empty));
        Assert.Equal(1, router.InFlightCount);

        router.OnNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, Generation, AccountId, body.CharacterGuid, body.TransferId, ControlNoticeOutcome.Completed, string.Empty));
        await FlushWorldAsync();

        Assert.Equal(0, router.InFlightCount);
        Assert.Empty(_session.Sent); // a completed transfer tells the source player nothing: the target worker takes the client over
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
    }

    [Fact]
    public async Task AnAbortedNotice_TellsThePlayerWhyTheTeleportDidNotHappen_AndAnOperatorTextNoticeIsIgnored()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        var body = (ControlTransferSubmit)_submits[0].Submit.Body;

        router.OnNotice(new ControlNotice(ControlNoticeKind.OperatorText, 0, AccountId, 0, Guid.Empty, ControlNoticeOutcome.None, "restart started"));
        Assert.Equal(1, router.InFlightCount);

        router.OnNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, Generation, AccountId, body.CharacterGuid, body.TransferId, ControlNoticeOutcome.Aborted, "target-incarnation-changed"));
        await FlushWorldAsync();

        Assert.Equal(0, router.InFlightCount);
        Assert.Empty(_session.Sent.Where(sent => sent.Opcode == WorldOpcode.SmsgTransferAborted));
        Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public void AReplyOrNoticeForNobody_IsIgnored()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();

        router.OnSubmitReply(new ControlSubmitReply(999, false, Guid.Empty, "stale"));
        router.OnNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, 1, 1, 123, Guid.NewGuid(), ControlNoticeOutcome.Aborted, "stale"));

        Assert.Equal(0, router.InFlightCount);
    }

    // ------------------------------------------------------------------ over a real link

    [Fact]
    public async Task OverARealLink_TheGatewayAcceptsTheRequest_AndItsOutcomeNoticeEndsTheEntry_AndAFullTargetShowsAsTheMaxPlayersPacket()
    {
        await using LinkRig rig = await LinkRig.StartAsync(Generation, AccountId);
        var linked = new GatewayClusterFarTeleportRouter(_world, _probe, _ => Generation, (gen, submit) => rig.Host.TrySendControlSubmit(gen, submit));
        rig.Host.SubmitSink = linked;
        rig.Client.ControlSubmitReceived = submit =>
        {
            rig.Received.Enqueue(submit);
            var body = (ControlTransferSubmit)submit.Body;
            rig.Client.SendControlSubmitReply(new ControlSubmitReply(submit.RequestId, true, body.TransferId, string.Empty));
            rig.Client.SendControlNotice(new ControlNotice(
                ControlNoticeKind.TransferOutcome, body.SessionGeneration, body.AccountId, body.CharacterGuid, body.TransferId, ControlNoticeOutcome.Aborted,
                "target map (1, instance 0) cannot be routed: worker-at-player-capacity"));
        };

        Assert.True(linked.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        ControlSubmit received = await rig.WaitForSubmitAsync();
        var sent = Assert.IsType<ControlTransferSubmit>(received.Body);
        Assert.Equal(_player.Guid.Value, sent.CharacterGuid);
        Assert.Equal(Token, sent.SourceToken);
        Assert.Equal(Generation, sent.SessionGeneration);

        await WaitUntilAsync(() => linked.InFlightCount == 0);
        await FlushWorldAsync();
        Assert.Contains(_session.Sent, message => message.Opcode == WorldOpcode.SmsgTransferAborted && message.Payload.SequenceEqual(new[] { (byte)TransferAbortReason.MaxPlayers }));
        Assert.Contains(_session.Sent, message => message.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task OverARealLink_AGatewayWithNoHandler_RefusesAtOnce_AndThePlayerIsTold()
    {
        await using LinkRig rig = await LinkRig.StartAsync(Generation, AccountId);
        var linked = new GatewayClusterFarTeleportRouter(_world, _probe, _ => Generation, (gen, submit) => rig.Host.TrySendControlSubmit(gen, submit));
        rig.Host.SubmitSink = linked;

        Assert.True(linked.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        await WaitUntilAsync(() => linked.InFlightCount == 0);
        await FlushWorldAsync();

        Assert.Contains(_session.Sent, message => message.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task OverARealLink_ARequestForASessionTheLinkDoesNotCarry_IsNotSent()
    {
        await using LinkRig rig = await LinkRig.StartAsync(Generation, AccountId);
        var linked = new GatewayClusterFarTeleportRouter(_world, _probe, _ => Generation + 1, (gen, submit) => rig.Host.TrySendControlSubmit(gen, submit));

        Assert.False(linked.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        Assert.Equal(0, linked.InFlightCount);
    }

    // ------------------------------------------------------------------ the feature

    [Theory]
    [InlineData(true, false, true, "0", true)]
    [InlineData(false, false, true, "0", false)] // not worker mode: Combined and single-process worlds route nothing
    [InlineData(true, true, true, "0", false)] // a standby attaches no feature and routes nothing
    [InlineData(true, false, false, "0", false)] // Cluster:Worker:Transfers:Enabled off
    [InlineData(true, false, true, "", false)] // no OwnedMaps list: the worker owns every map, nothing is remote
    [InlineData(true, false, true, "  ", false)]
    public void TheFeatureRoutesOnlyInWorkerModeWithTransfersOnAndAnOwnedMapList(bool workerMode, bool standby, bool transfers, string ownedMaps, bool expected)
    {
        var worker = new GatewayWorkerOptions { Enabled = workerMode, Standby = standby, OwnedMaps = ownedMaps };
        var control = new WorkerTransferControlOptions { Enabled = transfers };

        Assert.Equal(expected, ClusterFarTeleportFeature.ShouldRoute(worker, control));
    }

    [Fact]
    public void WithoutTheWorkerOptions_TheFeatureBuildsNoRouter_SoTheTeleportRefusalOfAnUnownedMapStaysAsItWas()
    {
        var feature = new ClusterFarTeleportFeature(new EmptyServices());

        feature.Attach(_world);

        Assert.Null(feature.Router);
    }

    // ------------------------------------------------------------------ rig

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Budget);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeProbe : ITransferPlayerProbe
    {
        public bool Blocked { get; set; }

        public TransferActiveBlockers Inspect(Player player) => new(Blocked, false, false, false, false, false);

        public void CloseInterruptible(Player player)
        {
        }
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class RecordingSession(int accountId, long generation) : IPlayerSession
    {
        private readonly ConcurrentQueue<(WorldOpcode Opcode, byte[] Payload)> _sent = new();

        public int AccountId { get; } = accountId;

        public long SessionGeneration { get; } = generation;

        public AccountSecurity Security => AccountSecurity.Player;

        public IReadOnlyCollection<(WorldOpcode Opcode, byte[] Payload)> Sent => [.. _sent];

        public void Clear() => _sent.Clear();

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => _sent.Enqueue((opcode, payload.ToArray()));

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    /// <summary>A real link host and client on loopback with one open session of <c>generation</c>, as the gateway would hold it.</summary>
    private sealed class LinkRig : IAsyncDisposable
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private LinkRig(GatewaySessionLinkHost host, GatewaySessionLinkClient client)
        {
            Host = host;
            Client = client;
        }

        public GatewaySessionLinkHost Host { get; }

        public GatewaySessionLinkClient Client { get; }

        public ConcurrentQueue<ControlSubmit> Received { get; } = new();

        public static async Task<LinkRig> StartAsync(long generation, int accountId)
        {
            using var cts = new CancellationTokenSource(Budget);
            var host = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, "worker-ek", Guid.NewGuid(), 1, "dev", 1, 1),
                new OpenAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0));
            host.Start();
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway-a", Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret);
            var rig = new LinkRig(host, client);
            try
            {
                await client.ConnectAsync(cts.Token);
                while (host.AdmittedLinkCount != 1)
                {
                    await Task.Delay(10, cts.Token);
                }

                GatewaySessionOpenReply reply = await client.OpenAsync(
                    new GatewaySessionOpenRequest(generation, accountId, "TELEPORTER", 5875, "127.0.0.1"), new QuietSink(), cts.Token);
                Assert.True(reply.Accepted);
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }

            return rig;
        }

        public async Task<ControlSubmit> WaitForSubmitAsync()
        {
            using var cts = new CancellationTokenSource(Budget);
            while (!Received.TryPeek(out _))
            {
                await Task.Delay(10, cts.Token);
            }

            Received.TryDequeue(out ControlSubmit? submit);
            return submit!;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private sealed class OpenAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Accepted(new QuietRemoteSession()));
    }

    private sealed class QuietRemoteSession : IGatewayRemoteSession
    {
        public void Start()
        {
        }

        public void Deliver(WorldOpcode opcode, byte[] payload)
        {
        }

        public void CloseFromGateway(ClusterSessionDetachReason reason)
        {
        }
    }

    private sealed class QuietSink : IGatewaySessionSink
    {
        public void OnOpened(GatewaySessionOpenReply reply)
        {
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void OnClosed(ClusterSessionDetachReason reason)
        {
        }
    }
}
