using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// Authoritative in-memory map and instance placement table mapping `(MapId, InstanceId)` pairs
/// to owning <see cref="WorkerNodeId"/> nodes with readiness, drain, and capacity checks.
/// </summary>
public sealed class ClusterMapPlacementTable : IClusterMapRouter
{
    public const string FailureWorkerUnknown = "worker-unknown";
    public const string FailureWorkerNotReady = "worker-not-ready";
    public const string FailureWorkerDraining = "worker-draining";
    public const string FailureWorkerAtPlayerCapacity = "worker-at-player-capacity";
    public const string FailureWorkerAtInstanceCapacity = "worker-at-instance-capacity";
    public const string FailureMapUnassigned = "map-unassigned";
    public const string FailureInstanceUnbound = "instance-unbound";
    public const string FailureInvalidInstanceId = "invalid-instance-id";
    public const string FailureNoEligibleWorker = "no-eligible-worker";

    private readonly object _sync = new();
    private readonly Dictionary<WorkerNodeId, ClusterWorkerStatus> _workers = [];
    private readonly Dictionary<uint, WorkerNodeId> _staticMaps = [];
    private readonly Dictionary<(uint MapId, uint InstanceId), WorkerNodeId> _instanceBindings = [];

    /// <summary>Registers or updates a worker node's status and capacity snapshot.</summary>
    public void RegisterOrUpdateWorker(ClusterWorkerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.WorkerId.IsEmpty)
            throw new ArgumentException("WorkerId must not be empty.", nameof(status));

