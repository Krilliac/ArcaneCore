using System.Diagnostics;
using System.Net;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Handoff.Remote;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>
/// C5 of _wf/handoff-control-DESIGN.md: the real <see cref="WorldHandoffCoordinator"/> drives a whole-World handoff through <see cref="RemoteWorldHandoffSource"/> and
/// <see cref="RemoteWorldHandoffTarget"/> over <see cref="LinkControlChannel"/>s. Two real <see cref="GatewaySessionLinkHost"/>s (worker A and standby B) on loopback and real
/// <see cref="GatewaySessionLinkClient"/>s carry the frames, and a real <see cref="WorkerControlService"/> answers on each. Fakes sit only at the process boundary (the world
/// participants behind the services, the gateway's router) and the database boundary (one in-memory journal the coordinator writes and the workers read).
/// </summary>
public sealed class RemoteWorldHandoffTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly WorkerNodeId NodeA = new("world-a");
    private static readonly WorkerNodeId NodeB = new("world-b");
    private static readonly Guid IncarnationA = Guid.NewGuid();
    private static readonly Guid IncarnationB = Guid.NewGuid();
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, IncarnationA);

    private static readonly string[] FullRun =
    [
        "stage:Begun",
        "B.Warm",
        "stage:Warm",
        "A.StopAdmissions",
        "stage:AdmissionsStopped",
        "router.Hold",
        "A.Freeze",
        "stage:Frozen",
        "A.Flush",
        "stage:Flushed",
        "stage:EpochTakeover",
        "A.RetireWrites",
        "A.ReleaseWriterLock",
        "B.Promote",
        "router.Reroute",
        "stage:Rerouted",
        "A.Retire",
        "stage:Retired",
    ];

    [Fact]
    public async Task Coordinator_OverTheRealLinks_RunsTheStagesInTheDesignOrder()
    {
        await using Pair pair = await Pair.StartAsync();
        Guid id = Guid.NewGuid();
        WorldHandoffCoordinator coordinator = await pair.CoordinatorAsync(id);

        WorldHandoffRecord record = await coordinator.RunAsync(id, pair.StageHook, CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(FullRun, pair.Events.Snapshot());
        Assert.Equal(WorldHandoffStage.Retired, record.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, record.DecisionState);
        Assert.Equal(new CharacterOwnershipToken("world", 4, IncarnationB), record.TargetOwnershipToken);
        Assert.Equal(record.TargetOwnershipToken, pair.Target.PromotedWith);
        Assert.Equal(new uint[] { 0, 1 }, pair.Journal.LastTakeoverMaps);
    }

    [Fact]
    public async Task ARefusedFreeze_AbortsTheHandoff_AndResumesA()
    {
        await using Pair pair = await Pair.StartAsync();
        Guid id = Guid.NewGuid();
        WorldHandoffCoordinator coordinator = await pair.CoordinatorAsync(id);

        // The journal no longer says AdmissionsStopped when the Freeze arrives, so worker A refuses it (it never freezes on the coordinator's word alone).
        Task Hook(WorldHandoffStage stage, CancellationToken ct)
        {
            pair.Events.Add("stage:" + stage);
            if (stage == WorldHandoffStage.AdmissionsStopped)
            {
                pair.Journal.SetStage(id, WorldHandoffStage.Warm);
            }

            return Task.CompletedTask;
        }

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() => coordinator.RunAsync(id, Hook, CancellationToken.None).WaitAsync(Budget));

        ControlRefusedException cause = Assert.IsType<ControlRefusedException>(aborted.InnerException);
        Assert.Equal(ControlStatus.Refused, cause.Reply.Status);
        Assert.Equal(ControlVerb.Freeze, cause.Reply.Verb);
        Assert.Equal(0, pair.Source.Freezes);
        Assert.Equal(1, pair.Source.Resumes);
        Assert.Equal(PlayerTransferDecisionState.Aborted, pair.Journal.Current(id).DecisionState);
        string[] events = pair.Events.Snapshot();
        Assert.Equal(["stage:Begun", "B.Warm", "stage:Warm", "A.StopAdmissions", "stage:AdmissionsStopped", "router.Hold", "A.Resume", "router.Release"], events);
    }

    [Fact]
    public async Task AFlushThatTimesOut_AbortsTheHandoff_AndResumesA()
    {
        var flushGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Pair pair = await Pair.StartAsync(new LinkControlTimeouts { Flush = TimeSpan.FromMilliseconds(300) }, source => source.FlushGate = flushGate);
        Guid id = Guid.NewGuid();
        WorldHandoffCoordinator coordinator = await pair.CoordinatorAsync(id);

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() => coordinator.RunAsync(id, pair.StageHook, CancellationToken.None).WaitAsync(Budget));

        ControlTimeoutException cause = Assert.IsType<ControlTimeoutException>(aborted.InnerException);
        Assert.Equal(ControlVerb.Flush, cause.Verb);
        Assert.Equal(1, pair.Source.Resumes);
        Assert.Equal(PlayerTransferDecisionState.Aborted, pair.Journal.Current(id).DecisionState);
        Assert.DoesNotContain("B.Promote", pair.Events.Snapshot());
        flushGate.SetResult();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task LinkLoss_FailsAPendingCall_WithinOneSecond_AndEveryLaterOne()
    {
        var freezeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Pair pair = await Pair.StartAsync(new LinkControlTimeouts(), source => source.FreezeGate = freezeGate);
        Guid id = pair.Journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);
        Task<ControlReply> pending = pair.ChannelA.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        await pair.Source.FreezeStarted.Task.WaitAsync(Budget);

        // Process death: the worker's link host goes away while its Freeze is still running.
        var clock = Stopwatch.StartNew();
        Task dispose = pair.DisposeHostAAsync();
        await Assert.ThrowsAsync<ControlLinkLostException>(() => pending).WaitAsync(Budget);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"the pending call took {clock.Elapsed.TotalMilliseconds:0} ms to fail");

        await Assert.ThrowsAsync<ControlLinkLostException>(() => pair.ChannelA.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Flush)).WaitAsync(Budget);
        Assert.False(pair.ChannelA.IsConnected);
        freezeGate.SetResult();
        await dispose.WaitAsync(Budget);
    }

    [Fact]
    public async Task APromoteThatTimesOut_StaysCommitted_AndRecoveryFinishesIt()
    {
        var promoteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Pair pair = await Pair.StartAsync(new LinkControlTimeouts { Promote = TimeSpan.FromMilliseconds(300) }, target: target => target.PromoteGate = promoteGate);
        Guid id = Guid.NewGuid();
        WorldHandoffCoordinator coordinator = await pair.CoordinatorAsync(id);

        ControlTimeoutException timeout = await Assert.ThrowsAsync<ControlTimeoutException>(() => coordinator.RunAsync(id, pair.StageHook, CancellationToken.None).WaitAsync(Budget));

        Assert.Equal(ControlVerb.Promote, timeout.Verb);
        WorldHandoffRecord stuck = pair.Journal.Current(id);
        Assert.Equal(PlayerTransferDecisionState.Committed, stuck.DecisionState);
        Assert.Equal(WorldHandoffStage.EpochTakeover, stuck.Stage);
        Assert.Equal(0, pair.Source.Retires);
        Assert.DoesNotContain("router.Reroute", pair.Events.Snapshot());

        // The promotion finishes on B; recovery repeats the abort-free roll-forward on a fresh coordinator and the worker answers the repeats from what it remembers.
        promoteGate.SetResult();
        WorldHandoffCoordinator recovery = await pair.CoordinatorAsync(id);
        WorldHandoffRecord done = await recovery.RecoverAsync(id).WaitAsync(Budget);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, done.DecisionState);
        Assert.Equal(1, pair.Target.Promotes);
        Assert.Equal(1, pair.Source.Releases);
        Assert.Equal(1, pair.Source.Retires);
        Assert.Equal(1, pair.Events.Snapshot().Count(e => e == "router.Reroute"));
    }

    [Fact]
    public async Task Connect_RefusesAWorkerThatIsNotTheRightKindOfParticipant()
    {
        await using Pair pair = await Pair.StartAsync();
        Guid id = Guid.NewGuid();

        // The standby cannot be the draining source; the active world cannot be the standby target.
        InvalidOperationException notSource = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RemoteWorldHandoffSource.ConnectAsync(pair.ChannelB, id, CancellationToken.None));
        Assert.Contains("standby", notSource.Message);
        InvalidOperationException notTarget = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RemoteWorldHandoffTarget.ConnectAsync(pair.ChannelA, id, CancellationToken.None));
        Assert.Contains("not a standby", notTarget.Message);
    }

    [Fact]
    public async Task Connect_RefusesAWorkerWhoseHandoffControlIsOff()
    {
        await using Pair pair = await Pair.StartAsync(handoffEnabledOnA: false);

        ControlRefusedException refused = await Assert.ThrowsAsync<ControlRefusedException>(
            () => RemoteWorldHandoffSource.ConnectAsync(pair.ChannelA, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(ControlStatus.Disabled, refused.Reply.Status);
    }

    [Fact]
    public async Task Connect_RefusesAWorkerThatSeesTwoGateways()
    {
        await using Pair pair = await Pair.StartAsync();
        await using GatewaySessionLinkClient second = new(
            GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway-b", Guid.NewGuid(), 1, "dev", 1, 1), pair.HostA.BoundEndPoint, Pair.Secret);
        using (var cts = new CancellationTokenSource(Budget))
        {
            await second.ConnectAsync(cts.Token);
            while (pair.HostA.AdmittedGatewayCount != 2)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => RemoteWorldHandoffSource.ConnectAsync(pair.ChannelA, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("2 admitted gateways", refused.Message);
    }

    // ------------------------------------------------------------------ rig

    /// <summary>World A and standby B, each behind a real link host with a real control service, and the gateway's two links with their control channels.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        public const string Secret = "link-secret-0123456789";
        private readonly GatewaySessionLinkClient _clientA;
        private readonly GatewaySessionLinkClient _clientB;
        private readonly GatewaySessionLinkHost _hostB;
        private readonly Lock _gate = new();
        private Task? _hostADisposal;

        private Pair(
            Journal journal, Events events, FakeSource source, FakeTarget target, GatewaySessionLinkHost hostA, GatewaySessionLinkHost hostB,
            GatewaySessionLinkClient clientA, GatewaySessionLinkClient clientB, LinkControlChannel channelA, LinkControlChannel channelB)
        {
            Journal = journal;
            Events = events;
            Source = source;
            Target = target;
            HostA = hostA;
            _hostB = hostB;
            _clientA = clientA;
            _clientB = clientB;
            ChannelA = channelA;
            ChannelB = channelB;
        }

        public Journal Journal { get; }

        public Events Events { get; }

        public FakeSource Source { get; }

        public FakeTarget Target { get; }

        public GatewaySessionLinkHost HostA { get; }

        public LinkControlChannel ChannelA { get; }

        public LinkControlChannel ChannelB { get; }

        public static async Task<Pair> StartAsync(
            LinkControlTimeouts? timeouts = null, Action<FakeSource>? source = null, Action<FakeTarget>? target = null, bool handoffEnabledOnA = true)
        {
            timeouts ??= new LinkControlTimeouts();
            var journal = new Journal();
            var events = new Events();
            var fakeSource = new FakeSource(events);
            var fakeTarget = new FakeTarget(events);
            source?.Invoke(fakeSource);
            target?.Invoke(fakeTarget);

            var serviceA = new WorkerControlService(
                handoffEnabled: handoffEnabledOnA, handoff: new WorkerControlHandoff(journal, fakeSource, null), transfersEnabled: false, transfers: null,
                identity: () => new WorkerControlIdentity(NodeA, false, IncarnationA, "node-a", "world", [0u, 1u]));
            var serviceB = new WorkerControlService(
                handoffEnabled: true, handoff: new WorkerControlHandoff(journal, null, fakeTarget), transfersEnabled: false, transfers: null,
                identity: () => new WorkerControlIdentity(NodeB, true, IncarnationB, "node-b", "world", [0u, 1u]));

            GatewaySessionLinkHost? hostA = null;
            GatewaySessionLinkHost? hostB = null;
            GatewaySessionLinkClient? clientA = null;
            GatewaySessionLinkClient? clientB = null;
            try
            {
                hostA = StartHost("worker-a", serviceA);
                hostB = StartHost("worker-b", serviceB);
                clientA = await ConnectAsync(hostA, "gateway-a");
                clientB = await ConnectAsync(hostB, "gateway-a");
                return new Pair(journal, events, fakeSource, fakeTarget, hostA, hostB, clientA, clientB,
                    new LinkControlChannel(clientA, timeouts), new LinkControlChannel(clientB, timeouts));
            }
            catch
            {
                await DisposeAllAsync(clientA, clientB, hostA, hostB);
                throw;
            }
        }

        /// <summary>The real coordinator over the two remote participants of <paramref name="handoffId"/> and a recording router.</summary>
        public async Task<WorldHandoffCoordinator> CoordinatorAsync(Guid handoffId)
        {
            RemoteWorldHandoffSource source = await RemoteWorldHandoffSource.ConnectAsync(ChannelA, handoffId, CancellationToken.None).WaitAsync(Budget);
            RemoteWorldHandoffTarget target = await RemoteWorldHandoffTarget.ConnectAsync(ChannelB, handoffId, CancellationToken.None).WaitAsync(Budget);
            return new WorldHandoffCoordinator(Journal, source, target, new RecordingRouter(Events));
        }

        public Task StageHook(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            Events.Add("stage:" + stage);
            return Task.CompletedTask;
        }

        /// <summary>"Kills" worker A's process: disposes its link host once; later calls return the same task.</summary>
        public Task DisposeHostAAsync()
        {
            lock (_gate)
            {
                return _hostADisposal ??= HostA.DisposeAsync().AsTask();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAllAsync(_clientA, _clientB, null, _hostB);
            await DisposeHostAAsync();
        }

        private static GatewaySessionLinkHost StartHost(string node, IGatewayControlHandler handler)
        {
            var host = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, node, Guid.NewGuid(), 1, "dev", 1, 1),
                new NoSessionsAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0))
            {
                ControlHandler = handler,
            };
            host.Start();
            return host;
        }

        private static async Task<GatewaySessionLinkClient> ConnectAsync(GatewaySessionLinkHost host, string gatewayNode)
        {
            using var cts = new CancellationTokenSource(Budget);
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, gatewayNode, Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret);
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
                await client.DisposeAsync();
                throw;
            }

            return client;
        }

        private static async Task DisposeAllAsync(GatewaySessionLinkClient? a, GatewaySessionLinkClient? b, GatewaySessionLinkHost? hostA, GatewaySessionLinkHost? hostB)
        {
            if (a is not null)
            {
                await a.DisposeAsync();
            }

            if (b is not null)
            {
                await b.DisposeAsync();
            }

            if (hostA is not null)
            {
                await hostA.DisposeAsync();
            }

            if (hostB is not null)
            {
                await hostB.DisposeAsync();
            }
        }
    }

    private sealed class NoSessionsAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "no sessions in this test"));
    }

    /// <summary>The ordered record of what the worker participants, the router and the coordinator's stage hook did.</summary>
    private sealed class Events
    {
        private readonly List<string> _list = [];

        public void Add(string what)
        {
            lock (_list)
            {
                _list.Add(what);
            }
        }

        public string[] Snapshot()
        {
            lock (_list)
            {
                return [.. _list];
            }
        }
    }

    private sealed class RecordingRouter(Events events) : IWorldHandoffRouter
    {
        public Task HoldAsync(CancellationToken cancellationToken)
        {
            events.Add("router.Hold");
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            events.Add("router.Release");
            return Task.CompletedTask;
        }

        public Task RerouteAsync(CancellationToken cancellationToken)
        {
            events.Add("router.Reroute");
            return Task.CompletedTask;
        }
    }

    /// <summary>The handoff row table (the database boundary): the coordinator writes it, the workers' control services read it.</summary>
    private sealed class Journal : IWorldHandoffStore
    {
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];

        public uint[]? LastTakeoverMaps { get; private set; }

        public Guid Add(WorldHandoffStage stage, PlayerTransferDecisionState state)
        {
            Guid id = Guid.NewGuid();
            Put(new WorldHandoffRecord(id, NodeA, NodeB, stage, state, SourceToken, null, null, null, 1, 1));
            return id;
        }

        public WorldHandoffRecord Current(Guid id)
        {
            lock (_records)
            {
                return _records[id];
            }
        }

        public void SetStage(Guid id, WorldHandoffStage stage) => Put(Current(id) with { Stage = stage });

        private WorldHandoffRecord Put(WorldHandoffRecord record)
        {
            lock (_records)
            {
                _records[record.HandoffId] = record;
            }

            return record;
        }

        public Task<WorldHandoffRecord> BeginAsync(
            Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Put(new WorldHandoffRecord(
                handoffId, sourceWorkerId, targetWorkerId, WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit, sourceOwnershipToken, null, null, null, 1, 1)));

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default) =>
            Task.FromResult(Put(Current(handoffId) with { Stage = nextStage }));

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
            Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default) =>
            CommitEpochTakeoverAsync(handoffId, targetOwnerNode, targetIncarnation, null, cancellationToken);

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
            Guid handoffId, string targetOwnerNode, Guid targetIncarnation, IReadOnlyCollection<uint>? targetOwnedMaps, CancellationToken cancellationToken)
        {
            LastTakeoverMaps = targetOwnedMaps?.ToArray();
            WorldHandoffRecord before = Current(handoffId);
            var taken = new CharacterOwnershipToken(before.SourceOwnershipToken.Scope, before.SourceOwnershipToken.Epoch + 1, targetIncarnation);
            return Task.FromResult<WorldHandoffRecord?>(Put(before with
            {
                Stage = WorldHandoffStage.EpochTakeover,
                DecisionState = PlayerTransferDecisionState.Committed,
                TargetOwnershipToken = taken,
                TargetOwnerNode = targetOwnerNode,
            }));
        }

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Rerouted }));

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Retired, DecisionState = PlayerTransferDecisionState.Completed }));

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Aborted, DecisionState = PlayerTransferDecisionState.Aborted, AbortReason = reason }));

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult(_records.GetValueOrDefault(handoffId));
            }
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _records.Values.Where(r => !r.IsResolved)]);
            }
        }

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException("a handoff without player transfers");
    }

    private sealed class FakeSource(Events events) : IWorldHandoffSource
    {
        private int _freezes;
        private int _resumes;
        private int _retires;
        private int _releases;

        public int Freezes => Volatile.Read(ref _freezes);

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public int Releases => Volatile.Read(ref _releases);

        public TaskCompletionSource? FreezeGate { get; set; }

        public TaskCompletionSource? FlushGate { get; set; }

        public TaskCompletionSource FreezeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerNodeId WorkerId => NodeA;

        public CharacterOwnershipToken Ownership => SourceToken;

        public Task StopAdmissionsAsync(CancellationToken cancellationToken)
        {
            events.Add("A.StopAdmissions");
            return Task.CompletedTask;
        }

        public async Task FreezeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _freezes);
            events.Add("A.Freeze");
            FreezeStarted.TrySetResult();
            if (FreezeGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            events.Add("A.Flush");
            if (FlushGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resumes);
            events.Add("A.Resume");
            return Task.CompletedTask;
        }

        public Task RetireAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _retires);
            events.Add("A.Retire");
            return Task.CompletedTask;
        }

        public Task RetireWritesAsync(CancellationToken cancellationToken)
        {
            events.Add("A.RetireWrites");
            return Task.CompletedTask;
        }

        public Task ReleaseWriterLockAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releases);
            events.Add("A.ReleaseWriterLock");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTarget(Events events) : IWorldHandoffTarget
    {
        private int _promotes;
        private CharacterOwnershipToken? _promotedWith;

        public int Promotes => Volatile.Read(ref _promotes);

        public CharacterOwnershipToken? PromotedWith => Volatile.Read(ref _promotedWith);

        public TaskCompletionSource? PromoteGate { get; set; }

        public WorkerNodeId WorkerId => NodeB;

        public string OwnerNode => "node-b";

        public Guid Incarnation => IncarnationB;

        public Task WarmAsync(CancellationToken cancellationToken)
        {
            events.Add("B.Warm");
            return Task.CompletedTask;
        }

        public async Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _promotes);
            Volatile.Write(ref _promotedWith, takenOver);
            events.Add("B.Promote");
            if (PromoteGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }
    }
}
