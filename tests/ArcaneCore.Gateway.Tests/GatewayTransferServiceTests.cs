using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// C8 of _wf/handoff-control-DESIGN.md: the gateway's per-player transfer service. The coordinator, the host, the recovery service, the slot state machine and the map router are the real ones; the fakes sit at
/// the process boundary (the two worker processes, behind the same <see cref="ITransferSourceWorker"/> / <see cref="ITransferTargetWorker"/> the remote proxies implement) and at the database boundary (the
/// in-memory decision store and a small handoff journal). The gateway's own transfer router is the real <see cref="GatewayTransferRouter"/> in the recovery tests, with no client sessions at all.
/// </summary>
public sealed class GatewayTransferServiceTests
{
    private const ulong Character = 4242UL;
    private const ulong OtherCharacter = 4343UL;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, Guid.NewGuid());
    private static readonly TransferPosition Landing = new(1629.36f, -4373.39f, 31.25f, 3.14f);

    // ------------------------------------------------------------------ slot state machine

    [Fact]
    public void TheSlotStateMachine_KeepsHandoffsAndTransfersApart()
    {
        var machine = new GatewaySlotStateMachine(["ek", "kal"]);
        Assert.Equal(GatewaySlotState.Idle, machine.StateOf("ek"));

        Assert.True(machine.TryBeginTransfer("ek", "kal", out IDisposable? first, out _));
        Assert.True(machine.TryBeginTransfer("ek", "kal", out IDisposable? second, out _));
        Assert.Equal(GatewaySlotState.Transferring, machine.StateOf("ek"));
        Assert.Equal(2, machine.TransfersOf("ek"));
        Assert.Equal(2, machine.TransfersOf("kal"));

        Assert.False(machine.TryBeginHandoff("ek", out IDisposable? refused, out string why));
        Assert.Null(refused);
        Assert.Contains("transfer", why);

        first!.Dispose();
        first.Dispose(); // a lease releases once
        Assert.Equal(1, machine.TransfersOf("ek"));
        second!.Dispose();
        Assert.Equal(GatewaySlotState.Idle, machine.StateOf("ek"));
        Assert.Equal(GatewaySlotState.Idle, machine.StateOf("kal"));

        Assert.True(machine.TryBeginHandoff("ek", out IDisposable? handoff, out _));
        Assert.Equal(GatewaySlotState.HandingOff, machine.StateOf("ek"));
        Assert.False(machine.TryBeginHandoff("ek", out _, out string twice));
        Assert.Contains("already", twice);
        Assert.False(machine.TryBeginTransfer("ek", "kal", out _, out string asSource));
        Assert.Contains("'ek'", asSource);
        Assert.False(machine.TryBeginTransfer("kal", "ek", out _, out string asTarget));
        Assert.Contains("'ek'", asTarget);
        Assert.Equal(0, machine.TransfersOf("kal")); // a refused begin changes nothing
        handoff!.Dispose();
        Assert.Equal(GatewaySlotState.Idle, machine.StateOf("ek"));

        Assert.False(machine.TryBeginTransfer("ek", "nowhere", out _, out string unknown));
        Assert.Contains("nowhere", unknown);
        Assert.False(machine.TryBeginHandoff("nowhere", out _, out _));
        Assert.True(machine.TryBeginTransfer("ek", "ek", out IDisposable? same, out _)); // the same slot twice counts once
        Assert.Equal(1, machine.TransfersOf("ek"));
        same!.Dispose();
        Assert.Equal(0, machine.TransfersOf("ek"));
    }

    // ------------------------------------------------------------------ submit

    [Fact]
    public async Task ASubmittedTransfer_RunsTheSixStages_ThroughTheCoordinator_AndReleasesTheSlots()
    {
        await using Rig rig = new();
        PlayerTransferRequest request = rig.Request();

        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(request);

        Assert.True(submission.Accepted, submission.Reason);
        Assert.NotNull(submission.Completion);
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);
        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(PlayerTransferDecisionState.Completed, outcome.DecisionState);
        Assert.Equal(new[] { "reserve", "install", "install", "activate" }, rig.Kal.Calls.ToArray());
        Assert.Equal(new[] { "suspend", "retire" }, rig.Ek.Calls.ToArray());
        Assert.Equal(new[] { "stage", "commit" }, rig.Gateway.Calls.ToArray());
        Assert.Equal(request.TransferId, rig.Gateway.StagedTransfer);
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("ek"));
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("kal"));
        Assert.False(rig.Service.IsRunning(request.TransferId));
        Assert.Empty(await rig.Store.ListUnresolvedAsync());
    }

    [Fact]
    public async Task ASubmit_IsRefused_WhileTheSourceOrTheTargetSlotIsHandingOff_AndJournalsNothing()
    {
        await using Rig rig = new();

        using (await rig.Service.EnterHandoffAsync("ek", CancellationToken.None))
        {
            GatewayTransferSubmission asSource = await rig.Service.SubmitAsync(rig.Request());
            Assert.False(asSource.Accepted);
            Assert.Contains("handing off", asSource.Reason);
            Assert.Null(asSource.Completion);
        }

        using (await rig.Service.EnterHandoffAsync("kal", CancellationToken.None))
        {
            GatewayTransferSubmission asTarget = await rig.Service.SubmitAsync(rig.Request());
            Assert.False(asTarget.Accepted);
            Assert.Contains("'kal'", asTarget.Reason);
        }

        Assert.Empty(await rig.Store.ListUnresolvedAsync());
        Assert.Empty(rig.Ek.Calls);
        Assert.Empty(rig.Kal.Calls);

        GatewayTransferSubmission afterwards = await rig.Service.SubmitAsync(rig.Request());
        Assert.True(afterwards.Accepted, afterwards.Reason); // the leases were released
        await afterwards.Completion!.WaitAsync(Budget);
    }

    [Fact]
    public async Task AHandoff_IsRefused_WhileATransferRuns_AndAllowedOnceItEnds()
    {
        await using Rig rig = new();
        rig.Ek.SuspendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(rig.Request());
        Assert.True(submission.Accepted, submission.Reason);
        await WaitUntilAsync(() => rig.Ek.Calls.Contains("suspend"));

        Assert.Equal(GatewaySlotState.Transferring, rig.Service.Machine.StateOf("ek"));
        Assert.Equal(GatewaySlotState.Transferring, rig.Service.Machine.StateOf("kal"));
        WorldHandoffUnavailableException refusedSource = await Assert.ThrowsAsync<WorldHandoffUnavailableException>(
            () => rig.Service.EnterHandoffAsync("ek", CancellationToken.None));
        Assert.Contains("transfer", refusedSource.Message);
        await Assert.ThrowsAsync<WorldHandoffUnavailableException>(() => rig.Service.EnterHandoffAsync("kal", CancellationToken.None));
        Assert.Equal(GatewaySlotState.Transferring, rig.Service.Machine.StateOf("ek")); // a refused handoff leaves the state as it was

        rig.Ek.SuspendGate.SetResult();
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);
        Assert.True(outcome.Succeeded, outcome.FailureMessage);

        using IDisposable lease = await rig.Service.EnterHandoffAsync("ek", CancellationToken.None);
        Assert.Equal(GatewaySlotState.HandingOff, rig.Service.Machine.StateOf("ek"));
    }

    [Fact]
    public async Task AHandoff_IsRefused_WhileATransferOfTheSlotIsUnresolvedInTheJournal()
    {
        await using Rig rig = new();
        Guid stuck = Guid.NewGuid();
        await rig.JournalAtStageAsync(stuck, Character, PlayerTransferStage.InstallPassive);

        WorldHandoffUnavailableException refused = await Assert.ThrowsAsync<WorldHandoffUnavailableException>(
            () => rig.Service.EnterHandoffAsync("kal", CancellationToken.None));
        Assert.Contains(stuck.ToString(), refused.Message);
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("kal")); // the refused handoff released its lease

        await rig.Store.AbortPreCommitAsync(stuck, "test");
        using IDisposable lease = await rig.Service.EnterHandoffAsync("kal", CancellationToken.None);
        Assert.Equal(GatewaySlotState.HandingOff, rig.Service.Machine.StateOf("kal"));
    }

    [Fact]
    public async Task ASubmit_IsRefused_WhileAHandoffOfTheSlotIsUnresolvedInTheJournal()
    {
        var handoffs = new FakeHandoffJournal();
        await using Rig rig = new(handoffs);
        handoffs.Add(new WorldHandoffRecord(
            Guid.NewGuid(), new WorkerNodeId("kal"), new WorkerNodeId("kal-standby"), WorldHandoffStage.Frozen, PlayerTransferDecisionState.PreCommit, SourceToken, null, null, null, 0, 0));

        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(rig.Request());

        Assert.False(submission.Accepted);
        Assert.Contains("world handoff", submission.Reason);
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("ek")); // the refused submit released its lease
        Assert.Empty(await rig.Store.ListUnresolvedAsync());
        Assert.Empty(rig.Ek.Calls);
    }

    [Fact]
    public async Task ASubmit_IsRefused_ForAGoneSession_AnUnroutableMap_AFullTarget_AndTheSameWorker()
    {
        await using Rig rig = new() { SessionOpen = false };
        GatewayTransferSubmission noSession = await rig.Service.SubmitAsync(rig.Request());
        Assert.False(noSession.Accepted);
        Assert.Contains("session", noSession.Reason);
        rig.SessionOpen = true;

        GatewayTransferSubmission unrouted = await rig.Service.SubmitAsync(rig.Request(targetMap: 99));
        Assert.False(unrouted.Accepted);
        Assert.Contains(ClusterMapPlacementTable.FailureMapUnassigned, unrouted.Reason);

        rig.Views["kal"] = new GatewayWorkerView(true, new WorkerStatusReport(10, 100, 100, WorkerStatusFlags.None)); // at its player limit
        GatewayTransferSubmission full = await rig.Service.SubmitAsync(rig.Request());
        Assert.False(full.Accepted);
        Assert.Contains(ClusterMapPlacementTable.FailureWorkerAtPlayerCapacity, full.Reason);
        rig.Views["kal"] = new GatewayWorkerView(true, null);

        GatewayTransferSubmission sameWorker = await rig.Service.SubmitAsync(rig.Request(targetMap: 0));
        Assert.False(sameWorker.Accepted);
        Assert.Contains("same worker", sameWorker.Reason);

        Assert.Empty(await rig.Store.ListUnresolvedAsync());
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("ek"));
    }

    [Fact]
    public async Task TheTargetReservingNothing_AbortsTheTransfer_AndResumesTheSource()
    {
        await using Rig rig = new();
        rig.Kal.RejectReservation = true;

        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(rig.Request());
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("ek"));
        Assert.Empty(await rig.Store.ListUnresolvedAsync());
    }

    // ------------------------------------------------------------------ production coupling with the handoff service

    [Fact]
    public async Task TheProductionConstructor_CouplesTheHandoffService_SoAHandoffIsRefusedWhileATransferRunsOrIsJournalled()
    {
        var handoffJournal = new FakeHandoffJournal();
        var store = new InMemoryTransferDecisionStore();
        store.SeedCharacterOwnership(Character, SourceToken);
        var options = new GatewayOptions
        {
            LinkSecret = StandbyWorkerTests.Secret,
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "ek", Maps = [0], StandbyNodeId = "ek-standby", StandbyAddress = "127.0.0.1", StandbyPort = 1 },
                new GatewayWorkerEntry { NodeId = "kal", Maps = [1] },
            ],
        };
        IServiceScopeFactory scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        using var server = new GatewayServer(scopes, Options.Create(options), NullLoggerFactory.Instance, transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));
        await using var handoffs = new GatewayHandoffService(server, handoffJournal, NullLoggerFactory.Instance);
        await using var transfers = new GatewayTransferService(
            server, store, store, new GatewayTransferOptions { Enabled = true }, handoffJournal, handoffs, NullLoggerFactory.Instance);

        Assert.Same(transfers, handoffs.Guard); // the production constructor installs itself as the handoff service's guard

        // A transfer of the slot is running (it holds the slot's lease): HandoffAsync is refused before its pre-flight.
        Assert.True(transfers.Machine.TryBeginTransfer("ek", "kal", out IDisposable? running, out _));
        WorldHandoffUnavailableException whileRunning = await Assert.ThrowsAsync<WorldHandoffUnavailableException>(() => handoffs.HandoffAsync("ek"));
        Assert.Contains("transfer", whileRunning.Message);
        running!.Dispose();

        // A transfer of the slot is unresolved in the journal: refused too, naming it, and the refused handoff left the slot idle.
        Guid stuck = Guid.NewGuid();
        await store.CreateReservationAsync(stuck, Character, 77, 5, new WorkerNodeId("ek"), new WorkerNodeId("kal"), 0, 0, 1, 0, SourceToken);
        WorldHandoffUnavailableException whileJournalled = await Assert.ThrowsAsync<WorldHandoffUnavailableException>(() => handoffs.HandoffAsync("ek"));
        Assert.Contains(stuck.ToString(), whileJournalled.Message);
        Assert.Equal(GatewaySlotState.Idle, transfers.Machine.StateOf("ek"));

        // The other direction, through the same guard: a handoff in progress keeps transfers of the slot out.
        await store.AbortPreCommitAsync(stuck, "test");
        using IDisposable handing = await handoffs.Guard!.EnterHandoffAsync("ek", CancellationToken.None);
        Assert.Equal(GatewaySlotState.HandingOff, transfers.Machine.StateOf("ek"));
        Assert.False(transfers.Machine.TryBeginTransfer("ek", "kal", out _, out _));
    }

    // ------------------------------------------------------------------ production wiring on real transports (design section 8)

    [Fact]
    public async Task OnRealLinks_ATargetThatRestartedBetweenTheReserveAndStage4_AbortsWithTargetIncarnationChanged_AndNeverCommits()
    {
        await using LinkRig rig = await LinkRig.StartAsync();
        rig.Kal.AfterInstall = () => rig.Kal.Incarnation = Guid.NewGuid(); // the target process "restarts" after it reserved and installed

        PlayerTransferRequest request = rig.Request();
        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(request);
        Assert.True(submission.Accepted, submission.Reason);
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.Equal(GatewayTransferService.TargetIncarnationChanged, outcome.FailureCode);
        Assert.Equal(new[] { "reserve", "install", "discard" }, rig.Kal.Calls.ToArray());
        Assert.Equal(new[] { "suspend", "resume" }, rig.Ek.Calls.ToArray());
        Assert.Equal(new[] { "stage", "cancel" }, rig.Gateway.Calls.ToArray()); // never "commit"
        Assert.Null((await rig.Store.GetDecisionAsync(request.TransferId))!.TargetOwnershipToken);
        Assert.Equal(GatewaySlotState.Idle, rig.Service.Machine.StateOf("ek"));
    }

    [Fact]
    public async Task OnRealLinks_ATargetWhoseIncarnationIsUnchanged_CommitsTheTransfer()
    {
        await using LinkRig rig = await LinkRig.StartAsync();

        PlayerTransferOutcome outcome = await (await rig.Service.SubmitAsync(rig.Request())).Completion!.WaitAsync(Budget);

        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(new[] { "reserve", "install", "install", "activate" }, rig.Kal.Calls.ToArray());
        Assert.Equal(new[] { "suspend", "retire" }, rig.Ek.Calls.ToArray());
        Assert.Equal(new[] { "stage", "commit" }, rig.Gateway.Calls.ToArray());
        Assert.False(rig.ProductionWiring.SessionExists(123456)); // ForServer's own session check reads the server's real connections: this gateway has none
    }

    // ------------------------------------------------------------------ pre-commit check (design 4.3)

    [Fact]
    public async Task ATargetThatRestartedSinceTheReserve_AbortsTheTransferBeforeTheCommit()
    {
        await using Rig rig = new();
        rig.IncarnationUnchanged = false;

        PlayerTransferRequest request = rig.Request();
        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(request);
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.Equal(GatewayTransferService.TargetIncarnationChanged, outcome.FailureCode);
        Assert.Contains("discard", rig.Kal.Calls);
        Assert.DoesNotContain("activate", rig.Kal.Calls);
        Assert.Contains("resume", rig.Ek.Calls);
        Assert.DoesNotContain("retire", rig.Ek.Calls);
        Assert.Contains("cancel", rig.Gateway.Calls);
        Assert.DoesNotContain("commit", rig.Gateway.Calls);
        Assert.Null((await rig.Store.GetDecisionAsync(request.TransferId))!.TargetOwnershipToken); // never committed
    }

    [Fact]
    public async Task ATargetThatStillHasItsIncarnation_CommitsTheTransfer()
    {
        await using Rig rig = new();
        PlayerTransferOutcome outcome = await (await rig.Service.SubmitAsync(rig.Request())).Completion!.WaitAsync(Budget);

        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(1, rig.IncarnationChecks);
    }

    // ------------------------------------------------------------------ recovery

    [Fact]
    public async Task Recovery_WithNoSessions_CompletesCommittedTransfers_AndAbortsPreCommitOnes()
    {
        await using Rig rig = new(useRealRouter: true);
        Guid committed = Guid.NewGuid();
        Guid preCommit = Guid.NewGuid();
        await rig.JournalAtStageAsync(committed, Character, PlayerTransferStage.StageWorldPort);
        await rig.Store.CommitDecisionAsync(committed, rig.Kal.Incarnation);
        await rig.JournalAtStageAsync(preCommit, OtherCharacter, PlayerTransferStage.StageWorldPort);

        TransferRecoverySummary summary = await rig.Service.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(2, summary.Scanned);
        Assert.Equal(1, summary.RolledForward);
        Assert.Equal(1, summary.RolledBack);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(PlayerTransferDecisionState.Completed, (await rig.Store.GetDecisionAsync(committed))!.DecisionState);
        Assert.Equal(PlayerTransferDecisionState.Aborted, (await rig.Store.GetDecisionAsync(preCommit))!.DecisionState);
        Assert.Contains("retire", rig.Ek.Calls); // the committed transfer: source retired, target activated; no session to switch
        Assert.Contains("activate", rig.Kal.Calls);
        Assert.Contains("resume", rig.Ek.Calls); // the pre-commit one: source resumed, shadow discarded
        Assert.Contains("discard", rig.Kal.Calls);
        Assert.Empty(await rig.Store.ListUnresolvedAsync());
    }

    [Fact]
    public async Task Recovery_LeavesATransferRunningHere_AndAWorldHandoffsOwnTransfers_Alone()
    {
        var handoffs = new FakeHandoffJournal();
        await using Rig rig = new(handoffs);

        // A transfer running on this service: it is parked inside Suspend, its row is PreCommit and not the recovery's to abort.
        rig.Ek.SuspendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayerTransferRequest running = rig.Request();
        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(running);
        Assert.True(submission.Accepted, submission.Reason);
        await WaitUntilAsync(() => rig.Ek.Calls.Contains("suspend"));

        // A transfer that belongs to an unresolved world handoff (its own recovery finishes it).
        Guid owned = Guid.NewGuid();
        await rig.JournalAtStageAsync(owned, OtherCharacter, PlayerTransferStage.InstallPassive);
        Guid handoffId = Guid.NewGuid();
        handoffs.Add(new WorldHandoffRecord(
            handoffId, new WorkerNodeId("ek"), new WorkerNodeId("kal"), WorldHandoffStage.Warm, PlayerTransferDecisionState.PreCommit, SourceToken, null, null, null, 0, 0));
        handoffs.Own(handoffId, owned);

        TransferRecoverySummary summary = await rig.Service.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.Scanned);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, (await rig.Store.GetDecisionAsync(running.TransferId))!.DecisionState);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, (await rig.Store.GetDecisionAsync(owned))!.DecisionState);

        rig.Ek.SuspendGate.SetResult();
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(Budget);
        Assert.True(outcome.Succeeded, outcome.FailureMessage);
    }

    [Fact]
    public async Task Recovery_SkipsTransfersBetweenWorkersThisGatewayDoesNotHave()
    {
        await using Rig rig = new();
        Guid foreign = Guid.NewGuid();
        await rig.Store.CreateReservationAsync(
            foreign, Character, 77, 5, new WorkerNodeId("elsewhere-a"), new WorkerNodeId("elsewhere-b"), 0, 0, 1, 0, SourceToken);

        TransferRecoverySummary summary = await rig.Service.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.Scanned);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, (await rig.Store.GetDecisionAsync(foreign))!.DecisionState);
    }

    [Fact]
    public async Task Stopping_RefusesNewSubmissions()
    {
        await using Rig rig = new();
        await rig.Service.StopAsync(CancellationToken.None);

        GatewayTransferSubmission submission = await rig.Service.SubmitAsync(rig.Request());

        Assert.False(submission.Accepted);
        Assert.Contains("stopping", submission.Reason);
    }

    // ------------------------------------------------------------------ commit-tolerant router

    [Fact]
    public async Task TheCommitTolerantRouter_SwitchesNothingForASessionThatIsNotStaged_WhereTheRouterItselfFails()
    {
        var router = new GatewayTransferRouter(new NoSessions());
        var tolerant = new GatewayCommitTolerantRouter(router, router.IsStaged, NullLogger.Instance);
        Guid transfer = Guid.NewGuid();
        CharacterOwnershipToken target = new("world", 4, Guid.NewGuid());

        Assert.False(router.IsStaged(transfer, 9));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CommitRouteSwitchAsync(transfer, Character, 9, new WorkerNodeId("kal"), target));

        await tolerant.CommitRouteSwitchAsync(transfer, Character, 9, new WorkerNodeId("kal"), target); // no session: nothing to do, no throw
        await tolerant.CancelStagedWorldPortAsync(transfer, Character, 9, new WorkerNodeId("ek"), "test");
    }

    // ------------------------------------------------------------------ map router

    [Fact]
    public void TheMapRouter_PlacesByOwnershipAndExcludesWorkersThatCannotTakeAPlayer()
    {
        var routes = new GatewayRouteTable([new GatewayWorkerEntry { NodeId = "ek", Maps = [0] }, new GatewayWorkerEntry { NodeId = "kal", Maps = [1] }]);
        routes.LinkUp("ek", null, true);
        routes.LinkUp("kal", null, true);
        var views = new Dictionary<string, GatewayWorkerView>
        {
            ["ek"] = new(true, null),
            ["kal"] = new(true, new WorkerStatusReport(5, 10, 100, WorkerStatusFlags.None)),
        };
        var router = new GatewayClusterMapRouter(() => routes, node => views.TryGetValue(node, out GatewayWorkerView view) ? (GatewayWorkerView?)view : null);

        Assert.True(router.TryResolveTargetWorker(1, 0, out WorkerNodeId worker, out string? failure));
        Assert.Equal("kal", worker.Value);
        Assert.Null(failure);
        Assert.True(router.TryGetWorker(new WorkerNodeId("kal"), out ClusterWorkerStatus status));
        Assert.True(status.CanAcceptPlayer);
        Assert.Equal(10, status.ActivePlayerCount);
        Assert.False(router.TryGetWorker(new WorkerNodeId("nobody"), out _));

        views["kal"] = new GatewayWorkerView(true, new WorkerStatusReport(5, 10, 100, WorkerStatusFlags.Standby));
        Assert.False(router.TryResolveTargetWorker(1, 0, out _, out failure));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerNotReady, failure);

        views["kal"] = new GatewayWorkerView(true, new WorkerStatusReport(5, 10, 100, WorkerStatusFlags.Restarting));
        Assert.False(router.TryResolveTargetWorker(1, 0, out _, out failure));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerDraining, failure);

        views["kal"] = new GatewayWorkerView(true, new WorkerStatusReport(5, 100, 100, WorkerStatusFlags.None));
        Assert.False(router.TryResolveTargetWorker(1, 0, out _, out failure));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerAtPlayerCapacity, failure);

        views["kal"] = new GatewayWorkerView(true, new WorkerStatusReport(5, 100, 0, WorkerStatusFlags.None)); // PlayerLimit 0: none
        Assert.True(router.TryResolveTargetWorker(1, 0, out _, out _));

        routes.LinkDown("kal"); // a worker whose link is down owns nothing
        Assert.False(router.TryResolveTargetWorker(1, 0, out _, out failure));
        Assert.Equal(ClusterMapPlacementTable.FailureMapUnassigned, failure);
    }

    [Fact]
    public void TheMapRouter_LeavesInstanceIdsToTheTargetWorker()
    {
        var routes = new GatewayRouteTable([new GatewayWorkerEntry { NodeId = "ek", Maps = [0] }, new GatewayWorkerEntry { NodeId = "kal", Maps = [1] }]);
        routes.LinkUp("ek", null, true);
        routes.LinkUp("kal", null, true);
        var router = new GatewayClusterMapRouter(() => routes, _ => new GatewayWorkerView(true, null));

        Assert.False(router.TryBindInstance(1, 0, new WorkerNodeId("kal"), out string? zero));
        Assert.Equal(ClusterMapPlacementTable.FailureInvalidInstanceId, zero);
        Assert.True(router.TryBindInstance(1, 7, new WorkerNodeId("kal"), out _)); // the owner: confirmed, nothing recorded
        Assert.False(router.TryBindInstance(1, 7, new WorkerNodeId("ek"), out _)); // not the owner
        Assert.True(router.TryAllocateOrResolveInstance(1, 7, out WorkerNodeId owner, out _));
        Assert.Equal("kal", owner.Value);
        Assert.False(router.UnbindInstance(1, 7));
    }

    // ------------------------------------------------------------------ hosting

    [Fact]
    public void TheTransferHosting_IsRegisteredOnlyWithItsOptInKey()
    {
        Assert.False(Registers(new Dictionary<string, string?>()));
        Assert.False(Registers(new Dictionary<string, string?> { ["Gateway:Transfers:Enabled"] = "false" }));
        Assert.True(Registers(new Dictionary<string, string?> { ["Gateway:Transfers:Enabled"] = "true" }));
        Assert.True(Registers(new Dictionary<string, string?> { ["Gateway:Transfers:Enabled"] = "true", ["Gateway:Handoff:Enabled"] = "true" })); // both on: one characters connection, no duplicate registration

        static bool Registers(Dictionary<string, string?> values)
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddGateway(configuration);
            bool service = services.Any(d => d.ServiceType == typeof(GatewayTransferService));
            bool characters = services.Any(d => d.ServiceType == typeof(ArcaneCore.Data.Characters.CharacterDbContext));
            bool handoff = values.GetValueOrDefault("Gateway:Handoff:Enabled") == "true";
            Assert.Equal(service || handoff, characters); // the characters database is the journal and is registered with its hosting, never alone
            return service;
        }
    }

    [Fact]
    public void TheWorkerLinks_CarryControlChannels_WithTransfersEnabledAlone()
    {
        IServiceScopeFactory scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new GatewayOptions());

        var off = new GatewayServer(scopes, options, NullLoggerFactory.Instance);
        var on = new GatewayServer(scopes, options, NullLoggerFactory.Instance, transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));

        Assert.Null(off.ControlTimeouts);
        Assert.NotNull(on.ControlTimeouts);
    }

    [Fact]
    public void TheTransferOptions_RejectNonPositiveIntervals_AndDefaultToOff()
    {
        var defaults = new GatewayTransferOptions();
        Assert.False(defaults.Enabled);
        Assert.Empty(defaults.Validate());

        IReadOnlyList<string> problems = new GatewayTransferOptions { RecoveryInterval = TimeSpan.Zero, AnomalyAfter = TimeSpan.FromSeconds(-1) }.Validate();
        Assert.Contains(problems, p => p.StartsWith("Gateway:Transfers:RecoveryInterval", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Transfers:AnomalyAfter", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ rig and fakes

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not become true in time");
            await Task.Delay(10);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(FakeHandoffJournal? handoffs = null, bool useRealRouter = false)
        {
            Routes = new GatewayRouteTable([new GatewayWorkerEntry { NodeId = "ek", Maps = [0] }, new GatewayWorkerEntry { NodeId = "kal", Maps = [1] }]);
            Routes.LinkUp("ek", null, true);
            Routes.LinkUp("kal", null, true);
            Views["ek"] = new GatewayWorkerView(true, null);
            Views["kal"] = new GatewayWorkerView(true, null);
            Store.SeedCharacterOwnership(Character, SourceToken);
            Store.SeedCharacterOwnership(OtherCharacter, SourceToken);

            ITransferGatewayRouter gateway = Gateway;
            if (useRealRouter)
            {
                var router = new GatewayTransferRouter(new NoSessions());
                gateway = new GatewayCommitTolerantRouter(router, router.IsStaged, NullLogger.Instance);
            }

            var wiring = new GatewayTransferWiring(
                ["ek", "kal"],
                new GatewayClusterMapRouter(() => Routes, node => Views.TryGetValue(node, out GatewayWorkerView view) ? (GatewayWorkerView?)view : null),
                gateway,
                [Ek, Kal],
                [Ek, Kal],
                (worker, transferId, cancellationToken) =>
                {
                    Interlocked.Increment(ref _incarnationChecks);
                    return Task.FromResult(IncarnationUnchanged);
                },
                _ => SessionOpen,
                node => [node]);
            Service = new GatewayTransferService(new GatewayTransferOptions(), Store, Store, handoffs, wiring);
        }

        private int _incarnationChecks;

        public GatewayRouteTable Routes { get; }

        public Dictionary<string, GatewayWorkerView> Views { get; } = [];

        public InMemoryTransferDecisionStore Store { get; } = new();

        public FakeNode Ek { get; } = new("ek");

        public FakeNode Kal { get; } = new("kal");

        public FakeGateway Gateway { get; } = new();

        public GatewayTransferService Service { get; }

        public bool SessionOpen { get; set; } = true;

        public bool IncarnationUnchanged { get; set; } = true;

        public int IncarnationChecks => Volatile.Read(ref _incarnationChecks);

        /// <summary>A request of <see cref="Character"/> on Eastern Kingdoms (map 0, worker ek) to Kalimdor (map 1, worker kal), unless <paramref name="targetMap"/> says otherwise.</summary>
        public PlayerTransferRequest Request(uint targetMap = 1)
        {
            return new PlayerTransferRequest(Guid.NewGuid(), Character, 77, 5, new WorkerNodeId("ek"), 0, 0, targetMap, 0, Landing, SourceToken);
        }

        /// <summary>Journals a transfer of <paramref name="character"/> from ek to kal up to <paramref name="stage"/> (Reserve to StageWorldPort), without running any worker.</summary>
        public async Task JournalAtStageAsync(Guid transferId, ulong character, PlayerTransferStage stage)
        {
            await Store.CreateReservationAsync(transferId, character, 77, 5, new WorkerNodeId("ek"), new WorkerNodeId("kal"), 0, 0, 1, 0, SourceToken);
            if (stage == PlayerTransferStage.Reserve)
            {
                return;
            }

            PlayerTransferBundle bundle = FakeNode.Bundle(transferId, character, new WorkerNodeId("ek"), new WorkerNodeId("kal"), 1, 0, Landing);
            await Store.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.SuspendAndCapture, bundle);
            if (stage == PlayerTransferStage.SuspendAndCapture)
            {
                return;
            }

            await Store.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.InstallPassive, bundle);
            if (stage == PlayerTransferStage.InstallPassive)
            {
                return;
            }

            await Store.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.StageWorldPort, bundle);
        }

        public async ValueTask DisposeAsync()
        {
            Ek.SuspendGate?.TrySetResult();
            await Service.DisposeAsync();
        }
    }

    /// <summary>
    /// The production wiring (<see cref="GatewayTransferWiring.ForServer"/>) on real transports: a real <see cref="GatewayServer"/> linked over loopback to two real <see cref="GatewaySessionLinkHost"/>s whose control
    /// dispatcher is a real <see cref="WorkerControlService"/> in front of the fake participants. Only the gateway's session router and its session check are replaced (no client session exists in this rig).
    /// </summary>
    private sealed class LinkRig : IAsyncDisposable
    {
        private readonly GatewaySessionLinkHost _ekHost;
        private readonly GatewaySessionLinkHost _kalHost;
        private readonly GatewayServer _server;

        private LinkRig(
            GatewaySessionLinkHost ekHost, GatewaySessionLinkHost kalHost, GatewayServer server, InMemoryTransferDecisionStore store, FakeNode ek, FakeNode kal, FakeGateway gateway)
        {
            _ekHost = ekHost;
            _kalHost = kalHost;
            _server = server;
            Store = store;
            Ek = ek;
            Kal = kal;
            Gateway = gateway;
            ProductionWiring = GatewayTransferWiring.ForServer(server, null, NullLoggerFactory.Instance);
            Service = new GatewayTransferService(
                new GatewayTransferOptions(), store, store, null, ProductionWiring with { GatewayRouter = gateway, SessionExists = _ => true });
        }

        public InMemoryTransferDecisionStore Store { get; }

        public FakeNode Ek { get; }

        public FakeNode Kal { get; }

        public FakeGateway Gateway { get; }

        /// <summary>What <see cref="GatewayTransferWiring.ForServer"/> built for the rig's server, before the router and the session check were replaced.</summary>
        public GatewayTransferWiring ProductionWiring { get; }

        public GatewayTransferService Service { get; }

        public PlayerTransferRequest Request() =>
            new(Guid.NewGuid(), Character, 77, 5, new WorkerNodeId("ek"), 0, 0, 1, 0, Landing, SourceToken);

        public static async Task<LinkRig> StartAsync()
        {
            using var cts = new CancellationTokenSource(Budget);
            var store = new InMemoryTransferDecisionStore();
            store.SeedCharacterOwnership(Character, SourceToken);
            var ek = new FakeNode("ek");
            var kal = new FakeNode("kal");
            GatewaySessionLinkHost ekHost = StartHost("ek", new WorkerControlService(
                handoffEnabled: false, handoff: null, transfersEnabled: true,
                transfers: new WorkerControlTransfer(store, ek, null, () => true, () => 8),
                identity: () => new WorkerControlIdentity(ek.WorkerId, false, ek.Incarnation, "node-ek", "world", [0u])));
            GatewaySessionLinkHost kalHost = StartHost("kal", new WorkerControlService(
                handoffEnabled: false, handoff: null, transfersEnabled: true,
                transfers: new WorkerControlTransfer(store, null, kal, () => true, () => 8),
                identity: () => new WorkerControlIdentity(kal.WorkerId, false, kal.Incarnation, "node-kal", "world", [1u])));
            GatewayServer? server = null;
            try
            {
                var options = new GatewayOptions
                {
                    BindAddress = IPAddress.Loopback.ToString(),
                    Port = 0,
                    LinkSecret = StandbyWorkerTests.Secret,
                    LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                    RealmId = 1,
                    ContentRevision = "dev",
                    CharactersSchemaVersion = 1,
                    WorldSchemaVersion = 1,
                    Workers =
                    [
                        new GatewayWorkerEntry { NodeId = "ek", Address = IPAddress.Loopback.ToString(), Port = ekHost.BoundEndPoint.Port, Maps = [0] },
                        new GatewayWorkerEntry { NodeId = "kal", Address = IPAddress.Loopback.ToString(), Port = kalHost.BoundEndPoint.Port, Maps = [1] },
                    ],
                };
                IServiceScopeFactory scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
                server = new GatewayServer(scopes, Options.Create(options), NullLoggerFactory.Instance, transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));
                await server.StartAsync(cts.Token);
                await server.WaitForAllLinksAsync(cts.Token);
                return new LinkRig(ekHost, kalHost, server, store, ek, kal, new FakeGateway());
            }
            catch
            {
                if (server is not null)
                {
                    await server.StopAsync(CancellationToken.None);
                    server.Dispose();
                }

                await ekHost.DisposeAsync();
                await kalHost.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
            await _ekHost.DisposeAsync();
            await _kalHost.DisposeAsync();
        }

        private static GatewaySessionLinkHost StartHost(string node, IGatewayControlHandler handler)
        {
            var host = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, node, Guid.NewGuid(), 1, "dev", 1, 1),
                new NoSessionsAcceptor(), () => 1, StandbyWorkerTests.Secret, new IPEndPoint(IPAddress.Loopback, 0))
            {
                ControlHandler = handler,
            };
            host.Start();
            return host;
        }
    }

    private sealed class NoSessionsAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(AuthResponseCode.Failed, "no sessions in this test"));
    }

    /// <summary>One worker process behind both participant contracts, recording what it was asked.</summary>
    private sealed class FakeNode(string id) : ITransferSourceWorker, ITransferTargetWorker
    {
        public WorkerNodeId WorkerId { get; } = new(id);

        private Guid _incarnation = Guid.NewGuid();

        public Guid Incarnation
        {
            get { lock (Calls) { return _incarnation; } }
            set { lock (Calls) { _incarnation = value; } }
        }

        /// <summary>Runs after an install: a test changes <see cref="Incarnation"/> there, between the reserve and the commit.</summary>
        public Action? AfterInstall { get; set; }

        public ConcurrentQueue<string> Calls { get; } = new();

        public bool RejectReservation { get; set; }

        /// <summary>When set, <c>Suspend</c> parks until it completes (a transfer in the middle of stage 2).</summary>
        public TaskCompletionSource? SuspendGate { get; set; }

        public static PlayerTransferBundle Bundle(
            Guid transferId, ulong character, WorkerNodeId source, WorkerNodeId target, uint targetMap, uint targetInstance, TransferPosition position) => new()
        {
            TransferId = transferId,
            CharacterGuid = character,
            CharacterId = (int)character,
            AccountId = 77,
            SessionGeneration = 5,
            SourceWorkerId = source,
            TargetWorkerId = target,
            SourceOwnershipToken = SourceToken,
            TargetMapId = targetMap,
            TargetInstanceId = targetInstance,
            TargetPosition = position,
            Health = 100,
            Level = 10,
            Powers = [0u, 0u, 0u, 0u, 0u],
        };

        public async Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("suspend");
            if (SuspendGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return Bundle(transferId, characterGuid, WorkerId, targetWorkerId, targetMapId, targetInstanceId, targetPosition);
        }

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("resume");
            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("retire");
            return Task.CompletedTask;
        }

        public Task<bool> TryReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("reserve");
            return Task.FromResult(!RejectReservation);
        }

        public Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("install");
            AfterInstall?.Invoke();
            return Task.CompletedTask;
        }

        public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("discard");
            return Task.CompletedTask;
        }

        public Task ActivateTargetPlayerAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("activate");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGateway : ITransferGatewayRouter
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public Guid StagedTransfer { get; private set; }

        public Task StageWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId,
            TransferPosition targetPosition, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("stage");
            StagedTransfer = transferId;
            return Task.CompletedTask;
        }

        public Task CancelStagedWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId restoredWorkerId, string abortReason, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("cancel");
            return Task.CompletedTask;
        }

        public Task CommitRouteSwitchAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId targetWorkerId, CharacterOwnershipToken targetOwnershipToken,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("commit");
            return Task.CompletedTask;
        }
    }

    /// <summary>A gateway with no client sessions at all (a restarted one).</summary>
    private sealed class NoSessions : IGatewayTransferEnvironment
    {
        public IGatewayTransferSession? FindSession(long sessionGeneration) => null;

        public Task<GatewayTransferTarget?> OpenTargetSessionAsync(IGatewayTransferSession session, string targetNode, CancellationToken cancellationToken) =>
            Task.FromResult<GatewayTransferTarget?>(null);
    }

    /// <summary>The handoff journal as the transfer service reads it: the unresolved handoffs and the transfer ids each one owns.</summary>
    private sealed class FakeHandoffJournal : IWorldHandoffStore
    {
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];
        private readonly Dictionary<Guid, List<Guid>> _owned = [];

        public void Add(WorldHandoffRecord record)
        {
            lock (_records)
            {
                _records[record.HandoffId] = record;
            }
        }

        public void Own(Guid handoffId, Guid transferId)
        {
            lock (_records)
            {
                if (!_owned.TryGetValue(handoffId, out List<Guid>? ids))
                {
                    _owned[handoffId] = ids = [];
                }

                ids.Add(transferId);
            }
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _records.Values.Where(r => !r.IsResolved)]);
            }
        }

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult<IReadOnlyList<Guid>>(_owned.TryGetValue(handoffId, out List<Guid>? ids) ? [.. ids] : []);
            }
        }

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult(_records.GetValueOrDefault(handoffId));
            }
        }

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException();
    }
}