        lock (_sync)
        {
            _workers[status.WorkerId] = status;
        }
    }

    /// <summary>Convenience registration overload for a worker node.</summary>
    public void RegisterWorker(
        WorkerNodeId workerId,
        bool isReady = true,
        int maxPlayerCapacity = 1000,
        int activePlayerCount = 0,
        int maxInstanceCapacity = 100,
        int activeInstanceCount = 0,
        bool isDraining = false,
        Guid incarnation = default)
    {
        if (incarnation == Guid.Empty)
            incarnation = Guid.NewGuid();

        RegisterOrUpdateWorker(new ClusterWorkerStatus(
            workerId,
            incarnation,
            IsReady: isReady,
            IsDraining: isDraining,
            ActivePlayerCount: activePlayerCount,
            MaxPlayerCapacity: maxPlayerCapacity,
            ActiveInstanceCount: activeInstanceCount,
            MaxInstanceCapacity: maxInstanceCapacity));
    }

    /// <summary>Assigns a static continent or non-instanced world map (`instanceId == 0`) to <paramref name="workerId"/>.</summary>
    public void AssignStaticMap(uint mapId, WorkerNodeId workerId)
    {
        if (workerId.IsEmpty)
            throw new ArgumentException("WorkerId must not be empty.", nameof(workerId));

        lock (_sync)
        {
            _staticMaps[mapId] = workerId;
        }
    }

    /// <summary>Updates worker readiness without altering capacity counters.</summary>
    public bool SetWorkerReadiness(WorkerNodeId workerId, bool isReady)
    {
        lock (_sync)
        {
            if (!_workers.TryGetValue(workerId, out ClusterWorkerStatus? current))
                return false;

            _workers[workerId] = current with { IsReady = isReady };
            return true;
        }
    }

    /// <summary>Updates worker drain state without altering capacity counters.</summary>
    public bool SetWorkerDraining(WorkerNodeId workerId, bool isDraining)
    {
        lock (_sync)
        {
            if (!_workers.TryGetValue(workerId, out ClusterWorkerStatus? current))
                return false;

            _workers[workerId] = current with { IsDraining = isDraining };
            return true;
        }
    }

    /// <summary>Updates active player and instance counts for <paramref name="workerId"/>.</summary>
    public bool UpdateWorkerLoad(WorkerNodeId workerId, int activePlayerCount, int? activeInstanceCount = null)
    {
        lock (_sync)
        {
            if (!_workers.TryGetValue(workerId, out ClusterWorkerStatus? current))
                return false;

            _workers[workerId] = current with
            {
                ActivePlayerCount = Math.Max(0, activePlayerCount),
                ActiveInstanceCount = activeInstanceCount.HasValue
                    ? Math.Max(0, activeInstanceCount.Value)
                    : current.ActiveInstanceCount,
            };
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetWorker(WorkerNodeId workerId, out ClusterWorkerStatus status)
    {
        lock (_sync)
        {
            if (_workers.TryGetValue(workerId, out ClusterWorkerStatus? found))
            {
                status = found;
                return true;
            }

            status = default!;
            return false;
        }
    }

    /// <summary>Looks up the static map owner for <paramref name="mapId"/> without readiness validation.</summary>
    public bool TryGetStaticMapOwner(uint mapId, out WorkerNodeId workerId)
    {
        lock (_sync)
        {
            return _staticMaps.TryGetValue(mapId, out workerId);
        }
    }

    /// <summary>Looks up the bound worker for `(mapId, instanceId)` without readiness validation.</summary>
    public bool TryGetInstanceBinding(uint mapId, uint instanceId, out WorkerNodeId workerId)
    {
        lock (_sync)
        {
            return _instanceBindings.TryGetValue((mapId, instanceId), out workerId);
        }
    }

    /// <summary>Returns a point-in-time snapshot of all registered workers ordered by worker ID.</summary>
    public IReadOnlyList<ClusterWorkerStatus> SnapshotWorkers()
    {
        lock (_sync)
        {
            return [.. _workers.Values.OrderBy(w => w.WorkerId.Value, StringComparer.Ordinal)];
        }
    }

    /// <summary>Returns a point-in-time snapshot of all static map assignments ordered by map ID.</summary>
    public IReadOnlyList<(uint MapId, WorkerNodeId WorkerId)> SnapshotStaticMaps()
    {
        lock (_sync)
        {
            return [.. _staticMaps.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value))];
        }
    }

    /// <summary>Returns a point-in-time snapshot of all active instance bindings ordered by map ID and instance ID.</summary>
    public IReadOnlyList<(uint MapId, uint InstanceId, WorkerNodeId WorkerId)> SnapshotInstanceBindings()
    {
        lock (_sync)
        {
            return [.. _instanceBindings.OrderBy(kv => kv.Key.MapId).ThenBy(kv => kv.Key.InstanceId).Select(kv => (kv.Key.MapId, kv.Key.InstanceId, kv.Value))];
        }
    }

    /// <inheritdoc />
    public bool TryBindInstance(uint mapId, uint instanceId, WorkerNodeId workerId, out string? failureReason)
    {
        if (instanceId == 0)
        {
            failureReason = FailureInvalidInstanceId;
            return false;
        }

        lock (_sync)
        {
            if (!ValidateWorkerForNewInstanceLocked(workerId, out failureReason))
                return false;

            var key = (mapId, instanceId);
            bool isNewBinding = !_instanceBindings.TryGetValue(key, out WorkerNodeId previousWorker) || previousWorker != workerId;
            _instanceBindings[key] = workerId;

            if (isNewBinding && _workers.TryGetValue(workerId, out ClusterWorkerStatus? current))
            {
                _workers[workerId] = current with { ActiveInstanceCount = current.ActiveInstanceCount + 1 };
            }

            failureReason = null;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryAllocateOrResolveInstance(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason)
    {
        if (instanceId == 0)
        {
            return TryResolveTargetWorker(mapId, 0, out workerId, out failureReason);
        }

        lock (_sync)
        {
            var key = (mapId, instanceId);
            if (_instanceBindings.TryGetValue(key, out WorkerNodeId existingWorker))
            {
                if (ValidateWorkerForPlayerAdmissionLocked(existingWorker, out failureReason))
                {
                    workerId = existingWorker;
                    return true;
                }

                workerId = default;
                return false;
            }

            // Prefer the static map owner if one is assigned and has both instance and player capacity;
            // otherwise select the ready non-draining worker with the lowest player load ratio.
            WorkerNodeId chosen = default;
            if (_staticMaps.TryGetValue(mapId, out WorkerNodeId staticOwner) &&
                ValidateWorkerForNewInstanceLocked(staticOwner, out _))
            {
                chosen = staticOwner;
            }
            else
            {
                ClusterWorkerStatus? best = _workers.Values
                    .Where(w => w.CanAcceptInstance && w.CanAcceptPlayer)
                    .OrderBy(w => w.ActiveInstanceCount)
                    .ThenBy(w => w.ActivePlayerCount)
                    .ThenBy(w => w.WorkerId.Value, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (best is not null)
                    chosen = best.WorkerId;
            }

            if (chosen.IsEmpty)
            {
                workerId = default;
                failureReason = FailureNoEligibleWorker;
                return false;
            }

            _instanceBindings[key] = chosen;
            ClusterWorkerStatus current = _workers[chosen];
            _workers[chosen] = current with { ActiveInstanceCount = current.ActiveInstanceCount + 1 };
            workerId = chosen;
            failureReason = null;
            return true;
        }
    }

    /// <inheritdoc />
    public bool UnbindInstance(uint mapId, uint instanceId)
    {
        lock (_sync)
        {
            var key = (mapId, instanceId);
            if (!_instanceBindings.Remove(key, out WorkerNodeId boundWorker))
                return false;

            if (_workers.TryGetValue(boundWorker, out ClusterWorkerStatus? current) && current.ActiveInstanceCount > 0)
            {
                _workers[boundWorker] = current with { ActiveInstanceCount = current.ActiveInstanceCount - 1 };
            }

            return true;
        }
    }

    /// <inheritdoc />
    public bool TryResolveTargetWorker(uint mapId, uint instanceId, out WorkerNodeId workerId, out string? failureReason)
    {
        lock (_sync)
        {
            WorkerNodeId candidate;
            if (instanceId > 0)
            {
                if (!_instanceBindings.TryGetValue((mapId, instanceId), out candidate))
                {
                    workerId = default;
                    failureReason = FailureInstanceUnbound;
                    return false;
                }
            }
            else
            {
                if (!_staticMaps.TryGetValue(mapId, out candidate))
                {
                    workerId = default;
                    failureReason = FailureMapUnassigned;
                    return false;
                }
            }

            if (!ValidateWorkerForPlayerAdmissionLocked(candidate, out failureReason))
            {
                workerId = default;
                return false;
            }

            workerId = candidate;
            failureReason = null;
            return true;
        }
    }

    private bool ValidateWorkerForPlayerAdmissionLocked(WorkerNodeId workerId, out string? failureReason)
    {
        if (workerId.IsEmpty || !_workers.TryGetValue(workerId, out ClusterWorkerStatus? status))
        {
            failureReason = FailureWorkerUnknown;
            return false;
        }

        if (!status.IsReady)
        {
            failureReason = FailureWorkerNotReady;
            return false;
        }

        if (status.IsDraining)
        {
            failureReason = FailureWorkerDraining;
            return false;
        }

        if (status.MaxPlayerCapacity > 0 && status.ActivePlayerCount >= status.MaxPlayerCapacity)
        {
            failureReason = FailureWorkerAtPlayerCapacity;
            return false;
        }

        failureReason = null;
        return true;
    }

    private bool ValidateWorkerForNewInstanceLocked(WorkerNodeId workerId, out string? failureReason)
    {
        if (!ValidateWorkerForPlayerAdmissionLocked(workerId, out failureReason))
            return false;

        ClusterWorkerStatus status = _workers[workerId];
        if (status.MaxInstanceCapacity > 0 && status.ActiveInstanceCount >= status.MaxInstanceCapacity)
        {
            failureReason = FailureWorkerAtInstanceCapacity;
            return false;
        }

        failureReason = null;
        return true;
    }
}
