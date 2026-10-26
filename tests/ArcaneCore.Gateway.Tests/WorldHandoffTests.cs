using System.Buffers.Binary;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.Gateway.Tests.StandbyWorkerTests;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Tier 3b drain protocol (docs/design/hot-reload-tiers.md section 5.3), in process: two worlds on one set of databases (A active, B standby),
/// a real gateway, the mock client, and the durable handoff journal. Every pre-commit stage aborts back to A; the epoch takeover is the point of
/// no return, after which a crashed handoff rolls forward.
/// </summary>
public sealed class WorldHandoffTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);

    [Fact]
    public async Task AFullHandoff_MovesTheOwnershipAndThePlayers_ToTheStandby_ThroughTheGateway()
    {
        using var cts = new CancellationTokenSource(Deadline);
        SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        Exception? stopFailure = null;
        try
        {
            await RunFullHandoffAsync(a, cts);
        }
        finally
        {
            try
            {
                await a.DisposeAsync();
            }
            catch (Exception ex)
            {
                stopFailure = ex;
            }
        }

        // The retired A logs its player out as it stops: every write it still attempts is refused by the characters fence (epoch N), nothing else fails.
        if (stopFailure is not null)
        {
            List<Exception> leaves = Leaves(stopFailure).ToList();
            Assert.All(leaves, leaf => Assert.IsType<CharacterOwnershipRejectedException>(leaf));
        }
    }

    private static IEnumerable<Exception> Leaves(Exception error)
    {
        IEnumerable<Exception> inner = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } one ? [one] : [];
        bool any = false;
        foreach (Exception child in inner)
        {
            any = true;
            foreach (Exception leaf in Leaves(child))
                yield return leaf;
        }

        if (!any)
            yield return error;
    }

    private static async Task RunFullHandoffAsync(SyntheticArcaneServer a, CancellationTokenSource cts)
    {
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        await using Linked linked = await LinkAsync(a, cts.Token);
        GatewayWorkerEndpoint bEndpoint = b.Services.GetRequiredService<GatewayWorkerEndpoint>();
        await bEndpoint.StartAsync(cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        try
        {
            // A player in the world on A, through the gateway.
            await a.AddAccountAsync("HANDOFF", "PASSWORD", cts.Token);
            LogonResult logon = await LogonClient.AuthenticateAsync(a.RealmEndpoint, "HANDOFF", "PASSWORD", cts.Token);
            WorldClient first = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Ok, await first.AuthenticateAsync("HANDOFF", logon.SessionKey, cts.Token));
            ulong guid = await CreateAndEnterAsync(first, a, "Handoff", cts.Token);
            CharacterOwnershipToken before = a.World.CharacterOwnership!;

            var stages = new List<WorldHandoffStage>();
            WorldHandoffRecord done = await Coordinator(a, b, source, new GatewayWorldHandoffRouter(linked.Gateway, bEndpoint.Host!.BoundEndPoint))
                .RunAsync(Guid.NewGuid(), (stage, _) => { stages.Add(stage); return Task.CompletedTask; }, cts.Token);

            Assert.Equal(
                [WorldHandoffStage.Begun, WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen,
                 WorldHandoffStage.Flushed, WorldHandoffStage.EpochTakeover, WorldHandoffStage.Rerouted, WorldHandoffStage.Retired],
                stages);
            Assert.Equal(WorldHandoffStage.Retired, done.Stage);
            Assert.Equal(PlayerTransferDecisionState.Completed, done.DecisionState);
            Assert.Equal(new CharacterOwnershipToken("world", before.Epoch + 1, Host(b).Incarnation), done.TargetOwnershipToken);
            Assert.Equal(done.TargetOwnershipToken, b.World.CharacterOwnership);
            Assert.False(Host(b).IsStandby);
            Assert.True(source.IsFrozen); // A stays parked: it never ticks again
            Assert.Equal(LocalWorldHandoffSource.RetiredReason, a.Services.GetRequiredService<WorkerAdmission>().ClosedReason);
            Assert.Equal(bEndpoint.Host.BoundEndPoint, linked.Gateway.WorkerEndPoint);

            // The client on A was disconnected by the reroute; it logs in again through the same gateway and lands on B, which sees the character.
            await first.DisposeAsync();
            await using WorldClient again = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Ok, await again.AuthenticateAsync("HANDOFF", logon.SessionKey, cts.Token));
            await WaitUntilAsync(() => Task.FromResult(bEndpoint.Acceptor!.Sessions.Count == 1), cts.Token); // tracked at Start, after the accept reply
            Assert.Single(bEndpoint.Acceptor!.Sessions);
            await again.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
            WorldFrame list = await again.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);
            Assert.Equal(1, list.Payload[0]);
            Assert.Equal(guid, BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));
            byte[] login = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(login, guid);
            await again.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, cts.Token);
            await again.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, cts.Token);
            await WaitUntilAsync(async () => await b.World.InvokeAsync(() => b.World.OnlinePlayerCount) == 1, cts.Token);

            // The durable fence row names B now.
            CharacterOwnershipRow row = await OwnershipAsync(b);
            Assert.Equal(before.Epoch + 1, row.Epoch);
            Assert.Equal(Host(b).Incarnation.ToString("D"), row.OwnerIncarnation);
        }
        finally
        {
            source.Dispose(); // release A's parked simulation thread before its host stops
            await bEndpoint.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(WorldHandoffStage.Begun)]
    [InlineData(WorldHandoffStage.Warm)]
    [InlineData(WorldHandoffStage.AdmissionsStopped)]
    [InlineData(WorldHandoffStage.Frozen)]
    [InlineData(WorldHandoffStage.Flushed)]
    public async Task AFailureBeforeTheEpochTakeover_Aborts_AndAResumesUnderItsOwnership(WorldHandoffStage failAt)
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;
        var router = new RecordingRouter();
        Guid handoffId = Guid.NewGuid();

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            Coordinator(a, b, source, router).RunAsync(handoffId, (stage, _) =>
                stage == failAt ? throw new InvalidOperationException("injected failure at " + stage) : Task.CompletedTask, cts.Token));

        Assert.Equal(WorldHandoffStage.Aborted, aborted.Record.Stage);
        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Equal("injected failure at " + failAt, aborted.Record.AbortReason);
        Assert.Equal(aborted.Record, await Store(a).GetAsync(handoffId, cts.Token));
        AssertAResumed(a, b, source, before, router);
        Assert.Equal(before.Epoch, (await OwnershipAsync(a)).Epoch);

        // The journal is free again: the next handoff runs.
        WorldHandoffRecord done = await Coordinator(a, b, source, router).RunAsync(Guid.NewGuid(), cancellationToken: cts.Token);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public async Task ASourceThatLostItsOwnership_AbortsAtTheTakeover_AndChangesNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;
        var router = new RecordingRouter();

        // Someone else took the characters over while A was draining (after the flush, before the takeover).
        Guid intruder = Guid.NewGuid();
        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            Coordinator(a, b, source, router).RunAsync(Guid.NewGuid(), async (stage, ct) =>
            {
                if (stage == WorldHandoffStage.Flushed)
                {
                    await using AsyncServiceScope scope = a.Services.CreateAsyncScope();
                    Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                        .TryTakeoverAsync(before, "intruder", intruder, ct));
                }
            }, cts.Token));

        Assert.Equal(WorldHandoffStage.Aborted, aborted.Record.Stage);
        Assert.Contains("no longer holds", aborted.Record.AbortReason);
        Assert.True(Host(b).IsStandby);
        Assert.Equal(0, router.Calls);
        CharacterOwnershipRow row = await OwnershipAsync(a);
        Assert.Equal(intruder.ToString("D"), row.OwnerIncarnation); // the takeover did not overwrite the intruder
        Assert.Equal(before.Epoch + 1, row.Epoch);
    }

    [Fact]
    public async Task ACrashAfterTheEpochTakeover_RollsForwardOnRecovery()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;
        var router = new RecordingRouter();
        Guid handoffId = Guid.NewGuid();

        // The coordinator dies right after the point of no return: nothing aborts, B is not promoted yet.
        await Assert.ThrowsAsync<CoordinatorCrash>(() => Coordinator(a, b, source, router).RunAsync(handoffId, (stage, _) =>
            stage == WorldHandoffStage.EpochTakeover ? throw new CoordinatorCrash() : Task.CompletedTask, cts.Token));
        WorldHandoffRecord committed = (await Store(a).GetAsync(handoffId, cts.Token))!;
        Assert.Equal(WorldHandoffStage.EpochTakeover, committed.Stage);
        Assert.Equal(PlayerTransferDecisionState.Committed, committed.DecisionState);
        Assert.True(Host(b).IsStandby);
        Assert.Contains(committed, await Store(a).GetUnresolvedAsync(cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(a).AbortAsync(handoffId, "too late", cts.Token)); // past the point of no return
        Assert.True(source.IsFrozen);

        // A fresh coordinator recovers from the journal: roll forward only.
        WorldHandoffRecord done = await Coordinator(a, b, source, router).RecoverAsync(handoffId, cts.Token);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, done.DecisionState);
        Assert.False(Host(b).IsStandby);
        Assert.Equal(new CharacterOwnershipToken("world", before.Epoch + 1, Host(b).Incarnation), b.World.CharacterOwnership);
        Assert.Equal(1, router.Calls);
        Assert.Empty(await Store(a).GetUnresolvedAsync(cts.Token));
        Assert.Equal(done, await Coordinator(a, b, source, router).RecoverAsync(handoffId, cts.Token)); // resolved: unchanged
    }

    [Fact]
    public async Task ACrashBeforeTheEpochTakeover_AbortsOnRecovery_AndResumesA()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;
        var router = new RecordingRouter();
        Guid handoffId = Guid.NewGuid();

        // The coordinator died with A frozen and flushed (its abort never ran).
        IWorldHandoffStore store = Store(a);
        await store.BeginAsync(handoffId, source.WorkerId, new WorkerNodeId("b"), before, cts.Token);
        foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
        {
            if (stage == WorldHandoffStage.AdmissionsStopped) await source.StopAdmissionsAsync(cts.Token);
            if (stage == WorldHandoffStage.Frozen) await source.FreezeAsync(cts.Token);
            if (stage == WorldHandoffStage.Flushed) await source.FlushAsync(cts.Token);
            await store.AdvancePreCommitAsync(handoffId, stage, cts.Token);
        }

        Assert.True(source.IsFrozen);
        WorldHandoffRecord recovered = await Coordinator(a, b, source, router).RecoverAsync(handoffId, cts.Token);
        Assert.Equal(WorldHandoffStage.Aborted, recovered.Stage);
        AssertAResumed(a, b, source, before, router);
    }

    [Fact]
    public async Task AnAbortWhoseJournalWriteFails_StillResumesA_AndRecoveryAbortsTheHandoff()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer a = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer b = await StartStandbyAsync(a, cts.Token);
        using LocalWorldHandoffSource source = Source(a);
        CharacterOwnershipToken before = a.World.CharacterOwnership!;
        var router = new RecordingRouter();
        Guid handoffId = Guid.NewGuid();
        var flaky = new AbortFailsOnceStore(Store(a));

        // A fails after the freeze, and the journal write of the abort fails too (a transient database error): A must not stay parked.
        await Assert.ThrowsAsync<TransientJournalFailure>(() =>
            new WorldHandoffCoordinator(flaky, source, new StandbyWorldHandoffTarget(new WorkerNodeId("b"), Host(b)), router)
                .RunAsync(handoffId, (stage, _) => stage == WorldHandoffStage.Flushed ? throw new InvalidOperationException("injected") : Task.CompletedTask, cts.Token));
        AssertAResumed(a, b, source, before, router);
        WorldHandoffRecord open = Assert.Single(await Store(a).GetUnresolvedAsync(cts.Token));
        Assert.Equal(WorldHandoffStage.Flushed, open.Stage); // still pre-commit: a recovery aborts it

        WorldHandoffRecord recovered = await Coordinator(a, b, source, router).RecoverAsync(handoffId, cts.Token);
        Assert.Equal(WorldHandoffStage.Aborted, recovered.Stage);
        AssertAResumed(a, b, source, before, router);
        Assert.Empty(await Store(a).GetUnresolvedAsync(cts.Token));
    }

    private static void AssertAResumed(SyntheticArcaneServer a, SyntheticArcaneServer b, LocalWorldHandoffSource source, CharacterOwnershipToken before, RecordingRouter router)
    {
        Assert.False(source.IsFrozen);
        Assert.True(a.Services.GetRequiredService<WorkerAdmission>().IsOpen);
        Assert.Equal(before, a.World.CharacterOwnership);
        Assert.True(a.World.InvokeAsync(() => true).Wait(TimeSpan.FromSeconds(10))); // A ticks again
        Assert.True(Host(b).IsStandby);
        Assert.Null(b.World.CharacterOwnership);
        Assert.Equal(0, router.Calls);
    }

    private static IWorldHandoffStore Store(SyntheticArcaneServer server) => server.Services.GetRequiredService<IWorldHandoffStore>();

    private static LocalWorldHandoffSource Source(SyntheticArcaneServer a) => new(
        new WorkerNodeId("a"), a.World, a.Services.GetRequiredService<CharacterSaveQueue>(), a.Services.GetRequiredService<WorkerAdmission>(),
        a.Services.GetRequiredService<CharacterPersistenceBarrier>());

    private static WorldHandoffCoordinator Coordinator(SyntheticArcaneServer a, SyntheticArcaneServer b, IWorldHandoffSource source, IWorldHandoffRouter router)
        => new(Store(a), source, new StandbyWorldHandoffTarget(new WorkerNodeId("b"), Host(b)), router);

    private static async Task<ulong> CreateAndEnterAsync(WorldClient client, SyntheticArcaneServer server, string name, CancellationToken token)
    {
        await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate(name), token);
        Assert.Equal(new byte[] { 0x2E }, (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token)).Payload);
        await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
        WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8));
        byte[] login = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(login, guid);
        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
        await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
        await WaitUntilAsync(async () => await server.World.InvokeAsync(() => server.World.OnlinePlayerCount) == 1, token);
        return guid;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }

    private sealed class CoordinatorCrash : Exception;

    private sealed class TransientJournalFailure : Exception;

    private sealed class AbortFailsOnceStore(IWorldHandoffStore inner) : IWorldHandoffStore
    {
        private int _failed;

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _failed, 1) == 0 ? throw new TransientJournalFailure() : inner.AbortAsync(handoffId, reason, cancellationToken);

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default)
            => inner.BeginAsync(handoffId, sourceWorkerId, targetWorkerId, sourceOwnershipToken, cancellationToken);
        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
            => inner.AdvancePreCommitAsync(handoffId, nextStage, cancellationToken);
        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
            => inner.CommitEpochTakeoverAsync(handoffId, targetOwnerNode, targetIncarnation, cancellationToken);
        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.MarkReroutedAsync(handoffId, cancellationToken);
        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.MarkRetiredAsync(handoffId, cancellationToken);
        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.GetAsync(handoffId, cancellationToken);
        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default) => inner.GetUnresolvedAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default) => inner.GetTransferIdsAsync(handoffId, cancellationToken);
        public ITransferDecisionStore ForHandoff(Guid handoffId) => inner.ForHandoff(handoffId);
    }

    private sealed class RecordingRouter : IWorldHandoffRouter
    {
        public int Calls;

        public Task RerouteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }
}
