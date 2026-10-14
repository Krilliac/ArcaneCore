using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

public sealed class ClusterM151Tests
{
    private sealed class NoOpSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private static ClusterNodeManifest CreateManifest(
        int realmId = 1,
        string nodeId = "worker-east-1",
        Guid? incarnation = null,
        ClusterRole role = ClusterRole.Worker,
        int contractVersion = ClusterNodeManifest.CurrentContractVersion,
        int charactersSchema = 42,
        int worldSchema = 19,
        string contentRevision = "dbc-1.12.1-rev9") =>
        new(
            RealmId: realmId,
            NodeId: nodeId,
            NodeIncarnation: incarnation ?? Guid.NewGuid(),
            Role: role,
            ContractVersion: contractVersion,
            CharactersSchemaVersion: charactersSchema,
            WorldSchemaVersion: worldSchema,
            ContentRevision: contentRevision);

    private static WorldRuntime CreateWorldRuntime(int tickIntervalMs = 5) =>
        new(
            new WorldRuntimeOptions
            {
                TickIntervalMs = tickIntervalMs,
                TickTimer = WorldTickTimer.Legacy,
            },
            new NoOpSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    [Fact]
    public void ClusterNodeManifest_TryAdmit_AcceptsCompatiblePeer_AndRejectsMismatches()
    {
        ClusterNodeManifest worker = CreateManifest(role: ClusterRole.Worker);
        ClusterNodeManifest gateway = CreateManifest(nodeId: "gateway-1", role: ClusterRole.Gateway);

        Assert.True(worker.TryAdmit(gateway, out string? okReason));
        Assert.Null(okReason);

        // Realm mismatch
        ClusterNodeManifest wrongRealm = gateway with { RealmId = 2 };
        Assert.False(worker.TryAdmit(wrongRealm, out string? realmReason));
        Assert.Contains("realm mismatch", realmReason, StringComparison.OrdinalIgnoreCase);

        // Contract version mismatch
        ClusterNodeManifest wrongContract = gateway with { ContractVersion = ClusterNodeManifest.CurrentContractVersion + 1 };
        Assert.False(worker.TryAdmit(wrongContract, out string? contractReason));
        Assert.Contains("contract version mismatch", contractReason, StringComparison.OrdinalIgnoreCase);

        // Characters schema mismatch
        ClusterNodeManifest wrongCharSchema = gateway with { CharactersSchemaVersion = 41 };
        Assert.False(worker.TryAdmit(wrongCharSchema, out string? charSchemaReason));
        Assert.Contains("characters schema mismatch", charSchemaReason, StringComparison.OrdinalIgnoreCase);

        // World schema mismatch
        ClusterNodeManifest wrongWorldSchema = gateway with { WorldSchemaVersion = 20 };
        Assert.False(worker.TryAdmit(wrongWorldSchema, out string? worldSchemaReason));
        Assert.Contains("world schema mismatch", worldSchemaReason, StringComparison.OrdinalIgnoreCase);

        // Content revision mismatch
        ClusterNodeManifest wrongContent = gateway with { ContentRevision = "dbc-1.12.1-rev8" };
        Assert.False(worker.TryAdmit(wrongContent, out string? contentReason));
        Assert.Contains("content revision mismatch", contentReason, StringComparison.OrdinalIgnoreCase);

        // Empty incarnation or node ID
        ClusterNodeManifest emptyIncarnation = gateway with { NodeIncarnation = Guid.Empty };
        Assert.False(worker.TryAdmit(emptyIncarnation, out string? incReason));
        Assert.Contains("NodeIncarnation", incReason, StringComparison.OrdinalIgnoreCase);

        ClusterNodeManifest emptyNodeId = gateway with { NodeId = " " };
        Assert.False(worker.TryAdmit(emptyNodeId, out string? idReason));
        Assert.Contains("NodeId", idReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClusterWorkerReadinessProbe_RequiresRealWorldTickRoundtrip_AndDistinguishesLivenessFromReadiness()
    {
        using WorldRuntime world = CreateWorldRuntime();
        Guid incarnation = Guid.NewGuid();
        ClusterNodeManifest manifest = CreateManifest(incarnation: incarnation);
        var probe = new ClusterWorkerReadinessProbe(manifest, world, initialOwnerEpoch: 3);

        // Before VerifyAndMarkReadyAsync: process is live, but NOT gameplay-ready.
        Assert.Equal(ClusterNodeState.Starting, probe.State);
        Assert.True(probe.IsLive);
        Assert.False(probe.IsGameplayReady);

        // Start VerifyAndMarkReadyAsync BEFORE starting WorldRuntime: it must wait on the world tick roundtrip.
        world.CharacterOwnership = new CharacterOwnershipToken("realm:1", Epoch: 7, Incarnation: incarnation);
        Task readyTask = probe.VerifyAndMarkReadyAsync(requireOwnershipToken: true);
        Assert.False(readyTask.IsCompleted);
        Assert.False(probe.IsGameplayReady);

        // Starting the world thread allows the posted roundtrip command to execute on the world tick.
        world.Start();
        await readyTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClusterNodeState.Active, probe.State);
        Assert.True(probe.IsLive);
        Assert.True(probe.IsGameplayReady);
        Assert.Equal(7, probe.CurrentOwnerEpoch);

        // Graceful drain: immediately drops IsGameplayReady while keeping IsLive true, flushes callback, then reaches Drained.
        bool flushCalled = false;
        await probe.BeginDrainAsync(
            flushCallback: _ =>
            {
                Assert.Equal(ClusterNodeState.Draining, probe.State);
                Assert.True(probe.IsLive);
                Assert.False(probe.IsGameplayReady);
                flushCalled = true;
                return ValueTask.CompletedTask;
            });

        Assert.True(flushCalled);
        Assert.Equal(ClusterNodeState.Drained, probe.State);
        Assert.True(probe.IsLive);
        Assert.False(probe.IsGameplayReady);

        probe.Stop();
        Assert.Equal(ClusterNodeState.Stopped, probe.State);
        Assert.False(probe.IsLive);
        Assert.False(probe.IsGameplayReady);
        world.Stop();
    }

    [Fact]
    public async Task ClusterWorkerReadinessProbe_RejectsMissingOrMismatchedOwnershipTokenWhenFenced()
    {
        using WorldRuntime world = CreateWorldRuntime();
        world.Start();
        try
        {
            ClusterNodeManifest manifest = CreateManifest();
            var probe = new ClusterWorkerReadinessProbe(manifest, world);

            // Missing token when requireOwnershipToken is true
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => probe.VerifyAndMarkReadyAsync(requireOwnershipToken: true));
            Assert.False(probe.IsGameplayReady);

            // Mismatched token incarnation
            world.CharacterOwnership = new CharacterOwnershipToken("realm:1", Epoch: 2, Incarnation: Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => probe.VerifyAndMarkReadyAsync(requireOwnershipToken: true));
            Assert.False(probe.IsGameplayReady);
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task LoopbackGatewayWorkerTransport_StartsReceiveLoopBeforeRegister_AndProxiesOrderedPacketsOnWorldTick()
    {
        using WorldRuntime world = CreateWorldRuntime();
        world.Start();
        try
        {
            ClusterNodeManifest workerManifest = CreateManifest(nodeId: "worker-1", role: ClusterRole.Worker);
            var readiness = new ClusterWorkerReadinessProbe(workerManifest, world, initialOwnerEpoch: 5);
            await readiness.VerifyAndMarkReadyAsync();

            var handledOnWorldThread = new ConcurrentQueue<(ClusterSessionPacket Packet, bool WasWorldThread)>();
            await using var localTransport = new LocalGatewayWorkerTransport(
                world,
                readiness,
                (transport, pkt) =>
                {
                    handledOnWorldThread.Enqueue((pkt, world.IsWorldThread));
                    // Echo two ordered server packets back to the gateway for each client packet.
                    byte[] reply1 = [pkt.Payload.Span[0], 0x10];
                    byte[] reply2 = [pkt.Payload.Span[0], 0x20];
                    Assert.True(transport.TrySendToGateway(pkt.CharacterGuid, pkt.SessionGeneration, pkt.Opcode + 1000, reply1));
                    Assert.True(transport.TrySendToGateway(pkt.CharacterGuid, pkt.SessionGeneration, pkt.Opcode + 2000, reply2));
                });

            await using var host = new LoopbackGatewayWorkerHost(workerManifest, localTransport);
            host.Start();

            ClusterNodeManifest gatewayManifest = CreateManifest(nodeId: "gateway-1", role: ClusterRole.Gateway);
            await using var client = new LoopbackGatewayWorkerClient(gatewayManifest, host.BoundEndPoint);

            var outboundDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedFromWorker = new ConcurrentQueue<ClusterSessionPacket>();
            client.OutboundPacketReceived += pkt =>
            {
                receivedFromWorker.Enqueue(pkt);
                if (receivedFromWorker.Count >= 4)
                {
                    outboundDone.TrySetResult();
                }
            };

            await client.ConnectAndRegisterAsync();
            Assert.True(client.ReceiveLoopStartedBeforeRegisterSent);
            Assert.True(client.IsReceiveLoopRunning);

            // Attach session (generation 1, epoch 5)
            ClusterSessionAttachResult attach = await client.AttachSessionAsync(
                accountId: 10,
                characterGuid: 5001UL,
                requestedGeneration: 1,
                expectedOwnerEpoch: 5);
            Assert.True(attach.Accepted);
            Assert.Equal(1, attach.AssignedGeneration);
            Assert.Equal(5, attach.WorkerOwnerEpoch);

            // Send two ordered client packets (sequence 1, then 2)
            Assert.True(await client.SendToWorkerAsync(
                new ClusterSessionPacket(1, 10, 5001UL, 5, Sequence: 1, Opcode: 0x095, new byte[] { 1, 2, 3 })));
            Assert.True(await client.SendToWorkerAsync(
                new ClusterSessionPacket(1, 10, 5001UL, 5, Sequence: 2, Opcode: 0x096, new byte[] { 4, 5, 6 })));

            // Duplicate/stale sequence 2 must be rejected
            Assert.False(await client.SendToWorkerAsync(
                new ClusterSessionPacket(1, 10, 5001UL, 5, Sequence: 2, Opcode: 0x097, new byte[] { 7 })));

            // Stale owner epoch must be rejected
            Assert.False(await client.SendToWorkerAsync(
                new ClusterSessionPacket(1, 10, 5001UL, 4, Sequence: 3, Opcode: 0x097, new byte[] { 7 })));

            await outboundDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, handledOnWorldThread.Count);
            Assert.All(handledOnWorldThread, item => Assert.True(item.WasWorldThread));

            ClusterSessionPacket[] outbound = [.. receivedFromWorker];
            Assert.Equal(4, outbound.Length);
            Assert.Equal([1L, 2L, 3L, 4L], outbound.Select(p => p.Sequence).ToArray());
            Assert.Equal([1149u, 2149u, 1150u, 2150u], outbound.Select(p => p.Opcode).ToArray());
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task LoopbackGatewayWorkerTransport_SupersedesDuplicateSessionAcrossGateways_AndRejectsIncompatibleGateway()
    {
        using WorldRuntime world = CreateWorldRuntime();
        world.Start();
        try
        {
            ClusterNodeManifest workerManifest = CreateManifest(nodeId: "worker-1", role: ClusterRole.Worker);
            var readiness = new ClusterWorkerReadinessProbe(workerManifest, world, initialOwnerEpoch: 11);
            await readiness.VerifyAndMarkReadyAsync();

            await using var localTransport = new LocalGatewayWorkerTransport(
                world,
                readiness,
                (transport, pkt) =>
                {
                    transport.TrySendToGateway(pkt.CharacterGuid, pkt.SessionGeneration, pkt.Opcode + 1, pkt.Payload);
                });

            await using var host = new LoopbackGatewayWorkerHost(workerManifest, localTransport);
            host.Start();

            // 1. Incompatible gateway (wrong content revision) is deterministically rejected at registration.
            ClusterNodeManifest badGatewayManifest = CreateManifest(
                nodeId: "gateway-bad",
                role: ClusterRole.Gateway,
                contentRevision: "dbc-mismatch");
            await using var badClient = new LoopbackGatewayWorkerClient(badGatewayManifest, host.BoundEndPoint);
            InvalidOperationException regEx = await Assert.ThrowsAsync<InvalidOperationException>(
                () => badClient.ConnectAndRegisterAsync());
            Assert.Contains("content revision mismatch", regEx.Message, StringComparison.OrdinalIgnoreCase);

            // 2. Two valid gateways connect; gateway-A attaches character 9001 at generation 10.
            await using var gatewayA = new LoopbackGatewayWorkerClient(
                CreateManifest(nodeId: "gateway-a", role: ClusterRole.Gateway),
                host.BoundEndPoint);
            await using var gatewayB = new LoopbackGatewayWorkerClient(
                CreateManifest(nodeId: "gateway-b", role: ClusterRole.Gateway),
                host.BoundEndPoint);

            await gatewayA.ConnectAndRegisterAsync();
            await gatewayB.ConnectAndRegisterAsync();

            var gatewayADetached = new TaskCompletionSource<(ulong Guid, long Gen, ClusterSessionDetachReason Reason)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var gatewayAOutbound = new ConcurrentQueue<ClusterSessionPacket>();
            gatewayA.SessionDetached += (guid, gen, reason) => gatewayADetached.TrySetResult((guid, gen, reason));
            gatewayA.OutboundPacketReceived += pkt => gatewayAOutbound.Enqueue(pkt);

            var gatewayBOutboundTcs = new TaskCompletionSource<ClusterSessionPacket>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            gatewayB.OutboundPacketReceived += pkt => gatewayBOutboundTcs.TrySetResult(pkt);

            ClusterSessionAttachResult attachA = await gatewayA.AttachSessionAsync(42, 9001UL, requestedGeneration: 10, expectedOwnerEpoch: 11);
            Assert.True(attachA.Accepted);

            // Stale generation 9 or 10 on gateway-B is rejected.
            ClusterSessionAttachResult staleAttach = await gatewayB.AttachSessionAsync(42, 9001UL, requestedGeneration: 10, expectedOwnerEpoch: 11);
            Assert.False(staleAttach.Accepted);
            Assert.Contains("stale session generation", staleAttach.RejectionReason, StringComparison.OrdinalIgnoreCase);

            // Newer generation 11 on gateway-B supersedes gateway-A's session.
            ClusterSessionAttachResult attachB = await gatewayB.AttachSessionAsync(42, 9001UL, requestedGeneration: 11, expectedOwnerEpoch: 11);
            Assert.True(attachB.Accepted);

            (ulong detachedGuid, long detachedGen, ClusterSessionDetachReason detachReason) =
                await gatewayADetached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(9001UL, detachedGuid);
            Assert.Equal(10L, detachedGen);
            Assert.Equal(ClusterSessionDetachReason.SupersededByNewLogin, detachReason);

            // Late packet from gateway-A with old generation 10 is dropped; packet from gateway-B with generation 11 succeeds.
            Assert.False(await gatewayA.SendToWorkerAsync(
                new ClusterSessionPacket(10, 42, 9001UL, 11, Sequence: 1, Opcode: 50, new byte[] { 0xAA })));
            Assert.True(await gatewayB.SendToWorkerAsync(
                new ClusterSessionPacket(11, 42, 9001UL, 11, Sequence: 1, Opcode: 50, new byte[] { 0xBB })));

            ClusterSessionPacket bReply = await gatewayBOutboundTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(11L, bReply.SessionGeneration);
            Assert.Equal(51u, bReply.Opcode);
            Assert.Equal(new byte[] { 0xBB }, bReply.Payload.ToArray());
            Assert.Empty(gatewayAOutbound);

            // 3. Draining worker refuses new session attaches.
            await readiness.BeginDrainAsync();
            ClusterSessionAttachResult drainAttach = await gatewayB.AttachSessionAsync(99, 9002UL, requestedGeneration: 1, expectedOwnerEpoch: 11);
            Assert.False(drainAttach.Accepted);
            Assert.Contains("not gameplay-ready", drainAttach.RejectionReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            world.Stop();
        }
    }
}
