using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

public sealed class LoopbackRejectDeliveryTests
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
    /// Deterministic regression for the registration-reject race: the host's send loop is held until the
    /// connection's disposal has started, which forces the "dispose begins before the rejection is written"
    /// interleaving. The rejection must still reach the client ahead of EOF.
    /// </summary>
    [Fact]
    public async Task LoopbackHost_DeliversRegistrationRejection_EvenWhenDisposalStartsBeforeAckIsWritten()
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

            var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.DisposeStartedForTest = () => disposeStarted.TrySetResult();
            host.BeforeSendFrameForTest = () => disposeStarted.Task;
            host.Start();

            await using var badClient = new LoopbackGatewayWorkerClient(
                CreateManifest("gateway-bad", ClusterRole.Gateway, "dbc-mismatch"),
                host.BoundEndPoint);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => badClient.ConnectAndRegisterAsync());
            Assert.Contains("content revision mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            world.Stop();
        }
    }
}
