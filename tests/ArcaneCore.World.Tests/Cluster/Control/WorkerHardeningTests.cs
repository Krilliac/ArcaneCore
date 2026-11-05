using System.Collections.Concurrent;
using System.Data.Common;
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
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Control;

/// <summary>
/// Review follow-ups to the worker side of the control channel (handoff-control design, commits C10 to C12), no database and no process boundary beyond fakes:
/// (1) the player is never shown the gateway's refusal text, (2) the MaxPlayers abort follows the structured <see cref="ControlReasonCode"/> and never a substring of the text,
/// (3) a promoted standby installs the far-teleport sink and routes, (4) the self-heal checks the journal row names this worker, rejects sub-millisecond intervals, disposes
/// synchronously and keeps an operation while a start verb of it still runs, (5) the reclaim guard treats a missing <c>cluster_world_handoff</c> table as no committed handoff.
/// </summary>
public sealed class WorkerHardeningTests : IDisposable
{
    private const int AccountId = 5;
    private const long Generation = 42;
    private static readonly CharacterOwnershipToken Token = new("worker:ek", 3, Guid.NewGuid());
    private static readonly TeleportDestination Kalimdor = new(1, -1196.22f, 29.0f, 176.949f, 3.1f);
    private static readonly WorkerNodeId NodeA = new("world-a");
    private static readonly WorkerNodeId NodeB = new("world-b");
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, Guid.NewGuid());

    private readonly WorldRuntime _world;
    private readonly RecordingSession _session = new(AccountId, Generation);
    private readonly Player _player;
    private readonly List<(long Generation, ControlSubmit Submit)> _submits = [];

    public WorkerHardeningTests()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = Token,
        };
        var character = new CharacterRecord { Id = 7, AccountId = AccountId, Name = "Wanderer", Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
        _player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), _session);
        _player.Relocate(12f, 34f, 56f, 1.5f, 0);
        _world.AddPlayer(_player, _world.GetMap(0));
        _world.UseManualClock();
        _world.Start();
    }

    public void Dispose() => _world.Stop();

    private GatewayClusterFarTeleportRouter NewRouter() => new(
        _world, new FakeProbe(), _ => Generation,
        (gen, submit) =>
        {
            _submits.Add((gen, submit));
            return true;
        });

    private Task FlushWorldAsync() => _world.InvokeAsync(() => true);

    // ------------------------------------------------------------------ (1) the player never sees the gateway's text

    [Fact]
    public async Task ARefusedSubmit_ShowsThePlayerOnlyTheGenericLine_NeverTheGatewayText()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        const string internalText = "worker 'world-b' (account 5, session generation 42) failed: System.InvalidOperationException: secret detail";

        router.OnSubmitReply(new ControlSubmitReply(_submits[0].Submit.RequestId, false, Guid.Empty, internalText, ControlReasonCode.NoEligibleWorker));
        await FlushWorldAsync();

        byte[] expected = ChatPackets.BuildSystemMessage(GatewayClusterFarTeleportRouter.PlayerFailureText);
        (WorldOpcode _, byte[] payload) = Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.Equal(expected, payload);
        Assert.DoesNotContain(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
        foreach ((WorldOpcode _, byte[] bytes) in _session.Sent)
        {
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("world-b", text);
            Assert.DoesNotContain("secret detail", text);
            Assert.DoesNotContain("generation", text);
        }
    }

    [Fact]
    public async Task AnAbortedNotice_ShowsThePlayerOnlyTheGenericLine_NeverTheGatewayText()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        var body = (ControlTransferSubmit)_submits[0].Submit.Body;

        router.OnNotice(new ControlNotice(
            ControlNoticeKind.TransferOutcome, Generation, AccountId, body.CharacterGuid, body.TransferId, ControlNoticeOutcome.Failed, "node world-b incarnation changed (account 5)", ControlReasonCode.TransferAborted));
        await FlushWorldAsync();

        (WorldOpcode _, byte[] payload) = Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.Equal(ChatPackets.BuildSystemMessage(GatewayClusterFarTeleportRouter.PlayerFailureText), payload);
    }

    // ------------------------------------------------------------------ (2) the abort follows the code, not the text

    [Fact]
    public async Task TheMaxPlayersAbort_IsSentForTheCapacityCode_EvenWhenTheTextSaysNothingOfCapacity()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        router.OnSubmitReply(new ControlSubmitReply(_submits[0].Submit.RequestId, false, Guid.Empty, "no room", ControlReasonCode.Capacity));
        await FlushWorldAsync();

        (WorldOpcode _, byte[] payload) = Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
        Assert.Equal(new[] { (byte)TransferAbortReason.MaxPlayers }, payload);
    }

    [Fact]
    public async Task TheWordCapacityInTheText_WithoutTheCode_SendsNoMaxPlayersAbort()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));

        router.OnSubmitReply(new ControlSubmitReply(_submits[0].Submit.RequestId, false, Guid.Empty, "worker-at-player-capacity", ControlReasonCode.None));
        await FlushWorldAsync();

        Assert.DoesNotContain(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
        Assert.Contains(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task ACapacityNotice_SendsTheMaxPlayersAbort()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        Assert.True(router.TryBeginTransfer(_player, Kalimdor, TeleportOptions.None));
        var body = (ControlTransferSubmit)_submits[0].Submit.Body;

        router.OnNotice(new ControlNotice(
            ControlNoticeKind.TransferOutcome, Generation, AccountId, body.CharacterGuid, body.TransferId, ControlNoticeOutcome.Refused, "full", ControlReasonCode.Capacity));
        await FlushWorldAsync();

        Assert.Single(_session.Sent, sent => sent.Opcode == WorldOpcode.SmsgTransferAborted);
    }

    // ------------------------------------------------------------------ (3) a promoted standby routes and gets the sink

    [Theory]
    [InlineData(true, false)] // a standby that was not promoted routes nothing
    [InlineData(true, true)] // promoted: Cluster:Worker:Standby stays true in the options, the world is an active worker now
    [InlineData(false, false)] // an active worker
    public void ShouldRoute_TreatsAPromotedStandbyAsActive(bool standbyOption, bool promoted)
    {
        var worker = new GatewayWorkerOptions { Enabled = true, Standby = standbyOption, OwnedMaps = "0" };
        var transfers = new WorkerTransferControlOptions { Enabled = true };

        Assert.Equal(!standbyOption || promoted, ClusterFarTeleportFeature.ShouldRoute(worker, transfers, promoted));
        Assert.Equal(!standbyOption, ClusterFarTeleportFeature.ShouldRoute(worker, transfers));
    }

    [Fact]
    public void ShouldRoute_StillNeedsTransfersAndOwnedMaps_AfterAPromotion()
    {
        var worker = new GatewayWorkerOptions { Enabled = true, Standby = true, OwnedMaps = "0" };

        Assert.False(ClusterFarTeleportFeature.ShouldRoute(worker, new WorkerTransferControlOptions { Enabled = false }, promoted: true));
        Assert.False(ClusterFarTeleportFeature.ShouldRoute(new GatewayWorkerOptions { Enabled = true, Standby = true, OwnedMaps = "" }, new WorkerTransferControlOptions { Enabled = true }, promoted: true));
    }

    [Fact]
    public async Task InstallSubmitSink_HandsTheRouterToTheLinkHost_AndIgnoresAStoppedEndpoint()
    {
        GatewayClusterFarTeleportRouter router = NewRouter();
        await using var host = new GatewaySessionLinkHost(
            GatewayLinkManifest.Create(ClusterRole.Worker, "worker-ek", Guid.NewGuid(), 1, "dev", 1, 1),
            new OpenAcceptor(), () => 1, "hardening-secret-0123456789", new IPEndPoint(IPAddress.Loopback, 0));
        Assert.Null(host.SubmitSink);

        ClusterFarTeleportFeature.InstallSubmitSink(host, router);

        Assert.Same(router, host.SubmitSink);
        ClusterFarTeleportFeature.InstallSubmitSink(null, router);
        Assert.Throws<ArgumentNullException>(() => ClusterFarTeleportFeature.InstallSubmitSink(host, null!));
    }

    // ------------------------------------------------------------------ (4) the self-heal

    [Fact]
    public void SelfHeal_RejectsAnIntervalUnderOneMillisecond_AndAcceptsOne()
    {
        var inner = new StubHandler();

        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSelfHealService(inner, null, null, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSelfHealService(inner, null, null, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSelfHealService(inner, null, null, TimeSpan.FromSeconds(-1)));
        using var one = new WorkerSelfHealService(inner, null, null, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, one.HeldCount);
    }

    [Fact]
    public async Task SelfHeal_DisposesSynchronously_AndAgainAsynchronously_AndThenRefusesToTrack()
    {
        var store = new InMemoryTransferDecisionStore();
        var heal = new WorkerSelfHealService(new StubHandler(), null, Transfers(store, new FakeSource(NodeA)), TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store, NodeA);
        await heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None);
        Assert.Equal(1, heal.HeldCount);

        heal.Dispose();
        heal.Dispose();
        await heal.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None));
    }

    [Fact]
    public async Task SelfHeal_DoesNotHealARowThatNamesAnotherSourceWorker()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeSource(NodeA);
        using var heal = new WorkerSelfHealService(new StubHandler(), null, Transfers(store, source), TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store, NodeB); // the journal says another worker is the source
        await heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None);
        await store.AbortPreCommitAsync(row.TransferId, "aborted elsewhere");

        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));

        Assert.Equal(0, source.Resumes);
        Assert.Equal(0, source.Retires);
        Assert.Equal(0, heal.Healed);
    }

    [Fact]
    public async Task SelfHeal_HealsARowThatNamesThisWorker()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeSource(NodeA);
        using var heal = new WorkerSelfHealService(new StubHandler(), null, Transfers(store, source), TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store, NodeA);
        await heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None);
        await store.AbortPreCommitAsync(row.TransferId, "aborted elsewhere");

        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));

        Assert.Equal(1, source.Resumes);
        Assert.Equal(1, heal.Healed);
    }

    [Fact]
    public async Task SelfHeal_KeepsTheOperation_WhenAnEndVerbArrivesWhileTheStartVerbStillRuns()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeSource(NodeA);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new StubHandler { Hold = request => request.Verb == ControlVerb.Suspend ? gate.Task : Task.CompletedTask };
        using var heal = new WorkerSelfHealService(inner, null, Transfers(store, source), TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store, NodeA);

        Task<ControlReply> start = heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None);
        Assert.Equal(1, heal.HeldCount);
        ControlReply ended = await heal.HandleAsync(
            new ControlRequest(2, ControlKind.PlayerTransfer, row.TransferId, ControlVerb.ResumeSource, ControlNoArguments.Instance), new GatewayControlContext(1), CancellationToken.None);
        Assert.Equal(ControlStatus.Ok, ended.Status);

        // The suspend may still take the player after the end verb: the operation stays tracked.
        Assert.Equal(1, heal.HeldCount);
        gate.SetResult();
        Assert.Equal(ControlStatus.Ok, (await start).Status);
        Assert.Equal(1, await heal.RunPassAsync(CancellationToken.None)); // PreCommit row: nothing to do yet, still held

        await store.AbortPreCommitAsync(row.TransferId, "gateway aborted");
        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));
        Assert.Equal(1, source.Resumes);
    }

    [Fact]
    public async Task SelfHeal_DropsTheOperation_WhenAnEndVerbArrivesAndNoStartVerbRuns()
    {
        var store = new InMemoryTransferDecisionStore();
        using var heal = new WorkerSelfHealService(new StubHandler(), null, Transfers(store, new FakeSource(NodeA)), TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store, NodeA);
        await heal.HandleAsync(Suspend(row), new GatewayControlContext(1), CancellationToken.None);
        Assert.Equal(1, heal.HeldCount);

        await heal.HandleAsync(
            new ControlRequest(2, ControlKind.PlayerTransfer, row.TransferId, ControlVerb.ResumeSource, ControlNoArguments.Instance), new GatewayControlContext(1), CancellationToken.None);

        Assert.Equal(0, heal.HeldCount);
    }

    // ------------------------------------------------------------------ (5) the reclaim guard on an older database

    [Theory]
    [InlineData("SQLite Error 1: 'no such table: cluster_world_handoff'.")]
    [InlineData("Table 'characters.cluster_world_handoff' doesn't exist")]
    [InlineData("42P01: relation \"cluster_world_handoff\" does not exist")]
    public void IsMissingHandoffTable_RecognisesTheProvidersMissingTableErrors(string message)
    {
        Assert.True(WorldHost.IsMissingHandoffTable(new FakeDbException(message)));
        Assert.True(WorldHost.IsMissingHandoffTable(new InvalidOperationException("wrapped", new FakeDbException(message))));
    }

    [Theory]
    [InlineData("SQLite Error 5: 'database is locked'.")]
    [InlineData("Table 'characters.some_other_table' doesn't exist")]
    [InlineData("connection refused")]
    public void IsMissingHandoffTable_RefusesAnyOtherFailure_SoAnUnreachableJournalStillStopsTheReclaim(string message)
    {
        Assert.False(WorldHost.IsMissingHandoffTable(new FakeDbException(message)));
    }

    [Fact]
    public void IsMissingHandoffTable_IgnoresAnExceptionThatIsNotADatabaseError()
    {
        Assert.False(WorldHost.IsMissingHandoffTable(new InvalidOperationException("no such table: cluster_world_handoff")));
    }

    // ------------------------------------------------------------------ fixtures

    private static WorkerControlTransfer Transfers(InMemoryTransferDecisionStore store, FakeSource source) =>
        new(store, source, null, () => true, () => 1);

    private static async Task<TransferDecisionRecord> NewReservationAsync(InMemoryTransferDecisionStore store, WorkerNodeId sourceWorker) =>
        await store.CreateReservationAsync(Guid.NewGuid(), 4242UL, 1, 1, sourceWorker, NodeB, 0, 0, 1, 0, SourceToken);

    private static ControlRequest Suspend(TransferDecisionRecord row) =>
        new(1, ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, new ControlSuspendArguments(row.CharacterGuid, NodeB.Value, 1, 0, new TransferPosition(1f, 2f, 3f, 0.5f)));

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class StubHandler : IGatewayControlHandler
    {
        public Func<ControlRequest, Task> Hold { get; init; } = _ => Task.CompletedTask;

        public async Task<ControlReply> HandleAsync(ControlRequest request, GatewayControlContext context, CancellationToken cancellationToken)
        {
            await Hold(request).ConfigureAwait(false);
            return new ControlReply(request.RequestId, request.Kind, request.OperationId, request.Verb, ControlStatus.Ok, string.Empty, ControlNoPayload.Instance);
        }
    }

    private sealed class FakeSource(WorkerNodeId workerId) : ITransferSourceWorker
    {
        private int _resumes;
        private int _retires;

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public WorkerNodeId WorkerId => workerId;

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resumes);
            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _retires);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe : ITransferPlayerProbe
    {
        public TransferActiveBlockers Inspect(Player player) => new(false, false, false, false, false, false);

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
}
