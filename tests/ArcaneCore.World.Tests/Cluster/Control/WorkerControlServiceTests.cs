using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Data.Characters.Transfers;
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
/// C4 of _wf/handoff-control-DESIGN.md: the worker end of the unified control channel. A real <see cref="GatewaySessionLinkHost"/> on loopback and a real
/// <see cref="GatewaySessionLinkClient"/> carry the frames (link proof, MAC, admission and framing included); the participants behind <see cref="WorkerControlService"/>
/// are fakes (the process boundary) and the journals are a fake handoff row table and the in-memory transfer decision store (the database boundary).
/// </summary>
public sealed class WorkerControlServiceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly WorkerNodeId NodeA = new("world-a");
    private static readonly WorkerNodeId NodeB = new("world-b");
    private static readonly Guid IncarnationA = Guid.NewGuid();
    private static readonly Guid IncarnationB = Guid.NewGuid();
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, IncarnationA);
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, IncarnationB);
    private static readonly TransferPosition Position = new(1f, 2f, 3f, 0.5f);

    // ------------------------------------------------------------------ whole-World handoff (kind 1)

    [Fact]
    public async Task Handoff_RepeatedFreeze_RunsOnce()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, new FakeHandoffTarget()));
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);

        ControlReply first = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        ControlReply second = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);

        Assert.Equal(ControlStatus.Ok, first.Status);
        Assert.Equal(ControlStatus.Ok, second.Status);
        Assert.Equal(ControlVerb.Freeze, second.Verb);
        Assert.Equal(1, source.Freezes);
    }

    [Fact]
    public async Task Handoff_ARepeatThatArrivesWhileFreezeRuns_JoinsIt()
    {
        var journal = new FakeHandoffJournal();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeHandoffSource { FreezeGate = gate };
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, new FakeHandoffTarget()));
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);

        Task<ControlReply> first = rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        await source.FreezeStarted.Task.WaitAsync(Budget);
        Task<ControlReply> second = rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        await Task.Delay(100);
        Assert.False(first.IsCompleted);
        gate.SetResult();

        Assert.Equal(ControlStatus.Ok, (await first).Status);
        Assert.Equal(ControlStatus.Ok, (await second).Status);
        Assert.Equal(1, source.Freezes);
    }

    [Fact]
    public async Task Handoff_VerbsFollowTheJournalStage_AndRefuseOutOfOrder()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        var target = new FakeHandoffTarget();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, target));
        Guid id = journal.Add(WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit);

        // Freeze before the journal says AdmissionsStopped changes nothing.
        ControlReply early = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        Assert.Equal(ControlStatus.Refused, early.Status);
        Assert.Equal(0, source.Freezes);

        // Warm needs the incarnation the standby really has.
        ControlReply wrong = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Warm, new ControlWarmArguments(Guid.NewGuid()));
        Assert.Equal(ControlStatus.Refused, wrong.Status);
        Assert.Equal(0, target.Warms);
        ControlReply warm = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Warm, new ControlWarmArguments(IncarnationB));
        Assert.Equal(ControlStatus.Ok, warm.Status);
        Assert.Equal(1, target.Warms);

        journal.SetStage(id, WorldHandoffStage.Warm);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.StopAdmissions)).Status);
        journal.SetStage(id, WorldHandoffStage.AdmissionsStopped);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        journal.SetStage(id, WorldHandoffStage.Frozen);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Flush)).Status);
        Assert.Equal((1, 1, 1), (source.Stops, source.Freezes, source.Flushes));

        // A handoff the journal does not know, or one that names another worker, is refused.
        ControlReply unknown = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.StopAdmissions);
        Assert.Equal(ControlStatus.Refused, unknown.Status);
        Guid foreign = journal.Add(WorldHandoffStage.Warm, PlayerTransferDecisionState.PreCommit, source: new WorkerNodeId("world-z"));
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.WorldHandoff, foreign, ControlVerb.StopAdmissions)).Status);
        Assert.Equal(1, source.Stops);
    }

    [Fact]
    public async Task Handoff_ResumeAfterTheTakeover_IsRefused_AndTheCommittedVerbsRun()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        var target = new FakeHandoffTarget();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, target));
        Guid id = journal.Add(WorldHandoffStage.EpochTakeover, PlayerTransferDecisionState.Committed, targetToken: TargetToken);

        ControlReply resume = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Resume);
        Assert.Equal(ControlStatus.Refused, resume.Status);
        Assert.Equal(0, source.Resumes);

        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.ReleaseWriterLock)).Status);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Retire)).Status);
        Assert.Equal((1, 1), (source.Releases, source.Retires));

        // Promote needs the token the journal committed.
        ControlReply stale = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Promote, new ControlTokenArguments(SourceToken));
        Assert.Equal(ControlStatus.Refused, stale.Status);
        Assert.Equal(0, target.Promotes);
        ControlReply promote = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Promote, new ControlTokenArguments(TargetToken));
        Assert.Equal(ControlStatus.Ok, promote.Status);
        Assert.Equal(1, target.Promotes);
    }

    [Fact]
    public async Task Handoff_RetireBeforeTheTakeover_IsRefused()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, new FakeHandoffTarget()));
        Guid id = journal.Add(WorldHandoffStage.Flushed, PlayerTransferDecisionState.PreCommit);

        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Retire)).Status);
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.ReleaseWriterLock)).Status);
        Assert.Equal((0, 0), (source.Retires, source.Releases));
    }

    [Fact]
    public async Task Handoff_FreezeAfterResume_IsRefused()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, new FakeHandoffTarget()));
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);

        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Resume)).Status);
        // The journal row was not written (the coordinator would abort it next); the worker's own tombstone already refuses the freeze.
        ControlReply freeze = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        Assert.Equal(ControlStatus.Refused, freeze.Status);
        Assert.Equal(0, source.Freezes);
        // The abort verb itself can be repeated (the roll-back is retried by recovery) and still runs once.
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Resume)).Status);
        Assert.Equal(1, source.Resumes);
    }

    [Fact]
    public async Task Handoff_AFailedFreeze_AnswersFailed_AndMayBeAskedAgain()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource { FreezeFailure = new InvalidOperationException("a quest settlement is in flight") };
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, new FakeHandoffTarget()));
        Guid id = journal.Add(WorldHandoffStage.AdmissionsStopped, PlayerTransferDecisionState.PreCommit);

        ControlReply failed = await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze);
        Assert.Equal(ControlStatus.Failed, failed.Status);
        Assert.Contains("quest settlement", failed.Reason);

        source.FreezeFailure = null;
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.WorldHandoff, id, ControlVerb.Freeze)).Status);
        Assert.Equal(2, source.Freezes);
    }

    [Fact]
    public async Task Handoff_Describe_ReportsTheWorker_AndTheAdmittedGatewayCount()
    {
        var journal = new FakeHandoffJournal();
        var source = new FakeHandoffSource();
        await using Rig rig = await Rig.StartAsync(HandoffService(journal, source, null));

        ControlReply reply = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe);

        Assert.Equal(ControlStatus.Ok, reply.Status);
        var describe = Assert.IsType<ControlDescribePayload>(reply.Payload);
        Assert.Equal("world-a", describe.WorkerId);
        Assert.False(describe.IsStandby);
        Assert.Equal(IncarnationA, describe.Incarnation);
        Assert.Equal("node-a", describe.OwnerNode);
        Assert.Equal("world", describe.OwnershipScope);
        Assert.Equal(new uint[] { 0, 1 }, describe.OwnedMaps.ToArray());
        Assert.Equal(SourceToken, describe.HeldSourceToken);
        Assert.Equal(1, describe.AdmittedGateways);
        Assert.True(describe.HandoffEnabled);
    }

    // ------------------------------------------------------------------ player transfer (kind 2)

    [Fact]
    public async Task Transfer_RepeatedSuspend_RunsOnce_AndRepliesWithTheBundleJson()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using Rig rig = await Rig.StartAsync(TransferService(store, source, new FakeTransferTarget()));
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.Reserve);
        var args = new ControlSuspendArguments(row.CharacterGuid, NodeB.Value, 1, 0, Position);

        ControlReply first = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, args);
        ControlReply second = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend, args);

        Assert.Equal(ControlStatus.Ok, first.Status);
        Assert.Equal(ControlStatus.Ok, second.Status);
        Assert.Equal(1, source.Suspends);
        byte[] expected = PlayerTransferBundleJson.SerializeToUtf8(source.Bundle(row));
        Assert.Equal(expected, Assert.IsType<ControlSuspendPayload>(first.Payload).BundleJson);
        Assert.Equal(expected, Assert.IsType<ControlSuspendPayload>(second.Payload).BundleJson);
    }

    [Fact]
    public async Task Transfer_SuspendOutOfStage_OrForAnotherTarget_IsRefused()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        await using Rig rig = await Rig.StartAsync(TransferService(store, source, new FakeTransferTarget()));
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.Reserve);

        ControlReply otherTarget = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Suspend,
            new ControlSuspendArguments(row.CharacterGuid, "world-z", 1, 0, Position));
        Assert.Equal(ControlStatus.Refused, otherTarget.Status);

        TransferDecisionRecord later = await NewTransferAsync(store, PlayerTransferStage.SuspendAndCapture, character: 5151UL);
        ControlReply pastReserve = await rig.SendAsync(ControlKind.PlayerTransfer, later.TransferId, ControlVerb.Suspend,
            new ControlSuspendArguments(later.CharacterGuid, NodeB.Value, 1, 0, Position));
        Assert.Equal(ControlStatus.Refused, pastReserve.Status);
        Assert.Equal(0, source.Suspends);
    }

    [Fact]
    public async Task Transfer_DiscardAndResumeSource_AfterTheCommit_AreRefused()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        var target = new FakeTransferTarget();
        await using Rig rig = await Rig.StartAsync(TransferService(store, source, target));
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.StageWorldPort, commit: true);

        ControlReply discard = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Discard, new ControlReasonArguments("late abort"));
        ControlReply resume = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.ResumeSource, new ControlReasonArguments("late abort"));

        Assert.Equal(ControlStatus.Refused, discard.Status);
        Assert.Equal(ControlStatus.Refused, resume.Status);
        Assert.Equal((0, 0), (target.Discards, source.Resumes));
    }

    [Fact]
    public async Task Transfer_RollForwardVerbs_RunOnlyWhenCommitted_WithTheJournalsTokens()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        var target = new FakeTransferTarget();
        await using Rig rig = await Rig.StartAsync(TransferService(store, source, target));
        TransferDecisionRecord pre = await NewTransferAsync(store, PlayerTransferStage.StageWorldPort, character: 7001UL);
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.StageWorldPort, commit: true);
        CharacterOwnershipToken committed = row.TargetOwnershipToken!;

        // Before the commit neither verb runs, whatever token it carries.
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.PlayerTransfer, pre.TransferId, ControlVerb.Activate, new ControlTokenArguments(committed))).Status);
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.PlayerTransfer, pre.TransferId, ControlVerb.RetireSource, new ControlTokenArguments(pre.SourceOwnershipToken))).Status);

        // After it, the token must be the committed one (Activate) or the superseded one (RetireSource).
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Activate, new ControlTokenArguments(row.SourceOwnershipToken))).Status);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Activate, new ControlTokenArguments(committed))).Status);
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.RetireSource, new ControlTokenArguments(committed))).Status);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.RetireSource, new ControlTokenArguments(row.SourceOwnershipToken))).Status);
        Assert.Equal((1, 1), (target.Activations, source.Retires));
    }

    [Fact]
    public async Task Transfer_InstallAfterDiscard_IsRefused_AndInstallItselfIsNeverRemembered()
    {
        var store = new InMemoryTransferDecisionStore();
        var source = new FakeTransferSource();
        var target = new FakeTransferTarget();
        await using Rig rig = await Rig.StartAsync(TransferService(store, source, target));
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.SuspendAndCapture);

        // The target's install is idempotent per transfer id and a repeat must join a load in progress, so each Install reaches it.
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Install)).Status);
        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Install)).Status);
        Assert.Equal(2, target.Installs);

        Assert.Equal(ControlStatus.Ok, (await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Discard, new ControlReasonArguments("changed my mind"))).Status);
        Assert.Equal(1, target.Discards);

        // The row is still PreCommit (the coordinator journals the abort itself); the worker's tombstone refuses the install.
        ControlReply install = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Install);
        Assert.Equal(ControlStatus.Refused, install.Status);
        Assert.Equal(2, target.Installs);
    }

    [Fact]
    public async Task Transfer_Reserve_ReportsWhetherTheTargetReserved_AndRefusesAWrongStageOrCharacter()
    {
        var store = new InMemoryTransferDecisionStore();
        var target = new FakeTransferTarget();
        await using Rig rig = await Rig.StartAsync(TransferService(store, new FakeTransferSource(), target));
        TransferDecisionRecord row = await NewTransferAsync(store, PlayerTransferStage.Reserve);

        ControlReply wrongCharacter = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Reserve, new ControlReserveArguments(row.CharacterGuid + 1, 1, 0));
        Assert.Equal(ControlStatus.Refused, wrongCharacter.Status);
        Assert.Equal(0, target.Reserves);

        target.Reserve = false;
        ControlReply full = await rig.SendAsync(ControlKind.PlayerTransfer, row.TransferId, ControlVerb.Reserve, new ControlReserveArguments(row.CharacterGuid, 1, 0));
        Assert.Equal(ControlStatus.Ok, full.Status);
        Assert.False(Assert.IsType<ControlReservePayload>(full.Payload).Reserved);

        target.Reserve = true;
        TransferDecisionRecord other = await NewTransferAsync(store, PlayerTransferStage.Reserve, character: 6001UL);
        ControlReply reserved = await rig.SendAsync(ControlKind.PlayerTransfer, other.TransferId, ControlVerb.Reserve, new ControlReserveArguments(other.CharacterGuid, 1, 0));
        var payload = Assert.IsType<ControlReservePayload>(reserved.Payload);
        Assert.True(payload.Reserved);
        Assert.Equal(0u, payload.InstanceId);

        TransferDecisionRecord later = await NewTransferAsync(store, PlayerTransferStage.SuspendAndCapture, character: 6002UL);
        Assert.Equal(ControlStatus.Refused, (await rig.SendAsync(ControlKind.PlayerTransfer, later.TransferId, ControlVerb.Reserve, new ControlReserveArguments(later.CharacterGuid, 1, 0))).Status);
    }

    [Fact]
    public async Task Transfer_TargetDescribe_ReportsTheTargetsIncarnationScopeAndCapacity()
    {
        var store = new InMemoryTransferDecisionStore();
        var target = new FakeTransferTarget();
        await using Rig rig = await Rig.StartAsync(TransferService(store, new FakeTransferSource(), target));

        ControlReply reply = await rig.SendAsync(ControlKind.PlayerTransfer, Guid.NewGuid(), ControlVerb.TargetDescribe);

        Assert.Equal(ControlStatus.Ok, reply.Status);
        var describe = Assert.IsType<ControlTargetDescribePayload>(reply.Payload);
        Assert.Equal(IncarnationB, describe.Incarnation);
        Assert.Equal("worker:east", describe.OwnershipScope);
        Assert.True(describe.AdmissionOpen);
        Assert.Equal(12, describe.FreeReservations);
    }

    // ------------------------------------------------------------------ opt-in keys

    [Fact]
    public async Task AKindWhoseKeyIsOff_AnswersDisabled_AndTheOtherKindStillWorks()
    {
        var store = new InMemoryTransferDecisionStore();
        var handoffOff = new WorkerControlService(
            handoffEnabled: false,
            handoff: new WorkerControlHandoff(new FakeHandoffJournal(), new FakeHandoffSource(), null),
            transfersEnabled: true,
            transfers: new WorkerControlTransfer(store, new FakeTransferSource(), new FakeTransferTarget(), () => true, () => 1),
            identity: Identity);
        await using Rig rig = await Rig.StartAsync(handoffOff);

        ControlReply describe = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe);
        ControlReply freeze = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Freeze);
        ControlReply transfer = await rig.SendAsync(ControlKind.PlayerTransfer, Guid.NewGuid(), ControlVerb.TargetDescribe);

        Assert.Equal(ControlStatus.Disabled, describe.Status);
        Assert.Equal(ControlStatus.Disabled, freeze.Status);
        Assert.Equal(ControlStatus.Ok, transfer.Status);

        var transfersOff = new WorkerControlService(
            handoffEnabled: true,
            handoff: new WorkerControlHandoff(new FakeHandoffJournal(), new FakeHandoffSource(), null),
            transfersEnabled: false,
            transfers: null,
            identity: Identity);
        await using Rig other = await Rig.StartAsync(transfersOff);
        ControlReply suspend = await other.SendAsync(ControlKind.PlayerTransfer, Guid.NewGuid(), ControlVerb.Suspend, new ControlSuspendArguments(1, "world-b", 1, 0, Position));
        Assert.Equal(ControlStatus.Disabled, suspend.Status);
        Assert.Equal(ControlStatus.Ok, (await other.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe)).Status);
    }

    [Fact]
    public async Task AWorkerWithNoControlService_AnswersDisabled_AndKeepsTheLinkUp()
    {
        await using Rig rig = await Rig.StartAsync(null);

        ControlReply first = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe);
        ControlReply second = await rig.SendAsync(ControlKind.PlayerTransfer, Guid.NewGuid(), ControlVerb.TargetDescribe);

        Assert.Equal(ControlStatus.Disabled, first.Status);
        Assert.Equal(ControlStatus.Disabled, second.Status);
        Assert.True(rig.Client.IsConnected);
    }

    [Fact]
    public async Task AHandlerThatThrows_IsAnsweredFailed_AndTheLinkStaysUp()
    {
        await using Rig rig = await Rig.StartAsync(new ThrowingHandler());

        ControlReply reply = await rig.SendAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe);

        Assert.Equal(ControlStatus.Failed, reply.Status);
        Assert.Contains("handler broke", reply.Reason);
        Assert.True(rig.Client.IsConnected);
    }

    // ------------------------------------------------------------------ helpers

    private static WorkerControlIdentity Identity() => new(NodeA, false, IncarnationA, "node-a", "world", [0u, 1u]);

    private static WorkerControlService HandoffService(FakeHandoffJournal journal, FakeHandoffSource? source, FakeHandoffTarget? target) =>
        new(handoffEnabled: true, handoff: new WorkerControlHandoff(journal, source, target), transfersEnabled: false, transfers: null, identity: Identity);

    private static WorkerControlService TransferService(InMemoryTransferDecisionStore store, FakeTransferSource? source, FakeTransferTarget? target) =>
        new(handoffEnabled: false, handoff: null, transfersEnabled: true,
            transfers: new WorkerControlTransfer(store, source, target, () => true, () => 12), identity: Identity);

    /// <summary>A journalled transfer A to B at <paramref name="upTo"/> (Reserve, SuspendAndCapture, InstallPassive or StageWorldPort), committed when asked.</summary>
    private static async Task<TransferDecisionRecord> NewTransferAsync(
        InMemoryTransferDecisionStore store, PlayerTransferStage upTo, bool commit = false, ulong character = 4242UL)
    {
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(id, character, NodeA, NodeB, SourceToken);
        TransferDecisionRecord row = await store.CreateReservationAsync(id, character, 1, 1, NodeA, NodeB, 0, 0, 1, 0, SourceToken);
        foreach (PlayerTransferStage stage in new[] { PlayerTransferStage.SuspendAndCapture, PlayerTransferStage.InstallPassive, PlayerTransferStage.StageWorldPort })
        {
            if (upTo < stage)
            {
                break;
            }

            row = await store.AdvancePreCommitStageAsync(id, stage, bundle);
        }

        if (commit)
        {
            row = await store.CommitDecisionAsync(id, IncarnationB);
        }

        return row;
    }

    /// <summary>The worker's link host on a loopback port and a real gateway link client admitted on it; sends control requests and collects the replies by request id.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const string Secret = "link-secret-0123456789";
        private readonly GatewaySessionLinkHost _host;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ControlReply>> _pending = new();
        private long _nextRequest;

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
            Assert.Equal(operationId, reply.OperationId);
            Assert.Equal(verb, reply.Verb);
            return reply;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _host.DisposeAsync();
        }
    }

    private sealed class NoSessionsAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "no sessions in this test"));
    }

    private sealed class ThrowingHandler : IGatewayControlHandler
    {
        public Task<ControlReply> HandleAsync(ControlRequest request, GatewayControlContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the handler broke");
    }

    // ------------------------------------------------------------------ handoff doubles

    /// <summary>The handoff row table (the database boundary): the worker only reads rows; the coordinator-side members are not used by it and refuse.</summary>
    private sealed class FakeHandoffJournal : IWorldHandoffStore
    {
        private readonly ConcurrentDictionary<Guid, WorldHandoffRecord> _rows = new();

        public Guid Add(
            WorldHandoffStage stage, PlayerTransferDecisionState state, WorkerNodeId? source = null, WorkerNodeId? target = null, CharacterOwnershipToken? targetToken = null)
        {
            Guid id = Guid.NewGuid();
            _rows[id] = new WorldHandoffRecord(id, source ?? NodeA, target ?? NodeB, stage, state, SourceToken, targetToken, targetToken is null ? null : "node-b", null, 1, 1);
            return id;
        }

        public void SetStage(Guid id, WorldHandoffStage stage) => _rows[id] = _rows[id] with { Stage = stage };

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue(handoffId, out WorldHandoffRecord? row) ? row : null);

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

        private static NotSupportedException Unused() => new("the worker control service only reads handoff rows; the coordinator writes them");
    }

    private sealed class FakeHandoffSource : IWorldHandoffSource
    {
        private int _stops;
        private int _freezes;
        private int _flushes;
        private int _resumes;
        private int _retires;
        private int _releases;

        public int Stops => Volatile.Read(ref _stops);

        public int Freezes => Volatile.Read(ref _freezes);

        public int Flushes => Volatile.Read(ref _flushes);

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public int Releases => Volatile.Read(ref _releases);

        public TaskCompletionSource? FreezeGate { get; init; }

        public TaskCompletionSource FreezeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? FreezeFailure { get; set; }

        public WorkerNodeId WorkerId => NodeA;

        public CharacterOwnershipToken Ownership => SourceToken;

        public Task StopAdmissionsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }

        public async Task FreezeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _freezes);
            FreezeStarted.TrySetResult();
            if (FreezeGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            if (FreezeFailure is { } failure)
            {
                throw failure;
            }
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushes);
            return Task.CompletedTask;
        }

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

        public Task ReleaseWriterLockAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releases);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandoffTarget : IWorldHandoffTarget
    {
        private int _warms;
        private int _promotes;

        public int Warms => Volatile.Read(ref _warms);

        public int Promotes => Volatile.Read(ref _promotes);

        public WorkerNodeId WorkerId => NodeB;

        public string OwnerNode => "node-b";

        public Guid Incarnation => IncarnationB;

        public Task WarmAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _warms);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _promotes);
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ transfer doubles

    private sealed class FakeTransferSource : ITransferSourceWorker
    {
        private int _suspends;
        private int _resumes;
        private int _retires;

        public int Suspends => Volatile.Read(ref _suspends);

        public int Resumes => Volatile.Read(ref _resumes);

        public int Retires => Volatile.Read(ref _retires);

        public WorkerNodeId WorkerId => NodeA;

        public PlayerTransferBundle Bundle(TransferDecisionRecord row) =>
            PlayerTransferValidatorTests.CreateSampleValidBundle(row.TransferId, row.CharacterGuid, NodeA, NodeB, row.SourceOwnershipToken);

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _suspends);
            return Task.FromResult(PlayerTransferValidatorTests.CreateSampleValidBundle(transferId, characterGuid, NodeA, targetWorkerId, SourceToken));
        }

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

    private sealed class FakeTransferTarget : ITransferTargetWorker
    {
        private int _reserves;
        private int _installs;
        private int _discards;
        private int _activations;

        public int Reserves => Volatile.Read(ref _reserves);

        public int Installs => Volatile.Read(ref _installs);

        public int Discards => Volatile.Read(ref _discards);

        public int Activations => Volatile.Read(ref _activations);

        public bool Reserve { get; set; } = true;

        public WorkerNodeId WorkerId => NodeB;

        public Guid Incarnation => IncarnationB;

        public string? OwnershipScope => "worker:east";

        public Task<bool> TryReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reserves);
            return Task.FromResult(Reserve);
        }

        public Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _installs);
            return Task.CompletedTask;
        }

        public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _discards);
            return Task.CompletedTask;
        }

        public Task ActivateTargetPlayerAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activations);
            return Task.CompletedTask;
        }
    }
}
