using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Tests.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Control;

/// <summary>
/// C11 of _wf/handoff-control-DESIGN.md (section 3.5): the worker heals itself from the journal when its gateway is gone. The control requests travel over a real
/// <see cref="GatewaySessionLinkHost"/> and <see cref="GatewaySessionLinkClient"/> on loopback, through the real <see cref="WorkerControlService"/> wrapped by the
/// <see cref="WorkerSelfHealService"/>; the participants are fakes (the process boundary), the handoff journal is a fake row table and the transfer journal is the in-memory
/// decision store (the database boundary). "The gateway is gone" is the client end of the link disposed. Another writer's abort or commit (a restarted gateway, a second
/// gateway's recovery) is the journal row changed behind the worker's back.
/// </summary>
public sealed class WorkerSelfHealTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly WorkerNodeId NodeA = new("world-a");
    private static readonly WorkerNodeId NodeB = new("world-b");
    private static readonly Guid IncarnationA = Guid.NewGuid();
    private static readonly Guid IncarnationB = Guid.NewGuid();
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, IncarnationA);
    private static readonly TransferPosition Position = new(1f, 2f, 3f, 0.5f);

    // ------------------------------------------------------------------ whole-World handoff

    [Fact]
    public async Task Handoff_GatewayGoneAfterFrozen_AbortedRow_ResumesTheWorldOnItsOwn()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, Fast, out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        journal.SetStage(id, WorldHandoffStage.Frozen);
        Assert.Equal(1, heal.HeldCount);

        await rig.DisposeAsync(); // the gateway process dies

        // PreCommit: the worker never aborts on its own, however many passes run.
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, await heal.RunPassAsync(CancellationToken.None));
        }

        Assert.Equal(0, source.Resumes);
        Assert.Equal(0, source.Retires);

        // A second gateway's recovery aborts the row; the worker notices on its own (the loop polls the journal).
        journal.Set(id, state: PlayerTransferDecisionState.Aborted);
        await WaitUntilAsync(() => heal.HeldCount == 0);
        Assert.Equal(1, source.Resumes);
        Assert.Equal(0, source.Retires);
        Assert.Equal(1, heal.Healed);
    }

    [Fact]
    public async Task Handoff_GatewayGoneAfterFrozen_CommittedRow_LeavesTheWorldRetiringAndNeverResumed()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, Fast, out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        await rig.DisposeAsync();

        journal.Set(id, state: PlayerTransferDecisionState.Committed, stage: WorldHandoffStage.EpochTakeover);
        await WaitUntilAsync(() => heal.HeldCount == 0);

        Assert.Equal(1, source.Retires);
        Assert.Equal(0, source.Resumes);
    }

    [Fact]
    public async Task Handoff_AdmissionsClosedButNeverFrozen_AbortedRow_ReopensTheWorld()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.Warm, PlayerTransferDecisionState.PreCommit);
        await using Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.StopAdmissions)).Status);

        journal.Set(id, state: PlayerTransferDecisionState.Aborted);
        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));

        Assert.Equal(1, source.Resumes);
    }

    [Fact]
    public async Task Handoff_AResumeTheGatewaySentItself_EndsTheHold()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        await using Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        Assert.Equal(1, heal.HeldCount);

        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Resume)).Status);

        Assert.Equal(0, heal.HeldCount);
        Assert.Equal(1, source.Resumes); // the gateway's own, not the self-heal's
        Assert.Equal(0, heal.Healed);
    }

    [Fact]
    public async Task Handoff_ARefusedFreeze_StartsNoHold()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit); // Freeze needs AdmissionsStopped
        await using Rig rig = await Rig.StartAsync(heal);

        ControlReply reply = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);

        Assert.Equal(ControlStatus.Refused, reply.Status);
        Assert.Equal(0, source.Freezes);
        Assert.Equal(0, heal.HeldCount);
    }

    [Fact]
    public async Task Handoff_ARefusedFreeze_KeepsAHoldAnEarlierVerbOfTheOperationTook()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.Warm, PlayerTransferDecisionState.PreCommit);
        await using Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.StopAdmissions)).Status);

        // The row is still at Warm, so Freeze is refused; the closed admissions must stay tracked.
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);

        Assert.Equal(1, heal.HeldCount);
    }

    [Fact]
    public async Task Handoff_AVerbStillRunning_IsHeld_AndASelfHealNeverDropsItBeforeTheVerbEnds()
    {
        var journal = new FakeHandoffJournal();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeHandoffSource { FreezeGate = gate };
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);

        Task<ControlReply> freeze = heal.HandleAsync(
            new ControlRequest(1, ControlKind.WorldHandoff, id, ControlVerb.Freeze, ControlNoArguments.Instance), new GatewayControlContext(1), CancellationToken.None);
        await source.FreezeStarted.Task.WaitAsync(Budget);
        Assert.Equal(1, heal.HeldCount); // tracked before the participant finished, so a lost reply cannot lose the hold

        // The row is aborted while the freeze still runs: the heal resumes, but the freeze may still park the world afterwards, so the hold stays.
        journal.Set(id, state: PlayerTransferDecisionState.Aborted);
        Assert.Equal(1, await heal.RunPassAsync(CancellationToken.None));
        Assert.Equal(1, source.Resumes);

        gate.SetResult();
        Assert.Equal(ControlStatus.Ok, (await freeze.WaitAsync(Budget)).Status);
        Assert.Equal(1, heal.HeldCount);
        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));
        Assert.Equal(2, source.Resumes); // the second pass resumed what the late freeze parked
    }

    [Fact]
    public async Task Handoff_AFailedJournalRead_IsRetriedOnTheNextPass()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using WorkerSelfHealService heal = SelfHeal(journal, source, TimeSpan.FromHours(1), out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        await using Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        journal.Set(id, state: PlayerTransferDecisionState.Aborted);
        journal.FailReads = 1;

        Assert.Equal(1, await heal.RunPassAsync(CancellationToken.None));
        Assert.Equal(0, source.Resumes);
        Assert.Equal(0, await heal.RunPassAsync(CancellationToken.None));
        Assert.Equal(1, source.Resumes);
    }

    [Fact]
    public async Task Handoff_WithTheKeyOff_AnswersDisabled_AndTracksNothing()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        var control = new WorkerControlService(
            handoffEnabled: false, handoff: new WorkerControlHandoff(journal, source, null), transfersEnabled: false, transfers: null, identity: Identity);
        await using var heal = new WorkerSelfHealService(control, control.Handoff, control.Transfers, TimeSpan.FromHours(1));
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        await using Rig rig = await Rig.StartAsync(heal);

        ControlReply reply = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);

        Assert.Equal(ControlStatus.Disabled, reply.Status);
        Assert.Equal(0, source.Freezes);
        Assert.Equal(0, heal.HeldCount);
    }

    // ------------------------------------------------------------------ player transfer

    [Fact]
    public async Task Transfer_GatewayGoneAfterSuspend_AbortedRow_ResumesThePlayerOnItsOwn()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using WorkerSelfHealService heal = SelfHeal(store, source, Fast);
        TransferDecisionRecord row = await NewReservationAsync(store);
        Rig rig = await Rig.StartAsync(heal);
        ControlReply suspended = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, Suspend(row));
        Assert.Equal(ControlStatus.Ok, suspended.Status);
        Assert.Equal(1, heal.HeldCount);

        await rig.DisposeAsync(); // the gateway process dies

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, await heal.RunPassAsync(CancellationToken.None));
        }

        Assert.Equal(0, source.Resumes);
        Assert.Equal(0, source.Retires);

        // A second gateway's transfer recovery aborts the PreCommit row.
        await store.AbortPreCommitAsync(row.TransferId, "recovered by another gateway");
        await WaitUntilAsync(() => heal.HeldCount == 0);

        Assert.Equal(1, source.Resumes);
        Assert.Equal(0, source.Retires);
        Assert.Equal(row.CharacterGuid, source.LastResumedCharacter);
        Assert.Equal(row.TransferId, source.LastResumedTransfer);
    }

    [Fact]
    public async Task Transfer_GatewayGoneAfterSuspend_CommittedRow_RetiresThePlayerHere()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using WorkerSelfHealService heal = SelfHeal(store, source, Fast);
        TransferDecisionRecord row = await NewReservationAsync(store);
        Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, Suspend(row))).Status);
        await rig.DisposeAsync();

        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(row.TransferId, row.CharacterGuid, NodeA, NodeB, SourceToken);
        foreach (PlayerTransferStage stage in new[] { PlayerTransferStage.SuspendAndCapture, PlayerTransferStage.InstallPassive, PlayerTransferStage.StageWorldPort })
        {
            await store.AdvancePreCommitStageAsync(row.TransferId, stage, bundle);
        }

        await store.CommitDecisionAsync(row.TransferId, IncarnationB);
        await WaitUntilAsync(() => heal.HeldCount == 0);

        Assert.Equal(1, source.Retires);
        Assert.Equal(0, source.Resumes);
        Assert.Equal(row.TransferId, source.LastRetiredTransfer);
    }

    [Fact]
    public async Task Transfer_ARefusedSuspend_StartsNoHold()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using WorkerSelfHealService heal = SelfHeal(store, source, TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store);
        await using Rig rig = await Rig.StartAsync(heal);

        // The request names another target than the journal row: refused before the participant runs.
        ControlReply reply = await rig.SendAsync(
            ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, new ControlSuspendArguments(row.CharacterGuid, "world-z", 1, 0, Position));

        Assert.Equal(ControlStatus.Refused, reply.Status);
        Assert.Equal(0, source.Suspends);
        Assert.Equal(0, heal.HeldCount);
    }

    [Fact]
    public async Task Transfer_AResumeSourceTheGatewaySentItself_EndsTheHold()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using WorkerSelfHealService heal = SelfHeal(store, source, TimeSpan.FromHours(1));
        TransferDecisionRecord row = await NewReservationAsync(store);
        await using Rig rig = await Rig.StartAsync(heal);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, Suspend(row))).Status);

        ControlReply resumed = await rig.SendAsync(
            ControlKind.PlayerTransfer, row.TransferId, ControlVerb.ResumeSource, new ControlReasonArguments("test abort"));

        Assert.Equal(ControlStatus.Ok, resumed.Status);
        Assert.Equal(0, heal.HeldCount);
        Assert.Equal(1, source.Resumes);
    }

    [Fact]
    public async Task ADisposedService_RefusesNewHolds_AndStopsItsLoop()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        WorkerSelfHealService heal = SelfHeal(journal, source, Fast, out _);
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        await heal.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => heal.HandleAsync(
            new ControlRequest(1, ControlKind.WorldHandoff, id, ControlVerb.Freeze, ControlNoArguments.Instance), new GatewayControlContext(1), CancellationToken.None));
        Assert.Equal(0, source.Freezes);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Budget);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    private static WorkerControlIdentity Identity() => new(NodeA, false, IncarnationA, "node-a", "world", [0u, 1u]);

    private static WorkerSelfHealService SelfHeal(FakeHandoffJournal journal, FakeHandoffSource source, TimeSpan interval, out WorkerControlService control)
    {
        control = new WorkerControlService(
            handoffEnabled: true, handoff: new WorkerControlHandoff(journal, source, null), transfersEnabled: false, transfers: null, identity: Identity);
        return new WorkerSelfHealService(control, control.Handoff, control.Transfers, interval);
    }

    private static WorkerSelfHealService SelfHeal(InMemoryTransferDecisionStore store, FakeTransferSource source, TimeSpan interval)
    {
        var control = new WorkerControlService(
            handoffEnabled: false, handoff: null, transfersEnabled: true, transfers: new WorkerControlTransfer(store, source, null, () => true, () => 12), identity: Identity);
        return new WorkerSelfHealService(control, control.Handoff, control.Transfers, interval);
    }

    private static async Task<TransferDecisionRecord> NewReservationAsync(InMemoryTransferDecisionStore store, ulong character = 4242UL) =>
        await store.CreateReservationAsync(Guid.NewGuid(), character, 1, 1, NodeA, NodeB, 0, 0, 1, 0, SourceToken);

    private static ControlSuspendArguments Suspend(TransferDecisionRecord row) => new(row.CharacterGuid, NodeB.Value, 1, 0, Position);

    /// <summary>The worker's link host on a loopback port and a real gateway link client admitted on it; sends control requests and collects the replies by request id.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const string Secret = "link-secret-0123456789";
        private readonly GatewaySessionLinkHost _host;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ControlReply>> _pending = new();
        private long _nextRequest;
        private int _disposed;

        private Rig(GatewaySessionLinkHost host, GatewaySessionLinkClient client)
        {
            _host = host;
            Client = client;
        }

        public GatewaySessionLinkClient Client { get; }

        public static async Task<Rig> StartAsync(IGatewayControlHandler? handler)
        {
            using var cts = new CancellationTokenSource(Budget);
            var host = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, "worker", Guid.NewGuid(), 1, "dev", 1, 1),
                new NoSessionsAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0))
            {
                ControlHandler = handler,
            };
            host.Start();
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway-a", Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret);
            var rig = new Rig(host, client);
            client.ControlReplyReceived = reply =>
            {
                if (rig._pending.TryRemove(reply.RequestId, out TaskCompletionSource<ControlReply>? waiting))
                {
                    waiting.TrySetResult(reply);
                }
            };
            try
            {
                await client.ConnectAsync(cts.Token);
                while (host.AdmittedLinkCount != 1)
                {
                    await Task.Delay(10, cts.Token);
                }
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }

            return rig;
        }

        public async Task<ControlReply> SendAsync(ControlKind kind, Guid operationId, ControlVerb verb, ControlArguments? arguments = null)
        {
            ulong id = (ulong)Interlocked.Increment(ref _nextRequest);
            var waiting = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiting;
            Assert.True(Client.SendControlRequest(new ControlRequest(id, kind, operationId, verb, arguments ?? ControlNoArguments.Instance)));
            ControlReply reply = await waiting.Task.WaitAsync(Budget);
            Assert.Equal(id, reply.RequestId);
            Assert.Equal(verb, reply.Verb);
            return reply;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await Client.DisposeAsync();
            await _host.DisposeAsync();
        }
    }

    private sealed class NoSessionsAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "no sessions in this test"));
    }

    /// <summary>The handoff row table (the database boundary): the worker only reads rows; the coordinator-side members are not used by it and refuse.</summary>
    private sealed class FakeHandoffJournal : IWorldHandoffStore
    {
        private readonly ConcurrentDictionary<Guid, WorldHandoffRecord> _rows = new();
        private int _failReads;

        /// <summary>How many of the next reads throw.</summary>
        public int FailReads
        {
            get => Volatile.Read(ref _failReads);
            set => Volatile.Write(ref _failReads, value);
        }

        public Guid Add(WorldHandoffStage stage, PlayerTransferDecisionState state)
        {
            Guid id = Guid.NewGuid();
            _rows[id] = new WorldHandoffRecord(id, NodeA, NodeB, stage, state, SourceToken, null, null, null, 1, 1);
            return id;
        }

        public void SetStage(Guid id, WorldHandoffStage stage) => _rows[id] = _rows[id] with { Stage = stage };

        public void Set(Guid id, PlayerTransferDecisionState? state = null, WorldHandoffStage? stage = null)
        {
            WorldHandoffRecord row = _rows[id];
            PlayerTransferDecisionState next = state ?? row.DecisionState;
            _rows[id] = row with
            {
                DecisionState = next,
                Stage = stage ?? row.Stage,
                AbortReason = next == PlayerTransferDecisionState.Aborted ? "aborted by another gateway" : row.AbortReason,
                TargetOwnershipToken = next == PlayerTransferDecisionState.Committed ? new CharacterOwnershipToken("world", 4, IncarnationB) : row.TargetOwnershipToken,
                TargetOwnerNode = next == PlayerTransferDecisionState.Committed ? "node-b" : row.TargetOwnerNode,
            };
        }

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failReads) >= 0)
            {
                throw new InvalidOperationException("the journal is unreachable");
            }

            Interlocked.Exchange(ref _failReads, 0);
            return Task.FromResult(_rows.TryGetValue(handoffId, out WorldHandoffRecord? row) ? row : null);
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _rows.Values.Where(r => !r.IsResolved)]);

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default) =>
            throw Unused();

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default) => throw Unused();

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default) => throw Unused();

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) => throw Unused();

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) => throw Unused();

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default) => throw Unused();

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default) => throw Unused();

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw Unused();

        private static NotSupportedException Unused() => new("the worker only reads handoff rows; the coordinator writes them");
    }

    private sealed class FakeHandoffSource : IWorldHandoffSource
    {
        private int _freezes;
        private int _resumes;
        private int _retires;

        public int Freezes => Volatile.Read(ref _freezes);

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public TaskCompletionSource? FreezeGate { get; init; }

        public TaskCompletionSource FreezeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerNodeId WorkerId => NodeA;

        public CharacterOwnershipToken Ownership => SourceToken;

        public Task StopAdmissionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task FreezeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _freezes);
            FreezeStarted.TrySetResult();
            if (FreezeGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resumes);
            return Task.CompletedTask;
        }

        public Task RetireAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _retires);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransferSource : ITransferSourceWorker
    {
        private int _suspends;
        private int _resumes;
        private int _retires;

        public int Suspends => Volatile.Read(ref _suspends);

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public ulong LastResumedCharacter { get; private set; }

        public Guid LastResumedTransfer { get; private set; }

        public Guid LastRetiredTransfer { get; private set; }

        public WorkerNodeId WorkerId => NodeA;

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _suspends);
            return Task.FromResult(PlayerTransferValidatorTests.CreateSampleValidBundle(transferId, characterGuid, NodeA, targetWorkerId, SourceToken));
        }

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
        {
            LastResumedCharacter = characterGuid;
            LastResumedTransfer = transferId;
            Interlocked.Increment(ref _resumes);
            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
        {
            LastRetiredTransfer = transferId;
            Interlocked.Increment(ref _retires);
            return Task.CompletedTask;
        }
    }
}
