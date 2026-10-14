namespace ArcaneCore.Kernel.Cluster.Transfer;

/// <summary>Stable identifier of a game worker node in a clustered realm.</summary>
public readonly record struct WorkerNodeId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator WorkerNodeId(string value) => new(value);
}

/// <summary>Readiness, drain state, and capacity snapshot advertised by a game worker.</summary>
public sealed record ClusterWorkerStatus(
    WorkerNodeId WorkerId,
    Guid Incarnation,
    bool IsReady = true,
    bool IsDraining = false,
    int ActivePlayerCount = 0,
    int MaxPlayerCapacity = 1000,
    int ActiveInstanceCount = 0,
    int MaxInstanceCapacity = 100,
    string ContentRevision = "default")
{
    public bool CanAcceptPlayer =>
        !WorkerId.IsEmpty &&
        IsReady &&
        !IsDraining &&
        (MaxPlayerCapacity <= 0 || ActivePlayerCount < MaxPlayerCapacity);

    public bool CanAcceptInstance =>
        !WorkerId.IsEmpty &&
        IsReady &&
        !IsDraining &&
        (MaxInstanceCapacity <= 0 || ActiveInstanceCount < MaxInstanceCapacity);
}

/// <summary>
/// Resolves `(MapId, InstanceId)` pairs to their owning <see cref="WorkerNodeId"/> while enforcing
/// worker readiness, non-draining status, and capacity limits.
/// </summary>
public interface IClusterMapRouter
{
    /// <summary>
    /// Resolves the authoritative target worker for <paramref name="mapId"/> and <paramref name="instanceId"/>,
    /// verifying that the worker is registered, ready, not draining, and has available player capacity.
    /// </summary>
    bool TryResolveTargetWorker(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason);

    /// <summary>Looks up the current registration and capacity snapshot for <paramref name="workerId"/>.</summary>
    bool TryGetWorker(WorkerNodeId workerId, out ClusterWorkerStatus status);

    /// <summary>Binds a dynamic instance `(mapId, instanceId)` (`instanceId &gt; 0`) to a specific eligible worker.</summary>
    bool TryBindInstance(uint mapId, uint instanceId, WorkerNodeId workerId, out string? failureReason);

    /// <summary>
    /// Resolves an existing instance binding or allocates a new binding on the least-loaded ready worker
    /// with available instance and player capacity.
    /// </summary>
    bool TryAllocateOrResolveInstance(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason);

    /// <summary>Removes an instance binding when an instance closes or expires.</summary>
    bool UnbindInstance(uint mapId, uint instanceId);
}
