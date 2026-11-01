using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;

namespace ArcaneCore.Gateway.Transfers;

/// <summary>One worker as the gateway sees it right now: whether its link is registered and the latest <c>WorkerStatus</c> report that arrived on it (null before the first one).</summary>
internal readonly record struct GatewayWorkerView(bool LinkUp, WorkerStatusReport? Report);

/// <summary>
/// The gateway's <see cref="IClusterMapRouter"/> (design _wf/handoff-control-DESIGN.md section 4.1): where a map copy lives and whether that worker can take one more player.
/// <list type="bullet">
/// <item><description>Placement is the static ownership of <see cref="GatewayRouteTable"/> (<see cref="GatewayRouteTable.TryResolve"/>: the maps each worker announced, the configured list as the fallback).
/// A worker whose link is down owns nothing there.</description></item>
/// <item><description>Readiness and load come from the latest <c>WorkerStatus</c> report of the worker's link: a standby process, or one that is restarting or shutting down, is not a target; a worker at its player limit
/// (<c>PlayerLimit</c> 0 means none) is full. A link with no report yet counts as ready and empty: the target's own <c>Reserve</c> is the final word.</description></item>
/// <item><description>Dynamic instance ids (question Q8, orchestrator decision): the gateway allocates and binds nothing. The target worker allocates the id at <c>Reserve</c> when the request names instance 0 for an instanced map, so
/// <see cref="TryAllocateOrResolveInstance"/> resolves to the worker that owns the map, <see cref="TryBindInstance"/> only confirms that the worker is that owner and <see cref="UnbindInstance"/> has nothing to remove.</description></item>
/// </list>
/// Thread safety: stateless apart from its collaborators, which are thread-safe. Nothing is allocated except the status snapshot of <see cref="TryGetWorker"/>.
/// </summary>
internal sealed class GatewayClusterMapRouter : IClusterMapRouter
{
    private readonly Func<GatewayRouteTable> _routes;
    private readonly Func<string, GatewayWorkerView?> _workers;

    /// <param name="routes">The gateway's current map-to-worker table (asked on every call: the server replaces its table when it starts, after the hosted services were built).</param>
    /// <param name="workers">The worker with the given node id as the gateway sees it now, or null for a node that is not configured.</param>
    public GatewayClusterMapRouter(Func<GatewayRouteTable> routes, Func<string, GatewayWorkerView?> workers)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(workers);
        _routes = routes;
        _workers = workers;
    }

    /// <inheritdoc />
    public bool TryResolveTargetWorker(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason)
    {
        if (!_routes().TryResolve(mapId, instanceId, out string node))
        {
            workerId = default;
            failureReason = ClusterMapPlacementTable.FailureMapUnassigned;
            return false;
        }

        if (!ValidateAdmission(node, out failureReason))
        {
            workerId = default;
            return false;
        }

        workerId = new WorkerNodeId(node);
        failureReason = null;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetWorker(WorkerNodeId workerId, out ClusterWorkerStatus status)
    {
        if (workerId.IsEmpty || _workers(workerId.Value) is not { } view)
        {
            status = default!;
            return false;
        }

        status = Describe(workerId, view);
        return true;
    }

    /// <inheritdoc />
    public bool TryBindInstance(uint mapId, uint instanceId, WorkerNodeId workerId, out string? failureReason)
    {
        if (instanceId == 0)
        {
            failureReason = ClusterMapPlacementTable.FailureInvalidInstanceId;
            return false;
        }

        if (workerId.IsEmpty || !_routes().TryResolve(mapId, instanceId, out string owner) || !string.Equals(owner, workerId.Value, StringComparison.Ordinal))
        {
            failureReason = ClusterMapPlacementTable.FailureNoEligibleWorker;
            return false;
        }

        return ValidateAdmission(owner, out failureReason);
    }

    /// <inheritdoc />
    public bool TryAllocateOrResolveInstance(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason) =>
        TryResolveTargetWorker(mapId, instanceId, out workerId, out failureReason);

    /// <inheritdoc />
    public bool UnbindInstance(uint mapId, uint instanceId) => false;

    private bool ValidateAdmission(string node, out string? failureReason)
    {
        if (_workers(node) is not { } view)
        {
            failureReason = ClusterMapPlacementTable.FailureWorkerUnknown;
            return false;
        }

        ClusterWorkerStatus status = Describe(new WorkerNodeId(node), view);
        if (!status.IsReady)
        {
            failureReason = ClusterMapPlacementTable.FailureWorkerNotReady;
            return false;
        }

        if (status.IsDraining)
        {
            failureReason = ClusterMapPlacementTable.FailureWorkerDraining;
            return false;
        }

        if (!status.CanAcceptPlayer)
        {
            failureReason = ClusterMapPlacementTable.FailureWorkerAtPlayerCapacity;
            return false;
        }

        failureReason = null;
        return true;
    }

    private static ClusterWorkerStatus Describe(WorkerNodeId workerId, GatewayWorkerView view)
    {
        WorkerStatusReport? report = view.Report;
        bool standby = report is { Standby: true };
        bool draining = report is { Restarting: true } or { ShuttingDown: true };
        // The gateway does not track instance capacity (the target allocates instances): 0 means unlimited.
        return new ClusterWorkerStatus(
            workerId,
            Guid.Empty,
            IsReady: view.LinkUp && !standby,
            IsDraining: draining,
            ActivePlayerCount: report?.Sessions ?? 0,
            MaxPlayerCapacity: report?.PlayerLimit ?? 0,
            ActiveInstanceCount: 0,
            MaxInstanceCapacity: 0);
    }
}
