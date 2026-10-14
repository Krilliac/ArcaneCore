using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

public sealed class ClusterMapPlacementTableTests
{
    [Fact]
    public void TryResolveTargetWorker_ResolvesStaticContinentMap_WhenWorkerReadyAndUnderCapacity()
    {
        var table = new ClusterMapPlacementTable();
        var workerA = new WorkerNodeId("worker-kalimdor-1");
        var workerB = new WorkerNodeId("worker-ek-1");

        table.RegisterWorker(workerA, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 120);
        table.RegisterWorker(workerB, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 250);
        table.AssignStaticMap(0, workerB);
        table.AssignStaticMap(1, workerA);

        Assert.True(table.TryResolveTargetWorker(0, 0, out WorkerNodeId resolvedEk, out string? errEk));
        Assert.Null(errEk);
        Assert.Equal(workerB, resolvedEk);

        Assert.True(table.TryResolveTargetWorker(1, 0, out WorkerNodeId resolvedKal, out string? errKal));
        Assert.Null(errKal);
        Assert.Equal(workerA, resolvedKal);
    }

    [Fact]
    public void TryResolveTargetWorker_RejectsUnassignedMap_NotReady_Draining_AndFullCapacity()
    {
        var table = new ClusterMapPlacementTable();
        var worker = new WorkerNodeId("worker-1");
        table.RegisterWorker(worker, isReady: true, maxPlayerCapacity: 10, activePlayerCount: 5);
        table.AssignStaticMap(0, worker);

        // Unassigned map
        Assert.False(table.TryResolveTargetWorker(530, 0, out _, out string? reasonUnassigned));
        Assert.Equal(ClusterMapPlacementTable.FailureMapUnassigned, reasonUnassigned);

        // Worker not ready
        table.SetWorkerReadiness(worker, isReady: false);
        Assert.False(table.TryResolveTargetWorker(0, 0, out _, out string? reasonNotReady));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerNotReady, reasonNotReady);

        // Worker draining
        table.SetWorkerReadiness(worker, isReady: true);
        table.SetWorkerDraining(worker, isDraining: true);
        Assert.False(table.TryResolveTargetWorker(0, 0, out _, out string? reasonDraining));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerDraining, reasonDraining);

        // Worker at player capacity
        table.SetWorkerDraining(worker, isDraining: false);
        table.UpdateWorkerLoad(worker, activePlayerCount: 10);
        Assert.False(table.TryResolveTargetWorker(0, 0, out _, out string? reasonFull));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerAtPlayerCapacity, reasonFull);
    }

    [Fact]
    public void DynamicInstanceBindings_Bind_Allocate_Resolve_AndUnbindCleanly()
    {
        var table = new ClusterMapPlacementTable();
        var worker1 = new WorkerNodeId("worker-dungeon-1");
        var worker2 = new WorkerNodeId("worker-dungeon-2");

        table.RegisterWorker(worker1, isReady: true, maxPlayerCapacity: 100, activePlayerCount: 40, maxInstanceCapacity: 2, activeInstanceCount: 1);
        table.RegisterWorker(worker2, isReady: true, maxPlayerCapacity: 100, activePlayerCount: 10, maxInstanceCapacity: 2, activeInstanceCount: 0);

        // Unbound instance fails TryResolveTargetWorker
        Assert.False(table.TryResolveTargetWorker(36, 101, out _, out string? unboundReason));
        Assert.Equal(ClusterMapPlacementTable.FailureInstanceUnbound, unboundReason);

        // Allocate instance (36, 101) -> picks worker2 (fewer active instances)
        Assert.True(table.TryAllocateOrResolveInstance(36, 101, out WorkerNodeId allocatedWorker, out string? allocErr));
        Assert.Null(allocErr);
        Assert.Equal(worker2, allocatedWorker);

        // Subsequent resolve returns the bound worker
        Assert.True(table.TryResolveTargetWorker(36, 101, out WorkerNodeId resolvedWorker, out _));
        Assert.Equal(worker2, resolvedWorker);

        // Explicit bind to worker1 succeeds while under instance cap, then rejects when instance cap reached
        Assert.True(table.TryBindInstance(43, 102, worker1, out _));
        Assert.False(table.TryBindInstance(43, 103, worker1, out string? capReason));
        Assert.Equal(ClusterMapPlacementTable.FailureWorkerAtInstanceCapacity, capReason);

        // Unbinding frees capacity and removes the route
        Assert.True(table.UnbindInstance(36, 101));
        Assert.False(table.TryResolveTargetWorker(36, 101, out _, out string? afterUnbind));
        Assert.Equal(ClusterMapPlacementTable.FailureInstanceUnbound, afterUnbind);
    }
}
