using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Explicit membership and drain lifecycle state of a cluster node (<c>docs/CLUSTERING_DESIGN.md</c> §3.2).
/// </summary>
public enum ClusterNodeState
{
    Starting = 0,
    Active = 1,
    Draining = 2,
    Drained = 3,
    Stopped = 4,
}

/// <summary>
/// Distinguishes process liveness (heartbeat) from gameplay readiness (admission of new sessions/maps)
/// and drives the graceful drain state machine (<c>Starting → Active → Draining → Drained → Stopped</c>).
/// </summary>
public interface IClusterNodeLifecycle
{
    ClusterNodeManifest Manifest { get; }

    ClusterNodeState State { get; }

    /// <summary>True while the node process is alive (Starting, Active, Draining, or Drained); false once Stopped.</summary>
    bool IsLive { get; }

    /// <summary>
    /// True only when <see cref="State"/> is <see cref="ClusterNodeState.Active"/> after schema/content/lease
    /// verification AND a real command roundtrip through the running <see cref="WorldRuntime"/> simulation tick.
    /// </summary>
    bool IsGameplayReady { get; }

    /// <summary>Active durable ownership epoch for this worker (or 1 when unfenced single-node).</summary>
    long CurrentOwnerEpoch { get; }

    Task VerifyAndMarkReadyAsync(
        ClusterNodeManifest? expectedClusterManifest = null,
        bool requireOwnershipToken = false,
        bool featureQueuesStarted = true,
        CancellationToken cancellationToken = default);

    Task BeginDrainAsync(
        Func<CancellationToken, ValueTask>? flushCallback = null,
        CancellationToken cancellationToken = default);

    void Stop();
}

/// <summary>
/// Evaluates worker gameplay readiness and executes graceful drain against an authoritative <see cref="WorldRuntime"/>.
/// A worker is never <see cref="IsGameplayReady"/> merely because its process is alive or its RPC port is open;
/// it must pass manifest/schema/content checks, hold a matching ownership lease (when fenced), have started its
/// feature queues, and complete a real command roundtrip on the running <see cref="WorldRuntime"/> thread.
/// </summary>
public sealed class ClusterWorkerReadinessProbe : IClusterNodeLifecycle
{
    private readonly WorldRuntime _world;
    private readonly Lock _gate = new();
    private volatile ClusterNodeState _state = ClusterNodeState.Starting;
    private long _ownerEpoch = 1;

    public ClusterWorkerReadinessProbe(ClusterNodeManifest manifest, WorldRuntime world, long initialOwnerEpoch = 1)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(world);
        if (initialOwnerEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialOwnerEpoch), "initialOwnerEpoch must be positive");
        }

        Manifest = manifest;
        _world = world;
        _ownerEpoch = initialOwnerEpoch;
    }

    public ClusterNodeManifest Manifest { get; }

    public ClusterNodeState State => _state;

    public bool IsLive => _state != ClusterNodeState.Stopped;

    public bool IsGameplayReady => _state == ClusterNodeState.Active && !_world.IsStopped;

    public long CurrentOwnerEpoch => Interlocked.Read(ref _ownerEpoch);

    /// <summary>Updates the active ownership epoch (or invalidates readiness if the epoch is revoked).</summary>
    public void UpdateOwnerEpoch(long newEpoch)
    {
        if (newEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newEpoch), "newEpoch must be positive");
        }

        Interlocked.Exchange(ref _ownerEpoch, newEpoch);
    }

    /// <summary>
    /// Verifies manifest validity, optional cluster peer manifest compatibility, feature queue startup,
    /// optional durable character ownership token, and a real tick roundtrip on <see cref="WorldRuntime"/>
    /// before transitioning from <see cref="ClusterNodeState.Starting"/> to <see cref="ClusterNodeState.Active"/>.
    /// </summary>
    public async Task VerifyAndMarkReadyAsync(
        ClusterNodeManifest? expectedClusterManifest = null,
        bool requireOwnershipToken = false,
        bool featureQueuesStarted = true,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state != ClusterNodeState.Starting)
            {
                throw new InvalidOperationException($"cannot mark node ready from state {_state}");
            }
        }

        if (!Manifest.IsValid(out string? invalidReason))
        {
            throw new InvalidOperationException($"worker manifest is invalid: {invalidReason}");
        }

        if (expectedClusterManifest is not null && !expectedClusterManifest.TryAdmit(Manifest, out string? rejectionReason))
        {
            throw new InvalidOperationException($"worker rejected by cluster manifest check: {rejectionReason}");
        }

        if (!featureQueuesStarted)
        {
            throw new InvalidOperationException("worker feature and persistence queues have not started");
        }

        CharacterOwnershipToken? token = _world.CharacterOwnership;
        if (requireOwnershipToken)
        {
            if (token is null)
            {
                throw new InvalidOperationException("fenced worker requires an active CharacterOwnershipToken before marking gameplay-ready");
            }

            if (token.Incarnation != Manifest.NodeIncarnation)
            {
                throw new InvalidOperationException(
                    $"ownership token incarnation {token.Incarnation} does not match manifest incarnation {Manifest.NodeIncarnation}");
            }

            if (token.Epoch <= 0)
            {
                throw new InvalidOperationException($"ownership token epoch ({token.Epoch}) must be positive");
            }

            Interlocked.Exchange(ref _ownerEpoch, token.Epoch);
        }
        else if (token is not null && token.Epoch > 0)
        {
            Interlocked.Exchange(ref _ownerEpoch, token.Epoch);
        }

        if (_world.IsStopped)
        {
            throw new InvalidOperationException("cannot mark worker gameplay-ready when WorldRuntime is stopped");
        }

        // Require a real command roundtrip executed on the running WorldRuntime simulation thread.
        bool executedOnWorldTick = await _world.InvokeAsync(
            () => !_world.IsStopped && _world.IsWorldThread && string.Equals(Thread.CurrentThread.Name, "world", StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);

        if (!executedOnWorldTick)
        {
            throw new InvalidOperationException("WorldRuntime command roundtrip did not execute on an active world simulation thread");
        }

        lock (_gate)
        {
            if (_state != ClusterNodeState.Starting)
            {
                throw new InvalidOperationException($"worker state changed to {_state} while awaiting readiness roundtrip");
            }

            _state = ClusterNodeState.Active;
        }
    }

    /// <summary>
    /// Transitions the node from <see cref="ClusterNodeState.Active"/> (or <see cref="ClusterNodeState.Starting"/>)
    /// to <see cref="ClusterNodeState.Draining"/>, stops advertising gameplay readiness so no new sessions are admitted,
    /// awaits a full <see cref="WorldRuntime"/> tick barrier plus optional persistence flush callback, and transitions
    /// to <see cref="ClusterNodeState.Drained"/>.
    /// </summary>
    public async Task BeginDrainAsync(
        Func<CancellationToken, ValueTask>? flushCallback = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state == ClusterNodeState.Stopped)
            {
                throw new InvalidOperationException("cannot drain a stopped node");
            }

            if (_state == ClusterNodeState.Drained)
            {
                return;
            }

            _state = ClusterNodeState.Draining;
        }

        if (!_world.IsStopped)
        {
            await _world.InvokeAsync(() => true, cancellationToken).ConfigureAwait(false);
        }

        if (flushCallback is not null)
        {
            await flushCallback(cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_state == ClusterNodeState.Draining)
            {
                _state = ClusterNodeState.Drained;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _state = ClusterNodeState.Stopped;
        }
    }
}
