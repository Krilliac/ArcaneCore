using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

public sealed class LoopbackReceiveLoopDisposeTests
{
    private static ClusterNodeManifest CreateManifest(string nodeId, ClusterRole role, string contentRevision) =>
        new(
            RealmId: 1,
            NodeId: nodeId,
            NodeIncarnation: Guid.NewGuid(),
            Role: role,
            ContractVersion: ClusterNodeManifest.CurrentContractVersion,
            CharactersSchemaVersion: 42,
            WorldSchemaVersion: 19,
            ContentRevision: contentRevision);

    /// <summary>
    /// A connection that ends through the receive loop (here: a rejected registration) is disposed from that
    /// loop's own <c>finally</c>. Disposal must run to completion (socket disposed) instead of awaiting the
    /// receive task it is running inside. The 30 s wait is only a failure bound for the hang being fixed.
    /// </summary>
    [Fact]
    public async Task LoopbackHost_CompletesConnectionDisposal_WhenConnectionEndsViaReceiveLoop()
    {
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, TickTimer = WorldTickTimer.Legacy },
            new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        world.Start();
        try
        {
            ClusterNodeManifest workerManifest = CreateManifest("worker-1", ClusterRole.Worker, "dbc-1.12.1-rev9");
            var readiness = new ClusterWorkerReadinessProbe(workerManifest, world, initialOwnerEpoch: 11);
            await readiness.VerifyAndMarkReadyAsync();

            await using var localTransport = new LocalGatewayWorkerTransport(world, readiness, (_, _) => { });
            await using var host = new LoopbackGatewayWorkerHost(workerManifest, localTransport);

            var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.DisposeCompletedForTest = () => disposeCompleted.TrySetResult();
            host.Start();

            await using var badClient = new LoopbackGatewayWorkerClient(
                CreateManifest("gateway-bad", ClusterRole.Gateway, "dbc-mismatch"),
                host.BoundEndPoint);
            await Assert.ThrowsAsync<InvalidOperationException>(() => badClient.ConnectAndRegisterAsync());

            await disposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            world.Stop();
        }
    }
}
