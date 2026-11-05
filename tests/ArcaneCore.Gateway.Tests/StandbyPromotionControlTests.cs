using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Y10: a standby has no draining handoff source and no transfer participants while it waits; once it is promoted its control service runs transfers and is the source of a handoff.
/// The world is a real standby (its own container, shared databases) promoted by the Promote verb after the journal's epoch takeover, and the verbs go through the production <see cref="WorkerControlService"/>.
/// A slot's standby has its own node id, and the gateway's transfer rows name the slot, so the rows here name <see cref="Slot"/>, which is not the standby's node id. PromotedStandbyTransferTests runs whole transfers through a gateway.
/// </summary>
public sealed class StandbyPromotionControlTests
{
    private const string Slot = "slot-a";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task APromotedStandby_RunsATransfer_AndIsTheSourceOfAHandoff()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true, Configuration = Worker(standby: false) }, configureServices: null, cts.Token);
        await using SyntheticArcaneServer standby = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true, SharedDatabaseDirectory = active.DataDirectory, Configuration = Worker(standby: true) }, configureServices: null, cts.Token);
        WorldHost host = StandbyWorkerTests.Host(standby);
        WorkerControlService control = standby.Services.GetRequiredService<WorkerControlService>();
        WorkerNodeId node = new(standby.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayWorkerOptions>>().Value.NodeId);

        // While it waits: the handoff target only, and no transfer side. A transfer verb finds no journal.
        Assert.True(host.IsStandby);
        Assert.Null(control.Handoff!.Source);
        Assert.NotNull(control.Handoff.Target);
        Assert.Null(control.Transfers);
        ControlReply waiting = await control.HandleAsync(
            new ControlRequest(1, ControlKind.PlayerTransfer, Guid.NewGuid(), ControlVerb.TargetDescribe, ControlNoArguments.Instance), new GatewayControlContext(1), cts.Token);
        Assert.Equal(ControlStatus.Refused, waiting.Status);

        // The journal's handoff up to the takeover, then the gateway's Promote: the worker learns the slot it takes over from that verb.
        IWorldHandoffStore journal = control.Handoff.Journal;
        Guid promotion = Guid.NewGuid();
        await journal.BeginAsync(promotion, new WorkerNodeId("retiring-worker"), node, active.World.CharacterOwnership!, cts.Token);
        foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
        {
            await journal.AdvancePreCommitAsync(promotion, stage, cts.Token);
        }

        WorldHandoffRecord committed = Assert.IsType<WorldHandoffRecord>(await journal.CommitEpochTakeoverAsync(promotion, host.OwnerNode, host.Incarnation, cts.Token));
        CharacterOwnershipToken handed = Assert.IsType<CharacterOwnershipToken>(committed.TargetOwnershipToken);
        StandbyWorkerTests.ReleaseWriterLock(active);
        ControlReply promoted = await control.HandleAsync(
            new ControlRequest(10, ControlKind.WorldHandoff, promotion, ControlVerb.Promote, new ControlPromoteArguments(handed, Slot)), new GatewayControlContext(1), cts.Token);
        Assert.Equal(ControlStatus.Ok, promoted.Status);
        Assert.False(host.IsStandby);
        await journal.MarkReroutedAsync(promotion, cts.Token);
        await journal.MarkRetiredAsync(promotion, cts.Token);

        // Promoted: the same service object now has the draining source and the transfer side.
        Assert.NotNull(control.Handoff.Source);
        Assert.IsType<LocalWorldHandoffSource>(control.Handoff.Source);
        Assert.NotNull(control.Handoff.Target);
        Assert.NotNull(control.Transfers);
        Assert.NotNull(control.Transfers!.Target);
        Assert.NotNull(control.Transfers.Source);

        // The transfer side answers under the slot; handoffs keep naming this process.
        Assert.NotEqual(Slot, node.Value);
        Assert.Equal(Slot, control.Transfers.Target!.WorkerId.Value);
        Assert.Equal(Slot, control.Transfers.Source!.WorkerId.Value);

        // A transfer through it: this worker's slot is the target of a journalled transfer; the reservation is made by the real target participant.
        Guid transferId = Guid.NewGuid();
        await control.Transfers.Journal.CreateReservationAsync(transferId, 4242UL, 1, 1, new WorkerNodeId("other-worker"), new WorkerNodeId(Slot), 0, 0, 0, 0, handed, cts.Token);
        ControlReply describe = await control.HandleAsync(
            new ControlRequest(2, ControlKind.PlayerTransfer, transferId, ControlVerb.TargetDescribe, ControlNoArguments.Instance), new GatewayControlContext(1), cts.Token);
        Assert.Equal(ControlStatus.Ok, describe.Status);
        var capacity = Assert.IsType<ControlTargetDescribePayload>(describe.Payload);
        Assert.True(capacity.AdmissionOpen);
        ControlReply reserved = await control.HandleAsync(
            new ControlRequest(3, ControlKind.PlayerTransfer, transferId, ControlVerb.Reserve, new ControlReserveArguments(4242UL, 0, 0)), new GatewayControlContext(1), cts.Token);
        Assert.Equal(ControlStatus.Ok, reserved.Status);
        Assert.True(Assert.IsType<ControlReservePayload>(reserved.Payload).Reserved);

        // A handoff from it: the journalled handoff names this worker as its source; stop admissions and freeze run on the real source, and the resume gives the world back.
        Guid handoffId = Guid.NewGuid();
        await journal.BeginAsync(handoffId, node, new WorkerNodeId("next-standby"), handed, cts.Token);
        await journal.AdvancePreCommitAsync(handoffId, WorldHandoffStage.Warm, cts.Token);
        ControlReply stopped = await Handoff(control, 4, handoffId, ControlVerb.StopAdmissions, cts.Token);
        Assert.Equal(ControlStatus.Ok, stopped.Status);
        Assert.Equal(LocalWorldHandoffSource.DrainingReason, standby.Services.GetRequiredService<WorkerAdmission>().ClosedReason);
        await journal.AdvancePreCommitAsync(handoffId, WorldHandoffStage.AdmissionsStopped, cts.Token);
        ControlReply frozen = await Handoff(control, 5, handoffId, ControlVerb.Freeze, cts.Token);
        Assert.Equal(ControlStatus.Ok, frozen.Status);
        Assert.True(((LocalWorldHandoffSource)control.Handoff.Source!).IsFrozen);
        ControlReply resumed = await Handoff(control, 6, handoffId, ControlVerb.Resume, cts.Token);
        Assert.Equal(ControlStatus.Ok, resumed.Status);
        Assert.False(((LocalWorldHandoffSource)control.Handoff.Source!).IsFrozen);
    }

    /// <summary>
    /// A worker stops reporting Standby at the ownership takeover, but its features are attached only when the promotion completes. In between, a verb must be served by the standby's participants
    /// (not fail inside the attach) and the promoted participants must be built once, after the promotion completed.
    /// </summary>
    [Fact]
    public async Task AVerbDuringThePromotionWindow_IsServedByTheStandbyParticipants_AndTheAttachWaitsForTheCompletedPromotion()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true, Configuration = Worker(standby: false) }, configureServices: null, cts.Token);
        await using SyntheticArcaneServer standby = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true, SharedDatabaseDirectory = active.DataDirectory, Configuration = Worker(standby: true) }, configureServices: null, cts.Token);
        WorkerControlHandoff waiting = standby.Services.GetRequiredService<WorkerControlService>().Handoff!;
        int builds = 0;
        bool completed = false;
        Guid incarnation = Guid.NewGuid();
        var control = new WorkerControlService(
            handoffEnabled: true,
            handoff: waiting,
            transfersEnabled: false,
            transfers: null,
            identity: () => new WorkerControlIdentity(new WorkerNodeId("window-worker"), false, incarnation, "owner", "scope", Array.Empty<uint>()),
            onPromoted: _ =>
            {
                builds++;
                return new WorkerPromotedParticipants(waiting with { Source = null }, null);
            },
            promotionCompleted: () => completed);

        // The window: not standby by Describe, promotion not completed.
        ControlReply describe = await Handoff(control, 1, Guid.NewGuid(), ControlVerb.Describe, cts.Token);
        Assert.Equal(ControlStatus.Ok, describe.Status);
        Assert.False(Assert.IsType<ControlDescribePayload>(describe.Payload).IsStandby);
        ControlReply freeze = await Handoff(control, 2, Guid.NewGuid(), ControlVerb.Freeze, cts.Token);
        Assert.Equal(ControlStatus.Refused, freeze.Status); // the journal has no such handoff (answered by either participant set); only builds == 0 below proves the attach is deferred
        Assert.Null(control.Transfers);
        Assert.Equal(0, builds);

        // The promotion completes: the next call builds the promoted participants once.
        completed = true;
        ControlReply after = await Handoff(control, 3, Guid.NewGuid(), ControlVerb.Describe, cts.Token);
        Assert.Equal(ControlStatus.Ok, after.Status);
        _ = control.Handoff;
        Assert.Equal(1, builds);
    }

    private static Task<ControlReply> Handoff(WorkerControlService control, ulong id, Guid handoff, ControlVerb verb, CancellationToken token) =>
        control.HandleAsync(new ControlRequest(id, ControlKind.WorldHandoff, handoff, verb, ControlNoArguments.Instance), new GatewayControlContext(1), token);

    private static Dictionary<string, string?> Worker(bool standby) => new()
    {
        ["Cluster:Worker:Enabled"] = "true",
        ["Cluster:Worker:Standby"] = standby ? "true" : "false",
        ["Cluster:Worker:Port"] = ArcaneCore.TestSupport.LoopbackPorts.Next().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Cluster:Worker:LinkSecret"] = StandbyWorkerTests.Secret,
        ["Cluster:Worker:Handoff:Enabled"] = "true",
        ["Cluster:Worker:Transfers:Enabled"] = "true",
    };
}
